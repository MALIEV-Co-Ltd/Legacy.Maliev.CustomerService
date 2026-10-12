using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Legacy.Maliev.CustomerService.Application.Models;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

public sealed class OrdinaryAddressPrimitiveMetadataTests
{
    [Fact]
    public void OrdinaryDefaultSlots_PreserveOwnedConvertersScopedPolicyNullsAndWrites()
    {
        var baseline = Options(false);
        var current = Options(true);
        foreach (var field in new[] { "Building", "AddressLine2", "City", "State", "PostalCode" })
        {
            var payload = "{\"AddressLine1\":\"road\",\"CountryId\":764,\"" + field + "\":true}";
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertAddressRequest>(payload, baseline));
            var reference = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string?>>("{\"" + field + "\":true}");
            var request = JsonSerializer.Deserialize<UpsertAddressRequest>(payload, current)!;
            Assert.Equal(reference![field], typeof(UpsertAddressRequest).GetProperty(field)!.GetValue(request));
            Assert.Equal(JsonSerializer.Serialize(request, baseline), JsonSerializer.Serialize(request, current));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CustomerAddressRelationRequest>(payload, current));
            var repeatedNull = "{\"AddressLine1\":\"road\",\"CountryId\":764,\"" + field + "\":\"padded\",\"" + field + "\":null}";
            Assert.Equal(JsonSerializer.Deserialize<UpsertAddressRequest>(repeatedNull, baseline), JsonSerializer.Deserialize<UpsertAddressRequest>(repeatedNull, current));
            var propertyOwned = Options(true);
            var owned = new OwnedStringConverter();
            var resolver = (DefaultJsonTypeInfoResolver)propertyOwned.TypeInfoResolver!;
            resolver.Modifiers.Insert(0, info =>
            {
                if (info.Type == typeof(UpsertAddressRequest)) info.Properties.Single(property => property.Name == field).CustomConverter = owned;
            });
            var propertyValue = JsonSerializer.Deserialize<UpsertAddressRequest>(payload, propertyOwned)!;
            Assert.Equal("owned", typeof(UpsertAddressRequest).GetProperty(field)!.GetValue(propertyValue));
            var globalOwned = Options(true);
            globalOwned.Converters.Add(owned);
            var globalValue = JsonSerializer.Deserialize<UpsertAddressRequest>(payload, globalOwned)!;
            Assert.Equal("owned", typeof(UpsertAddressRequest).GetProperty(field)!.GetValue(globalValue));
        }
    }

    private static JsonSerializerOptions Options(bool candidate)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        if (candidate)
        {
            var type = typeof(Program).Assembly.GetType("Legacy.Maliev.CustomerService.Api.OrdinaryAddressPrimitiveMvcJsonMetadata", throwOnError: true)!;
            var method = type.GetMethod("Configure", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            resolver.Modifiers.Add(info => method.Invoke(null, [info]));
        }
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = null,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = resolver,
        };
    }

    private sealed class OwnedStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => "owned";
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
}
