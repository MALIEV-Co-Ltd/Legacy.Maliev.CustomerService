using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Legacy.Maliev.CustomerService.Api;

/// <summary>Preserves ordinary company field primitive lexemes while delegating existing string and write behavior.</summary>
internal sealed class OrdinaryCompanyFieldPrimitiveJsonConverter(JsonConverter<string> literal) : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Number => reader.HasValueSequence
                ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
                : Encoding.UTF8.GetString(reader.ValueSpan),
            _ => literal.Read(ref reader, typeToConvert, options),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        literal.Write(writer, value, options);
}
