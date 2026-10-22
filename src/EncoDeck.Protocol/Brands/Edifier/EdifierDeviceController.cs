// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Edifier;

/// <summary>
/// Controller for EDIFIER headphones over SPP: battery and noise control (off / ANC / transparency).
/// The device has no handshake and no unsolicited notifications, so state is polled.
/// </summary>
public sealed class EdifierDeviceController : BrandControllerBase
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly EdifierFrameDecoder _decoder = new();
    private readonly Lock _gate = new();
    private readonly List<(byte Command, TaskCompletionSource<EdifierFrame> Waiter)> _waiters = [];
    private readonly CancellationTokenSource _pollStop = new();
    private Task? _pollTask;

    public EdifierDeviceController(IDuplexByteChannel channel, ControllerContext context)
        : base(channel, context)
    {
    }

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        bool battery = await RefreshBatteryAsync(cancellationToken).ConfigureAwait(false);
        bool noise = await RefreshNoiseAsync(cancellationToken).ConfigureAwait(false) is not null;
        if (!battery && !noise)
        {
            // An open RFCOMM channel that answers nothing is not speaking this protocol.
            Logger.LogInformation("EDIFIER device answered neither battery nor noise query");
            return false;
        }
        _pollTask ??= Task.Run(() => PollAsync(_pollStop.Token), CancellationToken.None);
        return true;
    }

    protected override void OnReceived(ReadOnlySpan<byte> data)
    {
        IReadOnlyList<EdifierFrame> frames;
        lock (_gate) frames = _decoder.Feed(data);
        foreach (var frame in frames)
        {
            Touch();
            TaskCompletionSource<EdifierFrame>? waiter = null;
            lock (_gate)
            {
                int index = _waiters.FindIndex(w => w.Command == frame.Command);
                if (index >= 0)
                {
                    waiter = _waiters[index].Waiter;
                    _waiters.RemoveAt(index);
                }
            }
            if (waiter is not null)
            {
                waiter.TrySetResult(frame);
            }
            else
            {
                // Late or unsolicited answer: still apply what it says.
                Apply(frame, StateSource.DeviceNotification);
            }
        }
    }

    public override async Task<CommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        bool battery = await RefreshBatteryAsync(cancellationToken).ConfigureAwait(false);
        bool noise = await RefreshNoiseAsync(cancellationToken).ConfigureAwait(false) is not null;
        return battery && noise ? CommandResult.Ok : new CommandResult(CommandStatus.Timeout, "Device did not answer every query.");
    }

    public override async Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        byte? mode = target.Mode switch
        {
            NoiseControlMode.Off => EdifierConstants.NoiseOff,
            NoiseControlMode.NoiseCancellation => EdifierConstants.NoiseAnc,
            NoiseControlMode.Transparency => EdifierConstants.NoiseTransparency,
            _ => null,
        };
        if (mode is null || target.LevelId is not null) return NotSupported($"Noise mode {target.Mode}/{target.LevelId}");
        if (Closed.IsCompleted) return CommandResult.NotConnected;

        var requested = new NoiseControlState(target.Mode);
        var pending = new PendingCommand(Guid.NewGuid(), "noise-control", Now);
        DeviceState? before = null;
        Store.TryUpdate(DeviceId, s =>
        {
            before = s;
            return s with
            {
                NoiseControl = Observed.From(requested, Now, StateSource.Optimistic),
                PendingCommands = s.PendingCommands.Add(pending),
                LastError = null,
            };
        });

        CommandResult result;
        try
        {
            var ack = await RequestAsync(EdifierConstants.SetNoiseMode, [mode.Value], cancellationToken).ConfigureAwait(false);
            // Even without an ack the write may have landed: trust a matching read-back.
            var actual = await RefreshNoiseAsync(cancellationToken).ConfigureAwait(false);
            if (actual is null)
            {
                result = new CommandResult(CommandStatus.Timeout, ack is null ? "noise-control" : "Read-back");
            }
            else
            {
                result = actual == requested
                    ? CommandResult.Ok
                    : new CommandResult(ack is null ? CommandStatus.Timeout : CommandStatus.Rejected, "Device reports a different noise mode.");
            }
        }
        catch (OperationCanceledException)
        {
            result = new CommandResult(CommandStatus.Cancelled);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Noise control command failed");
            result = new CommandResult(CommandStatus.TransportError, ex.Message);
        }

        Store.TryUpdate(DeviceId, s =>
        {
            var next = s with { PendingCommands = s.PendingCommands.RemoveAll(p => p.Id == pending.Id) };
            if (result.IsSuccess) return next;
            if (before is not null && next.NoiseControl?.Source == StateSource.Optimistic) next = next with { NoiseControl = before.NoiseControl };
            return next with { LastError = new DeviceError(result.Detail ?? result.Status.ToString(), result.Status, Now) };
        });
        Logger.LogInformation("Command noise-control -> {Result}", result);
        return result;
    }

    private async Task<bool> RefreshBatteryAsync(CancellationToken cancellationToken)
    {
        var frame = await RequestAsync(EdifierConstants.QueryBattery, [], cancellationToken).ConfigureAwait(false);
        return frame is not null && Apply(frame, StateSource.QueryResponse);
    }

    /// <summary>Queries the noise mode; returns the parsed state, or null without a valid answer.</summary>
    private async Task<NoiseControlState?> RefreshNoiseAsync(CancellationToken cancellationToken)
    {
        var frame = await RequestAsync(EdifierConstants.QueryNoiseMode, [], cancellationToken).ConfigureAwait(false);
        if (frame is null || !Apply(frame, StateSource.QueryResponse)) return null;
        return ParseNoise(frame.Payload);
    }

    /// <summary>Applies a battery or noise answer to the store; returns whether it was valid.</summary>
    private bool Apply(EdifierFrame frame, StateSource source)
    {
        switch (frame.Command)
        {
            case EdifierConstants.QueryBattery when frame.Payload is [var percent, ..] && percent <= 100:
                // Over-ear/neckband: one reading for the whole headset, shown in the Left slot.
                var reading = Observed.From(new BatteryReading(percent, null), Now, source);
                Store.TryUpdate(DeviceId, s => s with { Battery = s.Battery with { Left = reading }, LastUpdateSource = source });
                return true;
            case EdifierConstants.QueryNoiseMode when ParseNoise(frame.Payload) is { } noise:
                Store.TryUpdate(DeviceId, s => s.NoiseControl?.Source == StateSource.Optimistic && source == StateSource.DeviceNotification
                    ? s
                    : s with { NoiseControl = Observed.From(noise, Now, source), LastUpdateSource = source });
                return true;
            default:
                return false;
        }
    }

    internal static NoiseControlState? ParseNoise(byte[] payload) => payload is [var mode, ..]
        ? mode switch
        {
            EdifierConstants.NoiseOff => new NoiseControlState(NoiseControlMode.Off),
            EdifierConstants.NoiseAnc => new NoiseControlState(NoiseControlMode.NoiseCancellation),
            EdifierConstants.NoiseTransparency => new NoiseControlState(NoiseControlMode.Transparency),
            _ => null,
        }
        : null;

    /// <summary>Sends a request and waits for the frame echoing its command, or null on timeout.</summary>
    private async Task<EdifierFrame?> RequestAsync(byte command, byte[] payload, CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource<EdifierFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _waiters.Add((command, waiter));
        try
        {
            if (!await WriteAsync(EdifierFrameCodec.Encode(command, payload), cancellationToken).ConfigureAwait(false)) return null;
            return await waiter.Task.WaitAsync(RequestTimeout, Context.Time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            lock (_gate) _waiters.RemoveAll(w => w.Waiter == waiter);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval, Context.Time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false) && !Closed.IsCompleted)
            {
                if (Store.Current.PendingCommands.Count > 0) continue;
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "EDIFIER polling stopped");
        }
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        await _pollStop.CancelAsync().ConfigureAwait(false);
        if (_pollTask is { } poll)
        {
            try
            {
                await poll.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
        _pollStop.Dispose();
    }
}
