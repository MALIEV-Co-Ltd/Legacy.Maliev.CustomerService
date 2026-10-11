using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Legacy.Maliev.CustomerService.Application.Models;

namespace Legacy.Maliev.CustomerService.Api;

internal static class OrdinaryCompanyMvcJsonMetadata
{
    internal static IJsonTypeInfoResolver? Compose(IJsonTypeInfoResolver? resolver) =>
        resolver?.GetType() == typeof(DefaultJsonTypeInfoResolver)
            ? resolver.WithAddedModifier(Configure)
            : resolver;

    internal static void Configure(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(UpsertCompanyRequest)
            || typeInfo.Kind != JsonTypeInfoKind.Object
            || typeInfo.IsReadOnly
            || typeInfo.CreateObject is not null
            || !UsesOrdinaryMvcPolicy(typeInfo.Options))
            return;

        var name = typeInfo.Properties.SingleOrDefault(property => property.Name == nameof(UpsertCompanyRequest.Name));
        if (name is null || name.PropertyType != typeof(string) || name.Set is not { } assignName)
            return;

        typeInfo.CreateObject = static () => new UpsertCompanyRequest(null!, null, null);
        name.Set = (instance, value) =>
        {
            if (value is not null)
                assignName(instance, value);
        };
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
