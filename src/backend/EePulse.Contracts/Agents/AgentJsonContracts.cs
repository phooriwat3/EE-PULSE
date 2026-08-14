using System.Text.Json;
using System.Text.Json.Serialization;

namespace EePulse.Contracts.Agents;

public sealed class UtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (text is null || !text.EndsWith('Z') || !reader.TryGetDateTimeOffset(out var value) ||
            value.Offset != TimeSpan.Zero)
        {
            throw new JsonException("Agent timestamps must be RFC 3339 UTC values with a Z offset.");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new JsonException("Agent timestamps must be UTC values.");
        }

        writer.WriteStringValue(value.UtcDateTime);
    }
}

public static class AgentJsonContract
{
    public static void AddConverters(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Converters.Add(new UtcDateTimeOffsetJsonConverter());
    }
}
