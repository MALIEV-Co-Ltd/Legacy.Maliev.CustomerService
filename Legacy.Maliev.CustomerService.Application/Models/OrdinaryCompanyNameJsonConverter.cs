using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Legacy.Maliev.CustomerService.Application.Models;

/// <summary>Preserves the strict-JSON primitive name lexemes accepted by the ordinary legacy company routes.</summary>
public sealed class OrdinaryCompanyNameJsonConverter : JsonConverter<string>
{
    private static readonly CompanyNameJsonConverter Literal = new();

    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Literal.Read(ref reader, typeToConvert, options);
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            case JsonTokenType.Number:
                var text = reader.HasValueSequence
                    ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
                    : Encoding.UTF8.GetString(reader.ValueSpan);
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return text;
                throw new JsonException("The company name contains an invalid number.");
            default:
                throw new JsonException("The company name must be a string, Boolean or number.");
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
