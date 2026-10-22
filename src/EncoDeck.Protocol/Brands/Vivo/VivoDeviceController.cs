// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Vivo;

/// <summary>
/// Drives vivo / iQOO earbuds over the vivo GAIA RFCOMM channel: handshake, notification
/// registration, battery / wear / noise / sound effect / game mode / firmware reads, and the
/// writes the model declares. Writes are only reported successful once a device report shows the
/// new value (a SET echo alone is not trusted); otherwise the value is re-queried.
/// </summary>
public sealed class VivoDeviceController : BrandControllerBase
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(2);

    private readonly VivoDeviceProfile _profile;
    private readonly VivoFrameDecoder _decoder = new();
    private readonly Lock _waitersGate = new();
    private readonly List<Waiter> _waiters = [];

    public VivoDeviceController(IDuplexByteChannel channel, ControllerContext context, VivoDeviceProfile profile)
        : base(channel, context)
    {
        _profile = profile;
    }

    private VivoModel Model => _profile.Model;

    private sealed record Waiter(Func<VivoFrame, bool> Match, TaskCompletionSource<VivoFrame> Completion);

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        var hello = await RequestAsync(VivoCommands.Handshake, [], f => f.Command == VivoCommands.HandshakeResponse, HandshakeTimeout, cancellationToken).ConfigureAwait(false);
        if (hello is null)
        {
            Logger.LogInformation("vivo handshake not answered");
            return false;
        }

        // Fire-and-forget registration; the earbuds then push reports on their own.
        foreach (ushort command in VivoCommands.RegisterNotifications)
        {
            if (!await SendAsync(command, [], cancellationToken).ConfigureAwait(false)) return false;
        }

        await QueryAllAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public override async Task<CommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        if (Closed.IsCompleted) return CommandResult.NotConnected;
        bool any = await QueryAllAsync(cancellationToken).ConfigureAwait(false);
        return any ? CommandResult.Ok : new CommandResult(CommandStatus.Timeout, "The earbuds did not answer.");
    }

    private async Task<bool> QueryAllAsync(CancellationToken ct)
    {
        bool any = await QueryAsync(VivoCommands.QueryBattery, [], VivoCommands.ReportBattery, ct).ConfigureAwait(false) is not null;
        if (!Model.Headphones)
        {
            any |= await QueryAsync(VivoCommands.QueryWearState, [], VivoCommands.ReportWearState, ct).ConfigureAwait(false) is not null;
        }
        if (Supported(DeviceCapability.NoiseCancellation))
        {
            any |= await QueryAsync(VivoCommands.QueryNoiseMode, Model.NoiseQueryPayload, VivoCommands.ReportNoiseMode, ct).ConfigureAwait(false) is not null;
        }
        if (Supported(DeviceCapability.Equalizer))
        {
            any |= await QueryAsync(VivoCommands.QueryAudioEffect, [], VivoCommands.ReportAudioEffect, ct).ConfigureAwait(false) is not null;
        }
        if (Supported(DeviceCapability.GamingMode))
        {
            any |= await QueryAsync(VivoCommands.QueryLowLatency, [], VivoCommands.ReportLowLatency, ct).ConfigureAwait(false) is not null;
        }
        if (Store.Current.Firmware is null)
        {
            any |= await QueryAsync(VivoCommands.QueryFirmware, [], VivoCommands.ReportFirmware, ct).ConfigureAwait(false) is not null;
        }
        return any;
    }

    public override async Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (!Supported(DeviceCapability.NoiseCancellation) || !_profile.NoiseControlOptions.Any(o => o.Matches(target)))
        {
            return NotSupported("This noise control option");
        }
        byte mode = VivoParsers.NoiseModeByte(target.Mode);
        byte[] payload = [mode, .. Model.NoiseSetSuffix];
        var result = await WriteConfirmedAsync(
            VivoCommands.SetNoiseMode,
            payload,
            f => f.Command == VivoCommands.ReportNoiseMode && VivoParsers.TryParseNoise(f.Payload, out var m) && m == target.Mode,
            VivoCommands.QueryNoiseMode,
            Model.NoiseQueryPayload,
            VivoCommands.ReportNoiseMode,
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(new NoiseControlState(target.Mode), Now, StateSource.CommandConfirmation), LastUpdateSource = StateSource.CommandConfirmation });
        }
        return result;
    }

    public override async Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        if (!Supported(DeviceCapability.Equalizer)
            || !VivoCatalog.TryParsePresetId(presetId, out byte effect)
            || !Model.AudioEffectIds.Contains(effect))
        {
            return NotSupported("This equalizer preset");
        }
        var result = await WriteConfirmedAsync(
            VivoCommands.SetAudioEffect,
            [effect],
            f => f.Command is VivoCommands.AckAudioEffect or VivoCommands.ReportAudioEffect
                && VivoParsers.TryParseValue(f.Payload, out byte v) && VivoCatalog.NormalizeReportedEffect(v) == effect,
            VivoCommands.QueryAudioEffect,
            [],
            VivoCommands.ReportAudioEffect,
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            Store.TryUpdate(DeviceId, s => s with { Equalizer = Observed.From(new EqualizerState(presetId, effect), Now, StateSource.CommandConfirmation), LastUpdateSource = StateSource.CommandConfirmation });
        }
        return result;
    }

    public override async Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        if (toggle != DeviceToggle.GamingMode || !Supported(DeviceCapability.GamingMode)) return NotSupported(toggle.ToString());
        byte value = enabled ? (byte)1 : (byte)0;
        var result = await WriteConfirmedAsync(
            VivoCommands.SetLowLatency,
            [value],
            f => f.Command is VivoCommands.AckLowLatency or VivoCommands.ReportLowLatency
                && VivoParsers.TryParseValue(f.Payload, out byte v) && v == value,
            VivoCommands.QueryLowLatency,
            [],
            VivoCommands.ReportLowLatency,
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(toggle, Observed.From(enabled, Now, StateSource.CommandConfirmation)), LastUpdateSource = StateSource.CommandConfirmation });
        }
        return result;
    }

    public override async Task<CommandResult> FindEarbudsAsync(bool ring, CancellationToken cancellationToken = default)
    {
        if (!Supported(DeviceCapability.FindMyEarbuds)) return NotSupported("Find earbuds");
        byte value = ring ? (byte)1 : (byte)0;
        var ack = await RequestAsync(VivoCommands.SetFindDevice, [value], f => f.Command == VivoCommands.AckFindDevice, ConfirmTimeout, cancellationToken).ConfigureAwait(false);
        CommandResult result;
        if (ack is null)
        {
            result = Closed.IsCompleted ? new CommandResult(CommandStatus.TransportError) : new CommandResult(CommandStatus.Timeout, "The earbuds did not acknowledge.");
        }
        else if (!VivoParsers.TryParseValue(ack.Payload, out byte state) || (state != 0) != ring)
        {
            result = new CommandResult(CommandStatus.Rejected, "The earbuds refused (are they in the case?).");
        }
        else
        {
            result = CommandResult.Ok;
        }
        bool active = ring && result.IsSuccess;
        Store.TryUpdate(DeviceId, s => s with { IsFindingEarbuds = active });
        return result;
    }

    /// <summary>
    /// Sends a write, then waits for a report that shows the new value; when none arrives, re-queries
    /// once. The device often reports the old value right after a write, so a mismatching read-back
    /// is only a rejection after the confirmation window.
    /// </summary>
    private async Task<CommandResult> WriteConfirmedAsync(
        ushort setCommand,
        byte[] payload,
        Func<VivoFrame, bool> confirms,
        ushort queryCommand,
        byte[] queryPayload,
        ushort reportCommand,
        CancellationToken ct)
    {
        var waiter = Register(confirms);
        try
        {
            if (!await SendAsync(setCommand, payload, ct).ConfigureAwait(false)) return new CommandResult(CommandStatus.TransportError);
            if (await AwaitAsync(waiter, ConfirmTimeout, ct).ConfigureAwait(false) is not null) return CommandResult.Ok;
        }
        finally
        {
            Unregister(waiter);
        }

        var readBack = await RequestAsync(queryCommand, queryPayload, f => f.Command == reportCommand, QueryTimeout, ct).ConfigureAwait(false);
        if (readBack is null)
        {
            return Closed.IsCompleted ? new CommandResult(CommandStatus.TransportError) : new CommandResult(CommandStatus.Timeout, "The earbuds did not confirm the change.");
        }
        return confirms(readBack) ? CommandResult.Ok : new CommandResult(CommandStatus.Rejected, "The earbuds kept the previous setting.");
    }

    private async Task<VivoFrame?> QueryAsync(ushort command, byte[] payload, ushort report, CancellationToken ct) =>
        await RequestAsync(command, payload, f => f.Command == report, QueryTimeout, ct).ConfigureAwait(false);

    private async Task<VivoFrame?> RequestAsync(ushort command, byte[] payload, Func<VivoFrame, bool> match, TimeSpan timeout, CancellationToken ct)
    {
        var waiter = Register(match);
        try
        {
            if (!await SendAsync(command, payload, ct).ConfigureAwait(false)) return null;
            return await AwaitAsync(waiter, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            Unregister(waiter);
        }
    }

    private async Task<VivoFrame?> AwaitAsync(Waiter waiter, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await waiter.Completion.Task.WaitAsync(timeout, Context.Time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private Task<bool> SendAsync(ushort command, byte[] payload, CancellationToken ct)
    {
        var frame = new VivoFrame(command, payload);
        Logger.LogDebug("vivo TX {Frame}", frame);
        return WriteAsync(frame.Encode(Model.GaiaVersion), ct);
    }

    private Waiter Register(Func<VivoFrame, bool> match)
    {
        var waiter = new Waiter(match, new TaskCompletionSource<VivoFrame>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_waitersGate) _waiters.Add(waiter);
        return waiter;
    }

    private void Unregister(Waiter waiter)
    {
        lock (_waitersGate) _waiters.Remove(waiter);
    }

    protected override void OnReceived(ReadOnlySpan<byte> data)
    {
        var frames = _decoder.Feed(data);
        if (frames.Count == 0) return;
        Touch();
        foreach (var frame in frames)
        {
            Logger.LogDebug("vivo RX {Frame}", frame);
            Apply(frame);
            Waiter[] matched;
            lock (_waitersGate) matched = _waiters.Where(w => w.Match(frame)).ToArray();
            foreach (var w in matched) w.Completion.TrySetResult(frame);
        }
    }

    /// <summary>Applies a received report to the store. Source is QueryResponse when a query is waiting for it.</summary>
    private void Apply(VivoFrame frame)
    {
        StateSource source;
        lock (_waitersGate) source = _waiters.Count > 0 ? StateSource.QueryResponse : StateSource.DeviceNotification;
        var now = Now;
        var p = frame.Payload;
        switch (frame.Command)
        {
            case VivoCommands.ReportBattery when VivoParsers.TryParseBattery(p, out var l, out var r, out var c):
                Store.TryUpdate(DeviceId, s => s with
                {
                    Battery = new BatteryStatus(Wrap(l), Wrap(r), Wrap(c)),
                    LastUpdateSource = source,
                });
                break;
            case VivoCommands.ReportWearState when VivoParsers.TryParseWear(p, out var wear):
                Store.TryUpdate(DeviceId, s => s with { Wear = Observed.From(wear, now, source), LastUpdateSource = source });
                break;
            case VivoCommands.ReportNoiseMode when VivoParsers.TryParseNoise(p, out var mode):
                Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(new NoiseControlState(mode), now, source), LastUpdateSource = source });
                break;
            case VivoCommands.ReportAudioEffect or VivoCommands.AckAudioEffect when VivoParsers.TryParseValue(p, out byte effect):
                byte? shown = VivoCatalog.NormalizeReportedEffect(effect);
                string? preset = shown is { } id && Model.AudioEffectIds.Contains(id) ? VivoCatalog.PresetId(id) : null;
                Store.TryUpdate(DeviceId, s => s with { Equalizer = Observed.From(new EqualizerState(preset, effect), now, source), LastUpdateSource = source });
                break;
            case VivoCommands.ReportLowLatency or VivoCommands.AckLowLatency when VivoParsers.TryParseValue(p, out byte game) && game <= 1:
                Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(DeviceToggle.GamingMode, Observed.From(game == 1, now, source)), LastUpdateSource = source });
                break;
            case VivoCommands.ReportFirmware when VivoParsers.ParseFirmware(p) is { } version:
                var info = new FirmwareInfo(version, Model.ProjectName, new Dictionary<string, string>());
                Store.TryUpdate(DeviceId, s => s with { Firmware = Observed.From(info, now, StateSource.QueryResponse) });
                break;
            case VivoCommands.PeerTimeRequest:
                _ = AnswerTimeAsync();
                break;
        }

        Observed<BatteryReading>? Wrap(BatteryReading? reading) => reading is null ? null : Observed.From(reading, now, source);
    }

    /// <summary>The earbuds ask for the local time: <c>[century][year%100][month][day][hour][minute][second]</c>.</summary>
    private async Task AnswerTimeAsync()
    {
        var t = Context.Time.GetLocalNow();
        byte[] payload = [(byte)(t.Year / 100), (byte)(t.Year % 100), (byte)t.Month, (byte)t.Day, (byte)t.Hour, (byte)t.Minute, (byte)t.Second];
        try
        {
            await SendAsync(VivoCommands.HostTimeResponse, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to answer the vivo time request");
        }
    }

    private bool Supported(DeviceCapability capability) =>
        _profile.Capabilities.IsAvailable(capability, Context.AllowExperimental());
}
