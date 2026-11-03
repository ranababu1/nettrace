using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Time;

namespace NetTrace.Infrastructure.Logging;

/// <summary>
/// Writes structured log entries as JSON lines to one file per UTC day
/// (<c>nettrace-yyyyMMdd.jsonl</c>). Logging never blocks callers on disk I/O: entries pass
/// through a bounded queue drained by a single background writer, and entries that do not
/// fit in the queue are counted and reported rather than buffered without limit.
/// </summary>
public sealed class JsonFileLoggerProvider : ILoggerProvider
{
    public const int QueueCapacity = 4096;

    private static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, JsonFileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Channel<LogLine> _queue;
    private readonly Task _writerTask;
    private readonly LogLevel _minimumLevel;
    private long _droppedCount;
    private int _disposed;

    public JsonFileLoggerProvider(string logDirectory, IClock clock, LogLevel minimumLevel = LogLevel.Information)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentNullException.ThrowIfNull(clock);

        LogDirectory = Path.GetFullPath(logDirectory);
        Clock = clock;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(LogDirectory);

        _queue = Channel.CreateBounded<LogLine>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public string LogDirectory { get; }

    internal IClock Clock { get; }

    public static string GetFileName(DateTimeOffset timestampUtc) =>
        $"nettrace-{timestampUtc.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.jsonl";

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new JsonFileLogger(name, this));

    internal bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None && logLevel >= _minimumLevel && Volatile.Read(ref _disposed) == 0;

    internal void Enqueue(DateTimeOffset timestampUtc, string line)
    {
        if (!_queue.Writer.TryWrite(new LogLine(timestampUtc, line)))
        {
            Interlocked.Increment(ref _droppedCount);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        try
        {
            _writerTask.Wait(ShutdownFlushTimeout);
        }
        catch (AggregateException ex)
        {
            Debug.WriteLine($"NetTrace file logger stopped with an error: {ex.InnerException?.Message}");
        }
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        string? currentFile = null;
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var entry))
                {
                    var fileName = GetFileName(entry.TimestampUtc);
                    if (!string.Equals(fileName, currentFile, StringComparison.Ordinal))
                    {
                        if (writer is not null)
                        {
                            await writer.DisposeAsync().ConfigureAwait(false);
                        }

                        writer = OpenWriter(fileName);
                        currentFile = fileName;
                    }

                    if (writer is null)
                    {
                        continue;
                    }

                    var dropped = Interlocked.Exchange(ref _droppedCount, 0);
                    if (dropped > 0)
                    {
                        await TryWriteLineAsync(writer, JsonFileLogger.FormatDroppedNotice(entry.TimestampUtc, dropped))
                            .ConfigureAwait(false);
                    }

                    await TryWriteLineAsync(writer, entry.Line).ConfigureAwait(false);
                }

                if (writer is not null)
                {
                    await TryFlushAsync(writer).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private StreamWriter? OpenWriter(string fileName)
    {
        try
        {
            var stream = new FileStream(
                Path.Combine(LogDirectory, fileName), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"NetTrace file logger could not open {fileName}: {ex.Message}");
            return null;
        }
    }

    private static async Task TryWriteLineAsync(StreamWriter writer, string line)
    {
        try
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"NetTrace file logger write failed: {ex.Message}");
        }
    }

    private static async Task TryFlushAsync(StreamWriter writer)
    {
        try
        {
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"NetTrace file logger flush failed: {ex.Message}");
        }
    }

    private readonly record struct LogLine(DateTimeOffset TimestampUtc, string Line);
}
