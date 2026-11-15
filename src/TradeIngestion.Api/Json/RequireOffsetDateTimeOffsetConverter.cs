using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TradeIngestion.Api.Json;

/// <summary>
/// Rejects timestamps without an explicit UTC offset. The default converter silently interprets
/// "2025-03-01T10:15:00" in the server's local time zone, which would mis-order corrections depending on where
/// the service happens to run.
/// </summary>
public sealed partial class RequireOffsetDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Timestamp must be an ISO 8601 string.");
        }

        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text) || !ExplicitOffset().IsMatch(text))
        {
            throw new JsonException("Timestamp must be ISO 8601 with an explicit offset, e.g. 2025-03-01T10:15:00Z.");
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            throw new JsonException("Timestamp is not a valid ISO 8601 date-time.");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitOffset();
}
