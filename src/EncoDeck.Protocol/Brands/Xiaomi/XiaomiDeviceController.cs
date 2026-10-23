// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Xiaomi;

/// <summary>
/// Controller for Xiaomi / Redmi earbuds over the XIAOAI SPP channel: optional auth challenge,
/// battery, and low-latency (game) mode via the RCSP device-config channel.
/// </summary>
internal sealed partial class XiaomiDeviceController : BrandControllerBase
{
    /// <summary>The auth probe is short: models that do not require auth never answer it.</summary>
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromMilliseconds(1200);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private readonly XiaomiFrameDecoder _decoder = new();
    private readonly Lock _gate = new();
    private readonly List<Waiter> _waiters = [];
    private XiaomiDeviceProfile _profile;
    private byte _sequence = XiaomiProtocol.ConfigSequenceStart;

    public XiaomiDeviceController(IDuplexByteChannel channel, ControllerContext context, XiaomiDeviceProfile profile)
        : base(channel, context)
    {
        _profile = profile.ForDevice(context.Identity.Name);
    }

    /// <summary>True when the earbuds answered the auth challenge correctly.</summary>
    public bool IsAuthenticated { get; private set; }

    public XiaomiDeviceProfile Profile => _profile;

    private sealed record Waiter(Func<XiaomiFrame, bool> Accepts, TaskCompletionSource<XiaomiFrame> Completion);

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
        if (!await ReadBatteryAsync(cancellationToken).ConfigureAwait(false))
        {
            Logger.LogInformation("Xiaomi earbuds did not answer the battery query");
            return false;
        }
        await ReadFeaturesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public override Task<CommandResult> RefreshAsync(CancellationToken cancellationToken) => SerializedAsync(RefreshCoreAsync, cancellationToken);

