using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quill.Core.Summaries;

/// <summary>Serializes enums as camelCase strings ("live", "brief").</summary>
public sealed class CamelCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public CamelCaseEnumConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Transcript offsets as "hh:mm:ss" (hours may exceed 23, e.g. "25:03:09").
/// Reads are lenient with what language models emit: "h:mm:ss", "mm:ss" and a
/// fractional second part are accepted; sub-second precision is truncated.
/// </summary>
public sealed class TimestampJsonConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("timestamp must be a \"hh:mm:ss\" string");
        }

        var text = reader.GetString();
        return TryParse(text, out var value)
            ? value
            : throw new JsonException($"invalid timestamp \"{text}\", expected \"hh:mm:ss\"");
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Format(value));

    /// <summary>"hh:mm:ss", truncating sub-second precision; negative values clamp to zero.</summary>
    public static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(long)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2}");
    }

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        long hours = 0;
        if (parts.Length == 3 &&
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours))
        {
            return false;
        }

        var minutesText = parts[^2];
        var secondsText = parts[^1];
        var dot = secondsText.IndexOfAny(['.', ',']);
        if (dot >= 0)
        {
            secondsText = secondsText[..dot];
        }

        if (!long.TryParse(minutesText, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !long.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds > 59 || (parts.Length == 3 && minutes > 59) || hours > 100_000 || minutes > 6_000_000)
        {
            return false;
        }

        value = TimeSpan.FromSeconds((hours * 3600) + (minutes * 60) + seconds);
        return true;
    }
}
