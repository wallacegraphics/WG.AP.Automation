using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WG.AP.Integrations.Pace.Generated;

public partial class PaceClient
{
    /// <summary>
    /// Pace's value-object endpoints are already known to return scalars as JSON strings (see the
    /// hand-parsed <c>GetNullableInt</c>/<c>GetDecimal</c>/<c>GetBool</c> helpers in
    /// <see cref="Pace.PaceInvoiceService"/>); its <c>readObject</c> endpoints are suspected of the
    /// same for at least some generated <c>Read*Async</c> types. <c>AllowReadingFromString</c> tolerates
    /// a quoted number without breaking the case where Pace does send a real JSON number.
    /// <see cref="PaceDateTimeOffsetConverter"/> is the confirmed fix for a real failure: Pace's
    /// <c>lastModified</c> field (present on most readObject types, not just the one that first
    /// surfaced this) is formatted as <c>"2025-12-29T18:57:36GMT-05:00"</c> - a literal "GMT" spliced
    /// between the time and the offset that .NET's built-in ISO-8601 DateTimeOffset parser rejects.
    /// </summary>
    static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings)
    {
        settings.NumberHandling = JsonNumberHandling.AllowReadingFromString;
        settings.PropertyNameCaseInsensitive = true;
        settings.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        settings.Converters.Add(new PaceDateTimeOffsetConverter());
    }
}

/// <summary>
/// Parses Pace's non-standard <c>"yyyy-MM-ddTHH:mm:ssGMT{offset}"</c> timestamp format (e.g.
/// <c>"2025-12-29T18:57:36GMT-05:00"</c>), falling back to normal ISO-8601 parsing for any field that
/// isn't affected. Registering a converter for the non-nullable <see cref="DateTimeOffset"/> also covers
/// every <c>DateTimeOffset?</c> property on the generated client - System.Text.Json wraps a value-type
/// converter for its nullable counterpart automatically.
/// </summary>
public sealed class PaceDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private const string PaceGmtFormat = "yyyy-MM-ddTHH:mm:ss'GMT'zzz";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a Pace date string but got {reader.TokenType}.");
        }

        var value = reader.GetString();

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        if (DateTimeOffset.TryParseExact(value, PaceGmtFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return parsed;
        }

        throw new JsonException($"Unrecognized Pace date '{value}'.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