    private async Task<CommandResult> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        bool battery = await ReadBatteryAsync(cancellationToken).ConfigureAwait(false);
        await ReadFeaturesAsync(cancellationToken).ConfigureAwait(false);
        return battery ? CommandResult.Ok : new CommandResult(CommandStatus.Timeout, "The earbuds did not answer the battery query.");
    }

    private async Task<CommandResult> SetGamingModeCoreAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        if (toggle != DeviceToggle.GamingMode || !_profile.HasGamingMode) return NotSupported(toggle.ToString());

        DeviceState? before = null;
        Store.TryUpdate(DeviceId, s =>
        {
            before = s;
            return s with
            {
                Toggles = s.Toggles.SetItem(toggle, Observed.From(enabled, Now, StateSource.Optimistic)),
                LastError = null,
            };
        });

        CommandResult result;
        try
        {
            var write = new XiaomiFrame(XiaomiProtocol.KindRequest, XiaomiProtocol.OpSetConfig, NextSequence(),
                XiaomiProtocol.SetConfigPayload(XiaomiProtocol.ConfigLowLatency, enabled ? (byte)1 : (byte)0));
            if (!await WriteAsync(write.Encode(), cancellationToken).ConfigureAwait(false))
            {
                result = new CommandResult(CommandStatus.TransportError, "Write failed.");
            }
            else
            {
                // The write acknowledgement format is unconfirmed, so confirm by reading the value back.
                byte? value = await ReadConfigAsync(XiaomiProtocol.ConfigLowLatency, cancellationToken).ConfigureAwait(false);
                result = value switch
                {
                    null => new CommandResult(CommandStatus.Timeout, "The earbuds did not report low-latency mode after the write."),
                    _ when (value != 0) == enabled => CommandResult.Ok,
                    _ => new CommandResult(CommandStatus.Rejected, "The earbuds kept the previous low-latency setting."),
                };
            }
        }
        catch (OperationCanceledException)
        {
            result = new CommandResult(CommandStatus.Cancelled);
        }

        Store.TryUpdate(DeviceId, s =>
        {
            if (result.IsSuccess)
            {
                return s with { Toggles = s.Toggles.SetItem(toggle, Observed.From(enabled, Now, StateSource.CommandConfirmation)), LastUpdateSource = StateSource.CommandConfirmation };
            }
            var next = s;
            // Roll back unless a read-back already replaced the optimistic value with the device's.
            if (s.Toggles.TryGetValue(toggle, out var current) && current.Source == StateSource.Optimistic)
            {
                next = before is not null && before.Toggles.TryGetValue(toggle, out var old)
                    ? s with { Toggles = s.Toggles.SetItem(toggle, old) }
                    : s with { Toggles = s.Toggles.Remove(toggle) };
            }
            return next with { LastError = new DeviceError(result.Detail ?? result.Status.ToString(), result.Status, Now) };
        });
        return result;
    }

    protected override void OnReceived(ReadOnlySpan<byte> data)
    {
        foreach (var raw in _decoder.Feed(data))
        {
            var frame = XiaomiConfigCodec.NormalizeResponse(raw);
            if (frame is null) continue;
            Logger.LogTrace("Xiaomi frame received: {Frame}", frame);
            Touch();
            Apply(frame);
            ApplyFeatures(frame);
            if (raw.OpCode == XiaomiProtocol.OpNotifyConfig && (raw.Kind & 0xC0) == 0xC0
                && XiaomiConfigCodec.TryParse(raw.Payload, out _))
                _ = WriteAsync(new XiaomiFrame((byte)(raw.Kind & 0x3F), raw.OpCode, 0, [raw.Sequence]).Encode(), CancellationToken.None);
            Waiter? match = null;
            lock (_gate)
            {
                match = _waiters.FirstOrDefault(w => w.Accepts(frame));
                if (match is not null) _waiters.Remove(match);
            }
            match?.Completion.TrySetResult(frame);
        }
    }

    private void Apply(XiaomiFrame frame)
    {
        if (XiaomiProtocol.TryParseBattery(frame, out var left, out var right, out var @case))
        {
            var now = Now;
            Store.TryUpdate(DeviceId, s => s with
            {
                Battery = new BatteryStatus(
                    left is null ? s.Battery.Left : Observed.From(new BatteryReading(left, null), now, StateSource.QueryResponse),
                    right is null ? s.Battery.Right : Observed.From(new BatteryReading(right, null), now, StateSource.QueryResponse),
                    @case is null ? s.Battery.Case : Observed.From(new BatteryReading(@case, null), now, StateSource.QueryResponse)),
                LastUpdateSource = StateSource.QueryResponse,
            });
        }
        else if (XiaomiProtocol.TryParseConfig(frame, out var configId, out var value)
            && configId == XiaomiProtocol.ConfigLowLatency && value <= 1)
        {
            var source = frame.OpCode == XiaomiProtocol.OpNotifyConfig ? StateSource.DeviceNotification : StateSource.QueryResponse;
            var now = Now;
            _profile = _profile.WithGamingMode(true);
            var profile = _profile;
            Store.TryUpdate(DeviceId, s => s with
            {
                Profile = profile,
                Toggles = s.Toggles.SetItem(DeviceToggle.GamingMode, Observed.From(value == 1, now, source)),
                LastUpdateSource = source,
            });
        }
    }

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        var random = XiaomiAuth.CreateRandomFactor();
        var challenge = new XiaomiFrame(XiaomiProtocol.KindRequest, XiaomiProtocol.OpTargetInfo, XiaomiProtocol.AuthSequence, XiaomiAuth.BuildAuthCheckPayload(random));
        var answer = await RequestAsync(challenge, f => f.OpCode == XiaomiProtocol.OpTargetInfo && f.Payload.Length == 1 + XiaomiAuth.AuthRandomLength, AuthTimeout, cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            Logger.LogInformation("Xiaomi auth probe got no answer; continuing without auth");
            return;
        }
        if (!XiaomiAuth.TryParseAuthCheckResponse(answer.Payload, out _, out var result) || !XiaomiAuth.VerifyAuthResponse(random, result))
        {
            Logger.LogWarning("Xiaomi auth answer did not verify; later commands may be refused");
            return;
        }
        IsAuthenticated = true;
        var report = new XiaomiFrame(XiaomiProtocol.KindAuthResult, XiaomiProtocol.OpAuthResult, 0x00, XiaomiProtocol.AuthResultPayload.ToArray());
        await WriteAsync(report.Encode(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ReadBatteryAsync(CancellationToken cancellationToken)
    {
        var query = new XiaomiFrame(XiaomiProtocol.KindRequest, XiaomiProtocol.OpTargetInfo, XiaomiProtocol.BatterySequence, XiaomiProtocol.BatteryQueryPayload.ToArray());
        var answer = await RequestAsync(query, f => XiaomiProtocol.TryParseBattery(f, out _, out _, out _), RequestTimeout, cancellationToken).ConfigureAwait(false);
        return answer is not null;
    }

    private async Task<byte?> ReadConfigAsync(ushort configId, CancellationToken cancellationToken)
    {
        var query = new XiaomiFrame(XiaomiProtocol.KindRequest, XiaomiProtocol.OpGetConfig, NextSequence(), XiaomiProtocol.GetConfigPayload(configId));
        var answer = await RequestAsync(
            query,
            f => f.OpCode == XiaomiProtocol.OpGetConfig && XiaomiProtocol.TryParseConfig(f, out var id, out _) && id == configId,
            RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        return answer is not null && XiaomiProtocol.TryParseConfig(answer, out _, out var value) ? value : null;
    }

    /// <summary>Sends <paramref name="frame"/> and waits for the first received frame <paramref name="accepts"/> matches; null on timeout or write failure.</summary>
    private async Task<XiaomiFrame?> RequestAsync(XiaomiFrame frame, Func<XiaomiFrame, bool> accepts, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waiter = new Waiter(f => f.Sequence == frame.Sequence && accepts(f), new TaskCompletionSource<XiaomiFrame>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_gate) _waiters.Add(waiter);
        try
        {
            if (!await WriteAsync(frame.Encode(), cancellationToken).ConfigureAwait(false)) return null;
            return await waiter.Completion.Task.WaitAsync(timeout, Context.Time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            lock (_gate) _waiters.Remove(waiter);
        }
    }

    private byte NextSequence()
    {
        lock (_gate)
        {
            byte value = _sequence;
            _sequence = _sequence == byte.MaxValue ? XiaomiProtocol.ConfigSequenceStart : (byte)(_sequence + 1);
            return value;
        }
    }
}
