using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Legacy.Maliev.CustomerService.Application.Models;

namespace Legacy.Maliev.CustomerService.Api;

internal static class OrdinaryCompanyMvcJsonMetadata
{
    private static readonly JsonConverter<string> DefaultString =
        (JsonConverter<string>)JsonSerializerOptions.Default.GetConverter(typeof(string));

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
        name.Set = IgnoreNull(assignName);
        var taxNumber = typeInfo.Properties.SingleOrDefault(property => property.Name == nameof(UpsertCompanyRequest.TaxNumber));
        if (taxNumber is not null && taxNumber.PropertyType == typeof(string) && taxNumber.Set is { } assignTaxNumber)
        {
            taxNumber.Set = IgnoreNull(assignTaxNumber);
            PreserveDefaultStringRead(taxNumber, typeInfo.Options);
        }
        var registrar = typeInfo.Properties.SingleOrDefault(property => property.Name == nameof(UpsertCompanyRequest.Registrar));
        if (registrar is not null && registrar.PropertyType == typeof(string) && registrar.Set is { } assignRegistrar)
        {
            registrar.Set = IgnoreNull(assignRegistrar);
            PreserveDefaultStringRead(registrar, typeInfo.Options);
        }
    }

    private static void PreserveDefaultStringRead(JsonPropertyInfo property, JsonSerializerOptions options)
    {
        if (property.CustomConverter is not null
            || options.Converters.Any(converter => converter.CanConvert(typeof(string)))
            || !ReferenceEquals(options.GetConverter(typeof(string)), DefaultString))
            return;

        property.CustomConverter = new OrdinaryCompanyFieldPrimitiveJsonConverter(DefaultString);
    }

    private static Action<object, object?> IgnoreNull(Action<object, object?> assign) =>
        (instance, value) =>
        {
            if (value is not null)
                assign(instance, value);
        };

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
