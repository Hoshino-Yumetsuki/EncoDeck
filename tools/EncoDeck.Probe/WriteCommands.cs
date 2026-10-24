// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Oppo;

namespace EncoDeck.Probe;

/// <summary>
/// Explicit, opt-in write commands. Each one requires --confirm-write, accepts only values that have
/// been observed on real hardware, reads the state back after writing, and restores the original
/// state unless --no-restore is given.
/// </summary>
internal static class WriteCommands
{
    /// <summary>
    /// Noise-control protocol indices of the Enco X4: observed on hardware (0–8, 13) or listed for
    /// product 06EC10 in the official client's model configuration (11, 12, 14). Anything else is refused.
    /// </summary>
    private static readonly HashSet<int> ObservedNoiseIndices = [0, 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14];

    /// <summary>EQ preset protocol indices listed for the Enco X4 (0, 7, 2 also observed on hardware).</summary>
    private static readonly HashSet<int> KnownEqualizerPresets = [0, 1, 2, 3, 7];

    /// <summary>
    /// Experimental: the official client's "LE/BR switch" (game low-latency with LE Audio), query
    /// 0x0136 / set 0x0433. Prints the state, writes only with --state 0|1, then reads it back. Not
    /// restored. Windows LE Audio works with the switch off (X4, 2026-10-24).
    /// (0x0424 "setLeAudioAction" is only sent before firmware upgrades and is not a mode switch.)
    /// </summary>
    public static async Task<int> SetLeBrSwitchAsync(ProbeOptions options, CancellationToken ct)
    {
        int? state = options.Has("state") ? options.GetInt("state", -1) : null;
        if (state is not (null or 0 or 1))
        {
            Console.Error.WriteLine("Refused: --state must be 0 or 1.");
            return 2;
        }
        if (state is not null && !options.Has("confirm-write"))
        {
            Console.Error.WriteLine("Refused: 'le-br-switch --state' changes the Bluetooth audio mode. Re-run with --confirm-write.");
            return 2;
        }

        var trace = new ConsoleTrace(printRaw: true);
        var opened = await ProbeSession.OpenAsync(options, trace, ct);
        if (opened is null) return 4;
        var (session, _, _) = opened.Value;
        await using var _ = session;

        await session.RequestAsync(QueryCommands.StandardQueries[0].Request, cancellationToken: ct);
        async Task PrintStateAsync(string label)
        {
            var r = await session.RequestAsync(new OppoRequest(OppoCommand.QueryLeBrSwitch, []), cancellationToken: ct);
            if (r.Response is { } response && OppoFeatureParsers.TryParseLeAudioSwitch(response, out var s))
                Console.WriteLine($"{label}: state={(s.Enabled ? 1 : 0)} owner={(s.Owner ? 1 : 0)} reset={(s.ResetFlag ? 1 : 0)}");
            else
                Console.WriteLine($"{label}: {r.Status} {(r.Response is { } f ? ConsoleTrace.PayloadText(f) : r.Detail)}");
        }

        await PrintStateAsync("before");
        if (state is null) return 0;
        var set = await session.RequestAsync(new OppoRequest(OppoCommand.SetLeBrSwitch, [(byte)state]), TimeSpan.FromSeconds(5), ct);
        Console.WriteLine($"set 0x0433 [{state:X2}]: {set.Status} {(set.Response is { } s ? ConsoleTrace.PayloadText(s) : set.Detail)}");
        await Task.Delay(options.GetInt("dwell-ms", 3000), ct);
        await PrintStateAsync("after");
        return set.IsSuccess ? 0 : 5;
    }

    public static async Task<int> SetEqualizerAsync(ProbeOptions options, CancellationToken ct)
    {
        if (!options.Has("confirm-write"))
        {
            Console.Error.WriteLine("Refused: 'set-eq' changes a device setting. Re-run with --confirm-write.");
            return 2;
        }
        var presets = new List<byte>();
        foreach (var part in (options.Get("preset") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!byte.TryParse(part, out byte preset) || !KnownEqualizerPresets.Contains(preset))
            {
                Console.Error.WriteLine($"Refused: preset '{part}' is not a known preset ({string.Join(",", KnownEqualizerPresets.Order())}).");
                return 2;
            }
            presets.Add(preset);
        }
        if (presets.Count == 0)
        {
            Console.Error.WriteLine("Usage: set-eq --preset N[,N...] --confirm-write [--dwell-ms 4000] [--no-restore]");
            return 2;
        }

        var trace = new ConsoleTrace(printRaw: false) { Quiet = true };
        var opened = await ProbeSession.OpenAsync(options, trace, ct);
        if (opened is null) return 4;
        var (session, _, _) = opened.Value;
        await using var _ = session;
        await session.RequestAsync(QueryCommands.StandardQueries[0].Request, cancellationToken: ct);

        async Task<byte?> ReadAsync()
        {
            var r = await session.RequestAsync(new OppoRequest(OppoCommand.QueryEqualizer, []), cancellationToken: ct);
            return r.Response is { } f && OppoParsers.TryParseEqualizer(f, out byte p) ? p : null;
        }

        byte? original = await ReadAsync();
        Console.WriteLine($"Current preset: {original?.ToString() ?? "unknown"}");
        if (original is null) return 5;

        int dwell = Math.Clamp(options.GetInt("dwell-ms", 4000), 500, 30000);
        int failures = 0;
        var sequence = options.Has("no-restore") ? presets : [.. presets, original.Value];
        for (int i = 0; i < sequence.Count; i++)
        {
            byte preset = sequence[i];
            var result = await session.RequestAsync(new OppoRequest(OppoCommand.SetEqualizer, [preset]), TimeSpan.FromSeconds(3), ct);
            string ack = result.Response is { } r ? (r.Payload.Length > 0 ? r.Payload[0].ToString("X2") : "empty") : result.Status.ToString();
            await Task.Delay(400, ct);
            byte? readBack = await ReadAsync();
            bool restore = !options.Has("no-restore") && i == sequence.Count - 1;
            Console.WriteLine($"{(restore ? "restore" : $"step {i + 1}")}: set preset {preset} -> ack {ack} read-back {readBack?.ToString() ?? "unknown"}");
            if (ack != "00" || readBack != preset) failures++;
            if (i < sequence.Count - 1) await Task.Delay(dwell, ct);
        }
        return failures == 0 ? 0 : 5;
    }

