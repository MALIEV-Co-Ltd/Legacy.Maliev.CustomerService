using System.Text.Json;
using System.Text.Json.Serialization;

namespace Legacy.Maliev.CustomerService.Application.Models;

/// <summary>Preserves the original Newtonsoft address-line string conversion for standard JSON primitives.</summary>
public sealed class AddressLine1JsonConverter : JsonConverter<string>
{
    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            try { return reader.GetString(); }
            catch (InvalidOperationException error) { throw new JsonException("The address line contains malformed Unicode.", error); }
        }
        if (reader.TokenType == JsonTokenType.True) return "true";
        if (reader.TokenType == JsonTokenType.False) return "false";
        if (reader.TokenType == JsonTokenType.Number)
        {
            using var number = JsonDocument.ParseValue(ref reader);
            return number.RootElement.GetRawText();
        }

        throw new JsonException("The address line must be a string or scalar JSON primitive.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
