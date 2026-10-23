// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Xiaomi;

internal sealed partial class XiaomiDeviceController
{
    private static readonly ushort[] FeatureIds = [11, 7, 4, 29, 47, 161];
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Dictionary<ushort, byte[]> _configValues = [];

    private async Task ReadFeaturesAsync(CancellationToken ct)
    {
        var request = new XiaomiFrame(XiaomiProtocol.KindRequest, XiaomiProtocol.OpGetConfig, NextSequence(), XiaomiConfigCodec.ReadPayload(FeatureIds));
        await RequestAsync(request, f => f.OpCode == XiaomiProtocol.OpGetConfig && XiaomiConfigCodec.TryParse(f.Payload, out _), RequestTimeout, ct).ConfigureAwait(false);
    }

    private void ApplyFeatures(XiaomiFrame frame)
    {
        if (frame.OpCode is not (XiaomiProtocol.OpGetConfig or XiaomiProtocol.OpNotifyConfig)
            || !XiaomiConfigCodec.TryParse(frame.Payload, out var values)) return;
        foreach (var (id, value) in values)
            ApplyConfig(id, value, frame.OpCode == XiaomiProtocol.OpNotifyConfig ? StateSource.DeviceNotification : StateSource.QueryResponse);
    }

    private void ApplyConfig(ushort id, byte[] value, StateSource source)
    {
        bool valid = id switch
        {
            11 => value.Length == 2 && value[0] <= 2 && value[1] <= 31,
            7 => value.Length == 1 && value[0] <= 31,
            4 or 47 => value.Length == 1 && value[0] <= 1,
            29 => value.Length == 1,
            161 => value.Length == 4 && value[3] == 1 && value[2] == 10 && value[0] >= 1 && value[0] <= 10,
            _ => false,
        };
        if (!valid) return;
        lock (_gate) { _configValues[id] = value.ToArray(); _profile = _profile.WithConfig(id); }
        var profile = _profile;
        Store.TryUpdate(DeviceId, s =>
        {
            var next = s with { Profile = profile, LastUpdateSource = source };
            return id switch
            {
                11 => next with { NoiseControl = Observed.From(ParseNoise(value), Now, source) },
                7 => next with { Equalizer = Observed.From(new EqualizerState($"xiaomi-{value[0]}", value[0]), Now, source) },
                4 => next with { Toggles = s.Toggles.SetItem(DeviceToggle.DualDeviceConnection, Observed.From(value[0] == 1, Now, source)) },
                47 => next with { Toggles = s.Toggles.SetItem(DeviceToggle.GamingMode, Observed.From(value[0] == 1, Now, source)) },
                29 => next with { Spatial = Observed.From((value[0] & 1) == 0 ? SpatialAudioMode.Off : (value[0] & 8) != 0 ? SpatialAudioMode.HeadTracking : SpatialAudioMode.Fixed, Now, source) },
                161 => next with { PromptVolume = Observed.From((int)value[0], Now, source) },
                _ => next,
            };
        });
    }

    private NoiseControlState ParseNoise(byte[] value)
    {
        var mode = value[0] switch { 1 => NoiseControlMode.NoiseCancellation, 2 => NoiseControlMode.Transparency, _ => NoiseControlMode.Off };
        string id = $"{(value[0] == 1 ? "nc" : "ambient")}-{value[1]}";
        return new(mode, _profile.NoiseControlOptions.Any(o => o.Mode == mode && o.LevelId == id) ? id : null);
    }

    private async Task<byte[]?> ReadValueAsync(ushort id, CancellationToken ct)
    {
        var request = new XiaomiFrame(XiaomiProtocol.KindRequest, XiaomiProtocol.OpGetConfig, NextSequence(), XiaomiConfigCodec.ReadPayload(id));
        var frame = await RequestAsync(request, f => f.OpCode == XiaomiProtocol.OpGetConfig
            && XiaomiConfigCodec.TryParse(f.Payload, out var configs) && configs.ContainsKey(id), RequestTimeout, ct).ConfigureAwait(false);
        return frame is not null && XiaomiConfigCodec.TryParse(frame.Payload, out var values) && values.TryGetValue(id, out var value) ? value : null;
    }

    private async Task<CommandResult> WriteConfirmedAsync(ushort id, byte[] value, CancellationToken ct)
    {
        if (!_profile.HasConfig(id)) return NotSupported($"Config {id} has not been confirmed on this device.");
        var request = new XiaomiFrame(XiaomiProtocol.KindRequest, XiaomiProtocol.OpSetConfig, NextSequence(), XiaomiConfigCodec.WritePayload(id, value));
        if (!await WriteAsync(request.Encode(), ct).ConfigureAwait(false)) return new(CommandStatus.TransportError);
        var actual = await ReadValueAsync(id, ct).ConfigureAwait(false);
        if (Store.Current.Device?.DeviceId != DeviceId) return CommandResult.NotConnected;
        var result = actual is null ? new CommandResult(CommandStatus.Timeout, "No fresh readback after write.")
            : actual.AsSpan().SequenceEqual(value) ? CommandResult.Ok : new CommandResult(CommandStatus.Rejected, "The device kept a different value.");
        if (result.IsSuccess) ApplyConfig(id, actual!, StateSource.CommandConfirmation);
        Store.TryUpdate(DeviceId, s => s with { LastError = result.IsSuccess ? null : new DeviceError(result.Detail!, result.Status, Now) });
        return result;
    }