    public static async Task<int> SetNoiseControlAsync(ProbeOptions options, CancellationToken ct)
    {
        if (!options.Has("confirm-write"))
        {
            Console.Error.WriteLine("Refused: 'set-anc' changes a device setting. Re-run with --confirm-write.");
            return 2;
        }
        string? list = options.Get("index");
        if (list is null)
        {
            Console.Error.WriteLine("Usage: set-anc --index N[,N...] --confirm-write [--dwell-ms 6000] [--no-restore]");
            return 2;
        }
        var indices = new List<int>();
        foreach (var part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out int index) || !ObservedNoiseIndices.Contains(index))
            {
                Console.Error.WriteLine($"Refused: index '{part}' is not in the set observed on hardware ({string.Join(",", ObservedNoiseIndices.Order())}).");
                return 2;
            }
            indices.Add(index);
        }

        var trace = new ConsoleTrace(printRaw: false) { Quiet = true };
        var opened = await ProbeSession.OpenAsync(options, trace, ct);
        if (opened is null) return 4;
        var (session, _, _) = opened.Value;
        await using var _ = session;

        var events = new List<(DateTimeOffset At, uint Bitmap)>();
        session.UnsolicitedFrame += (_, f) =>
        {
            if (OppoParsers.TryParseNoiseControlEvent(f, out uint bitmap) && f.Payload.Length > 1 && f.Payload[1] == 0x01)
            {
                lock (events) events.Add((DateTimeOffset.UtcNow, bitmap));
            }
        };

        await session.RequestAsync(QueryCommands.StandardQueries[0].Request, cancellationToken: ct);
        uint? original = await ReadModeAsync(session, ct);
        Console.WriteLine($"Current mode bitmap: {Format(original)}");
        if (original is null)
        {
            Console.Error.WriteLine("Could not read the current mode; aborting before any write.");
            return 5;
        }

        int dwell = Math.Clamp(options.GetInt("dwell-ms", 6000), 500, 30000);
        var start = DateTimeOffset.UtcNow;
        int failures = 0;
        for (int step = 0; step < indices.Count; step++)
        {
            int index = indices[step];
            var outcome = await SetAndVerifyAsync(session, index, ct);
            Console.WriteLine($"{(DateTimeOffset.UtcNow - start).TotalSeconds,6:F1}s step {step + 1}: set index {index,-2} -> ack {outcome.Ack,-12} read-back {Format(outcome.ReadBack)}");
            if (outcome.Ack != "00") failures++;
            if (step < indices.Count - 1) await Task.Delay(dwell, ct);
        }

        if (!options.Has("no-restore"))
        {
            int restoreIndex = LowestBit(original.Value);
            await Task.Delay(dwell, ct);
            var restored = await SetAndVerifyAsync(session, restoreIndex, ct);
            Console.WriteLine($"{(DateTimeOffset.UtcNow - start).TotalSeconds,6:F1}s restore: set index {restoreIndex} -> ack {restored.Ack} read-back {Format(restored.ReadBack)} (original {Format(original)})");
        }

        lock (events)
        {
            Console.WriteLine($"Mode-change events received: {events.Count}");
            foreach (var (at, bitmap) in events) Console.WriteLine($"  {(at - start).TotalSeconds,6:F1}s event {Format(bitmap)}");
        }
        await ReportWriter.TryWriteAsync(options, "set-anc", trace, [], CancellationToken.None);
        return failures == 0 ? 0 : 5;
    }

    private static async Task<(string Ack, uint? ReadBack)> SetAndVerifyAsync(OppoSession session, int index, CancellationToken ct)
    {
        byte[] mask = MaskBytes(index);
        var request = new OppoRequest(OppoCommand.SetNoiseControl, [0x01, 0x01, .. mask], SequenceMode.FixedSet);
        var result = await session.RequestAsync(request, TimeSpan.FromSeconds(3), ct);
        string ack = result.Response is { } r
            ? (r.Payload.Length > 0 ? r.Payload[0].ToString("X2") : "empty")
            : result.Status.ToString();
        await Task.Delay(600, ct);
        return (ack, await ReadModeAsync(session, ct));
    }

    private static async Task<uint?> ReadModeAsync(OppoSession session, CancellationToken ct)
    {
        var result = await session.RequestAsync(QueryCommands.StandardQueries[5].Request, cancellationToken: ct);
        return result.Response is { } r && OppoParsers.TryParseNoiseControlResponse(r, out uint bitmap) ? bitmap : null;
    }

    /// <summary>One-hot little-endian mask using the minimum number of bytes.</summary>
    internal static byte[] MaskBytes(int index)
    {
        var mask = new byte[index / 8 + 1];
        mask[index / 8] = (byte)(1 << (index % 8));
        return mask;
    }

    private static int LowestBit(uint value)
    {
        for (int i = 0; i < 32; i++) if ((value & (1u << i)) != 0) return i;
        return 0;
    }

    private static string Format(uint? bitmap) => bitmap is { } b ? $"#{LowestBit(b)} (0x{b:X})" : "unknown";
}
