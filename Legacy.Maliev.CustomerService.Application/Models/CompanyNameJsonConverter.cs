using System.Text.Json;
using System.Text.Json.Serialization;

namespace Legacy.Maliev.CustomerService.Application.Models;

/// <summary>Reads literal company-name strings and reports malformed Unicode as invalid JSON.</summary>
public sealed class CompanyNameJsonConverter : JsonConverter<string>
{
    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("The company name must be a string.");

        try { return reader.GetString(); }
        catch (InvalidOperationException error) { throw new JsonException("The company name contains malformed Unicode.", error); }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
