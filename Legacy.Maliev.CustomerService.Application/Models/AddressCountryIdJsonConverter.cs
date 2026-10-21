using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Legacy.Maliev.CustomerService.Application.Models;

/// <summary>Preserves original Int32 address country values and numeric-string conversion without a nonzero restriction.</summary>
public sealed class AddressCountryIdJsonConverter : JsonConverter<int>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return 0;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number)) return number;
        if (reader.TokenType == JsonTokenType.String)
        {
            string? text;
            try { text = reader.GetString(); }
            catch (InvalidOperationException error) { throw new JsonException("The address country identifier contains malformed Unicode.", error); }
            if (string.IsNullOrEmpty(text)) return 0;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var textNumber)) return textNumber;
        }
        throw new JsonException("The address country identifier must be an Int32 value.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
