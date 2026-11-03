using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetTrace.Infrastructure.Logging;

internal sealed class JsonFileLogger(string categoryName, JsonFileLoggerProvider provider) : ILogger
{
    private const string OriginalFormatKey = "{OriginalFormat}";

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(formatter);
        var timestamp = provider.Clock.UtcNow;

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("timestamp_utc", FormatTimestamp(timestamp));
            json.WriteString("level", logLevel.ToString());
            json.WriteString("category", categoryName);
            if (eventId.Id != 0 || eventId.Name is not null)
            {
                json.WriteNumber("event_id", eventId.Id);
                if (eventId.Name is not null)
                {
                    json.WriteString("event_name", eventId.Name);
                }
            }

            json.WriteString("message", formatter(state, exception));

            if (state is IReadOnlyList<KeyValuePair<string, object?>> properties)
            {
                WriteProperties(json, properties);
            }

            if (exception is not null)
            {
                json.WriteStartObject("exception");
                json.WriteString("type", exception.GetType().FullName);
                json.WriteString("message", exception.Message);
                json.WriteString("details", exception.ToString());
                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        provider.Enqueue(timestamp, Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    internal static string FormatDroppedNotice(DateTimeOffset timestamp, long droppedCount)
    {
        var buffer = new ArrayBufferWriter<byte>(192);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("timestamp_utc", FormatTimestamp(timestamp));
            json.WriteString("level", nameof(LogLevel.Warning));
            json.WriteString("category", typeof(JsonFileLoggerProvider).FullName);
            json.WriteString(
                "message",
                string.Create(CultureInfo.InvariantCulture, $"{droppedCount} log entries were dropped because the log queue was full."));
            json.WriteStartObject("properties");
            json.WriteNumber("DroppedCount", droppedCount);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static void WriteProperties(Utf8JsonWriter json, IReadOnlyList<KeyValuePair<string, object?>> properties)
    {
        var started = false;
        foreach (var (key, value) in properties)
        {
            if (key == OriginalFormatKey)
            {
                continue;
            }

            if (!started)
            {
                json.WriteStartObject("properties");
                started = true;
            }

            json.WritePropertyName(key);
            WriteValue(json, value);
        }

        if (started)
        {
            json.WriteEndObject();
        }
    }

    private static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNullValue();
                break;
            case bool b:
                json.WriteBooleanValue(b);
                break;
            case int i:
                json.WriteNumberValue(i);
                break;
            case long l:
                json.WriteNumberValue(l);
                break;
            case double d when double.IsFinite(d):
                json.WriteNumberValue(d);
                break;
            case float f when float.IsFinite(f):
                json.WriteNumberValue(f);
                break;
            case decimal m:
                json.WriteNumberValue(m);
                break;
            case DateTimeOffset dto:
                json.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTime dt:
                json.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeSpan ts:
                json.WriteStringValue(ts.ToString("c", CultureInfo.InvariantCulture));
                break;
            case IFormattable formattable:
                json.WriteStringValue(formattable.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                json.WriteStringValue(value.ToString());
                break;
        }
    }
}
