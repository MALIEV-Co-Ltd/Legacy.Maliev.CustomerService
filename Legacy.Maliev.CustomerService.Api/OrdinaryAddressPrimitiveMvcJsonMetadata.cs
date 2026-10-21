using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Legacy.Maliev.CustomerService.Application.Models;

namespace Legacy.Maliev.CustomerService.Api;

internal static class OrdinaryAddressPrimitiveMvcJsonMetadata
{
    private static readonly JsonConverter<string> DefaultString =
        (JsonConverter<string>)JsonSerializerOptions.Default.GetConverter(typeof(string));

    internal static void Configure(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(UpsertAddressRequest)
            || typeInfo.Kind != JsonTypeInfoKind.Object
            || typeInfo.IsReadOnly
            || typeInfo.CreateObject is not null
            || !UsesOrdinaryMvcPolicy(typeInfo.Options))
            return;

        foreach (var field in new[] { nameof(UpsertAddressRequest.Building), nameof(UpsertAddressRequest.AddressLine2),
            nameof(UpsertAddressRequest.City), nameof(UpsertAddressRequest.State), nameof(UpsertAddressRequest.PostalCode) })
        {
            var property = typeInfo.Properties.SingleOrDefault(candidate => candidate.Name == field);
            if (property is null || property.PropertyType != typeof(string) || property.Set is null
                || property.CustomConverter is not null
                || typeInfo.Options.Converters.Any(converter => converter.CanConvert(typeof(string)))
                || !ReferenceEquals(typeInfo.Options.GetConverter(typeof(string)), DefaultString))
                continue;

            property.CustomConverter = new OrdinaryCompanyFieldPrimitiveJsonConverter(DefaultString);
        }
    }

    private static bool UsesOrdinaryMvcPolicy(JsonSerializerOptions options) =>
        options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingNull
        && options.PropertyNameCaseInsensitive
        && options.PropertyNamingPolicy is null
        && options.DictionaryKeyPolicy is null
        && options.ReferenceHandler is null
        && !options.RespectRequiredConstructorParameters
        && !options.RespectNullableAnnotations
        && options.PreferredObjectCreationHandling == JsonObjectCreationHandling.Replace
        && !options.IgnoreReadOnlyProperties;
}
