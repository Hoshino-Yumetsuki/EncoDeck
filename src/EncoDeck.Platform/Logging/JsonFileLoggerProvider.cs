// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Platform.Logging;

/// <summary>
/// Structured JSON-lines file logger. Writes happen on a background task so logging never blocks
/// Bluetooth callbacks or the UI thread. All text is passed through <see cref="LogRedactor"/>.
/// Files roll daily and only the newest <see cref="RetainedFiles"/> are kept.
/// </summary>
public sealed class JsonFileLoggerProvider : ILoggerProvider
{
    public const int RetainedFiles = 7;

    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly Task _writer;

    public JsonFileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
        PruneOldFiles();
        _writer = Task.Run(WriteLoopAsync);
    }

    public string CurrentFilePath => Path.Combine(_directory, $"encodeck-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Logging must never crash shutdown.
        }
    }

    private void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        string? openPath = null;
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                string path = CurrentFilePath;
                if (path != openPath)
                {
                    if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
                    writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                    openPath = path;
                }
                while (_queue.Reader.TryRead(out var line))
                {
                    await writer!.WriteLineAsync(line).ConfigureAwait(false);
                }
                await writer!.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // Disk full / file locked: drop logs rather than affect the app.
        }
        finally
        {
            if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            foreach (var file in new DirectoryInfo(_directory).GetFiles("encodeck-*.log")
                         .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                         .Skip(RetainedFiles))
            {
                file.Delete();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileLogger(JsonFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("t", DateTimeOffset.Now.ToString("O"));
                json.WriteString("lvl", ShortLevel(logLevel));
                json.WriteString("cat", category);
                if (eventId.Id != 0) json.WriteNumber("evt", eventId.Id);
                json.WriteString("msg", LogRedactor.Redact(formatter(state, exception)));
                if (state is IReadOnlyList<KeyValuePair<string, object?>> props)
                {
                    foreach (var (key, value) in props)
                    {
                        if (key == "{OriginalFormat}") continue;
                        json.WriteString(key, LogRedactor.Redact(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));
                    }
                }
                if (exception is not null) json.WriteString("ex", LogRedactor.Redact(exception.ToString()));
                json.WriteEndObject();
            }
            provider.Enqueue(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        }

        private static string ShortLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };
    }
}