    private async Task<CommandResult> SerializedAsync(Func<CancellationToken, Task<CommandResult>> action, CancellationToken ct)
    {
        bool entered = false;
        try
        {
            await _operations.WaitAsync(ct).ConfigureAwait(false); entered = true;
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            return await action(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(CommandStatus.Cancelled); }
        finally { if (entered) _operations.Release(); }
    }

    public override Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken) => SerializedAsync(ct =>
        toggle switch
        {
            DeviceToggle.GamingMode => SetGamingModeCoreAsync(toggle, enabled, ct),
            DeviceToggle.DualDeviceConnection => WriteConfirmedAsync(4, [enabled ? (byte)1 : (byte)0], ct),
            _ => Task.FromResult(NotSupported(toggle.ToString())),
        }, cancellationToken);

    public override Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken) => SerializedAsync(async ct =>
    {
        if (!_profile.NoiseControlOptions.Any(o => o.Matches(target))) return NotSupported("This noise option is not published for the connected model.");
        byte mode = target.Mode switch { NoiseControlMode.NoiseCancellation => 1, NoiseControlMode.Transparency => 2, _ => 0 };
        byte level = 0;
        if (target.LevelId is { } id && !byte.TryParse(id[(id.LastIndexOf('-') + 1)..], out level)) return NotSupported("Unknown noise level.");
        var current = await ReadValueAsync(11, ct).ConfigureAwait(false);
        if (current is null || current.Length != 2) return new CommandResult(CommandStatus.Timeout);
        if (target.LevelId is null)
        {
            if (current[0] == mode) level = current[1];
            else if (_profile.NoiseControlOptions.FirstOrDefault(o => o.Mode == target.Mode && o.LevelId is not null)?.LevelId is { } first)
                byte.TryParse(first[(first.LastIndexOf('-') + 1)..], out level);
        }
        return await WriteConfirmedAsync(11, [mode, level], ct).ConfigureAwait(false);
    }, cancellationToken);

    public override Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken) => SerializedAsync(ct =>
        _profile.EqualizerPresets.Any(p => p.Id == presetId) && byte.TryParse(presetId[7..], out var id)
            ? WriteConfirmedAsync(7, [id], ct) : Task.FromResult(NotSupported("EQ preset is not listed for this model.")), cancellationToken);

    public override Task<CommandResult> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken = default) => SerializedAsync(async ct =>
    {
        if (mode is not (SpatialAudioMode.Off or SpatialAudioMode.Fixed or SpatialAudioMode.HeadTracking)) return NotSupported("Spatial mode");
        var current = await ReadValueAsync(29, ct).ConfigureAwait(false);
        if (current is null || current.Length != 1) return NotSupported("Spatial audio was not reported by the device.");
        // Preserve quality/latency preference, virtual-surround and reserved bits.
        byte value = (byte)(current[0] & ~9);
        if (mode != SpatialAudioMode.Off) value |= 1;
        if (mode == SpatialAudioMode.HeadTracking) value |= 8;
        return await WriteConfirmedAsync(29, [value], ct).ConfigureAwait(false);
    }, cancellationToken);

    public override Task<CommandResult> SetPromptVolumeAsync(int level, CancellationToken cancellationToken = default) => SerializedAsync(async ct =>
    {
        if (level is < 1 or > 10) return NotSupported("Prompt volume must be 1–10.");
        if (!_profile.HasConfig(161)) return NotSupported("Prompt volume bounds are unknown.");
        var request = new XiaomiFrame(XiaomiProtocol.KindRequest, XiaomiProtocol.OpSetConfig, NextSequence(), XiaomiConfigCodec.WritePayload(161, [(byte)level]));
        if (!await WriteAsync(request.Encode(), ct).ConfigureAwait(false)) return new CommandResult(CommandStatus.TransportError);
        var actual = await ReadValueAsync(161, ct).ConfigureAwait(false);
        if (Store.Current.Device?.DeviceId != DeviceId) return CommandResult.NotConnected;
        var result = actual is not { Length: 4 } ? new CommandResult(CommandStatus.Timeout)
            : actual[0] != level ? new CommandResult(CommandStatus.Rejected) : CommandResult.Ok;
        if (result.IsSuccess) ApplyConfig(161, actual!, StateSource.CommandConfirmation);
        Store.TryUpdate(DeviceId, s => s with { LastError = result.IsSuccess ? null : new DeviceError(result.Detail ?? result.Status.ToString(), result.Status, Now) });
        return result;
    }, cancellationToken);
}
