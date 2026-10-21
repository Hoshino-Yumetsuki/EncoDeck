// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace EncoDeck.Probe;

/// <summary>Writes a JSON transcript when --report &lt;path&gt; is given. Addresses are never included.</summary>
internal static class ReportWriter
{
    public static async Task TryWriteAsync(ProbeOptions options, string command, ConsoleTrace trace, IReadOnlyList<(string Name, string Status)> results, CancellationToken ct)
    {
        string? path = options.Get("report");
        if (path is null) return;

        var report = new
        {
            tool = "EncoDeck.Probe",
            version = typeof(ReportWriter).Assembly.GetName().Version?.ToString(),
            command,
            createdUtc = DateTimeOffset.UtcNow,
            os = Environment.OSVersion.VersionString,
            results = results.Select(r => new { r.Name, r.Status }),
            transcript = trace.Transcript,
            note = "Bluetooth addresses are omitted. Frames contain protocol payloads only.",
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true }, ct);
        Console.WriteLine($"Report written to {Path.GetFullPath(path)}");
    }
}
