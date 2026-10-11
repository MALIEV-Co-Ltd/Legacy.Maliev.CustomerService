using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Legacy.Maliev.CustomerService.Application.Models;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Concrete source-design controls; compilation and execution are not yet admitted.</summary>
public sealed class CompanyOrdinaryMvcMetadataControls
{
    [Fact]
    public void OrdinaryDuplicateNames_PreserveOtherFieldsAndPublicRecordAndWireContracts()
    {
        Assert.Empty(typeof(UpsertCompanyRequest).GetCustomAttributes<JsonConverterAttribute>());
        var original = new UpsertCompanyRequest("first", "tax", "registrar");
        var (name, tax, registrar) = original;
        Assert.Equal(("first", "tax", "registrar"), (name, tax, registrar));
        Assert.Equal(original, new UpsertCompanyRequest("first", "tax", "registrar"));
        Assert.Equal(original.GetHashCode(), new UpsertCompanyRequest("first", "tax", "registrar").GetHashCode());
        Assert.Null((original with { Name = null! }).Name);

        var active = Options();
        Install(active);
        active.MakeReadOnly();
        Assert.NotNull(active.GetTypeInfo(typeof(UpsertCompanyRequest)).CreateObject);
        var outside = Options();
        outside.MakeReadOnly();
        Assert.Null(outside.GetTypeInfo(typeof(UpsertCompanyRequest)).CreateObject);
        Assert.Null(JsonSerializer.Deserialize<UpsertCompanyRequest>("{\"Name\":\"first\",\"Name\":null}", outside)!.Name);
        foreach (var body in new[]
        {
            "{\"Name\":\"first\",\"Name\":null}",
            "{\"Name\":null,\"Name\":\"first\",\"name\":null}",
            "{\"\\u004eame\":\"first\",\"\\u006eame\":null}",
            "{\"Name\":true,\"Name\":null}",
            "{\"Name\":1E+03,\"Name\":null}",
            "{\"Name\":1e309,\"Name\":null}",
            "{\"Name\":\"" + new string('x', 257) + "\",\"Name\":\"first\"}"
        })
        {
            var value = JsonSerializer.Deserialize<UpsertCompanyRequest>(body, active)!;
            Assert.NotNull(value.Name);
            Assert.Equal(body.Contains("true", StringComparison.Ordinal) ? "true"
                : body.Contains("1E+03", StringComparison.Ordinal) ? "1E+03"
                : body.Contains("1e309", StringComparison.Ordinal) ? "1e309" : "first", value.Name);
        }
        var otherFields = JsonSerializer.Deserialize<UpsertCompanyRequest>(
            "{\"Name\":\"first\",\"Name\":null,\"TaxNumber\":\"tax\",\"TaxNumber\":null,\"Registrar\":\"r\",\"Registrar\":null}", active)!;
        Assert.Equal("first", otherFields.Name);
        // Intentional compatibility repair: the two ordinary string fields now retain earlier non-null values.
        Assert.Equal("tax", otherFields.TaxNumber);
        Assert.Equal("r", otherFields.Registrar);
        foreach (var invalid in new[]
        {
            "{\"Name\":{},\"Name\":\"later\"}",
            "{\"Name\":[],\"Name\":\"later\"}",
            "{\"Name\":\"\\uD800\",\"Name\":\"later\"}",
            "{\"Name\":\"first\",\"Name\":\"\\uDC00\"}",
            "{\"Name\":\"first\",\"Name\":null,}",
            "{\"Name\":\"first\",\"TaxNumber\":42}",
            "{\"Name\":\"first\",\"Registrar\":true}"
        })
        {
            var baseline = Read(invalid, typeof(UpsertCompanyRequest), Options());
            var candidate = Read(invalid, typeof(UpsertCompanyRequest), Proposed());
            if (invalid.Contains("TaxNumber", StringComparison.Ordinal) || invalid.Contains("Registrar", StringComparison.Ordinal))
            {
                Assert.Equal(typeof(JsonException).FullName, baseline.ReadError);
                Assert.Null(candidate.ReadError);
                Assert.Null(candidate.WriteError);
                var expected = new UpsertCompanyRequest("first", invalid.Contains("TaxNumber", StringComparison.Ordinal) ? "42" : null,
                    invalid.Contains("Registrar", StringComparison.Ordinal) ? "true" : null);
                Assert.Equal(JsonSerializer.Serialize(expected, Options()), candidate.Json);
            }
            else Assert.Equal(baseline, candidate);
        }

        foreach (var ignoreNull in new[] { false, true })
            foreach (var required in new[] { false, true })
                foreach (var nullable in new[] { false, true })
                {
                    var bodies = new[]
                    {
                        "{}", "{\"Name\":\"first\",\"TaxNumber\":null,\"Registrar\":null}",
                        "{\"Name\":null,\"TaxNumber\":null,\"Registrar\":null}",
                        "{\"Name\":\"first\",\"Name\":null,\"TaxNumber\":null,\"Registrar\":null}",
                        "{\"Name\":\"first\",\"TaxNumber\":\"tax\",\"TaxNumber\":null,\"Registrar\":\"r\",\"Registrar\":null}",
                        "{\"TaxNumber\":null,\"Registrar\":null}",
                        "{\"Name\":\"first\",\"Registrar\":null}",
                        "{\"Name\":\"first\",\"TaxNumber\":null}"
                    };
                    foreach (var body in bodies)
                    {
                        var baseline = Options();
                        baseline.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
#pragma warning disable SYSLIB0020
                        baseline.IgnoreNullValues = ignoreNull;
#pragma warning restore SYSLIB0020
                        baseline.RespectRequiredConstructorParameters = required;
                        baseline.RespectNullableAnnotations = nullable;
                        var proposal = new JsonSerializerOptions(baseline);
                        Install(proposal);
                        proposal.MakeReadOnly();
                        Assert.Null(proposal.GetTypeInfo(typeof(UpsertCompanyRequest)).CreateObject);
                        Assert.Equal(Read(body, typeof(UpsertCompanyRequest), baseline), Read(body, typeof(UpsertCompanyRequest), proposal));
                    }
                }
        foreach (var reverse in new[] { false, true })
        {
            var baseline = Options();
            var proposal = Options();
            Install(proposal);
            Assert.Equal(ConfigurationConflict(baseline, reverse), ConfigurationConflict(proposal, reverse));
        }
        foreach (var preserve in new[] { false, true })
            foreach (var outOfOrder in new[] { false, true })
            {
                var baseline = Options();
                baseline.ReferenceHandler = preserve ? ReferenceHandler.Preserve : null;
                baseline.AllowOutOfOrderMetadataProperties = outOfOrder;
                var proposal = new JsonSerializerOptions(baseline);
                Install(proposal);
                if (preserve)
                    Assert.Equal(Read("{\"$id\":\"1\",\"Name\":\"first\",\"Name\":null}", typeof(UpsertCompanyRequest), baseline), Read("{\"$id\":\"1\",\"Name\":\"first\",\"Name\":null}", typeof(UpsertCompanyRequest), proposal));
                foreach (var body in new[]
                {
                    "{\"Name\":\"first\"}", "{\"$id\":\"1\",\"Name\":\"first\"}", "{\"$ref\":\"1\"}",
                    "{\"Name\":\"first\",\"$id\":\"1\"}", "{\"$id\":\"1\",\"$id\":\"2\",\"Name\":\"first\"}",
                    "[{\"$id\":\"1\",\"Name\":\"first\"},{\"$ref\":\"1\"}]",
                    "{\"$id\":\"1\",\"$values\":[{\"$id\":\"2\",\"Name\":\"first\"},{\"$ref\":\"2\"}]}",
                    "{\"First\":{\"$id\":\"1\",\"Name\":\"first\"},\"Second\":{\"$ref\":\"1\"}}"
                })
                {
                    var target = body.Contains("\"First\"", StringComparison.Ordinal) ? typeof(RequestPair)
                        : body.StartsWith("[", StringComparison.Ordinal) || body.Contains("\"$values\"", StringComparison.Ordinal)
                            ? typeof(List<UpsertCompanyRequest>) : typeof(UpsertCompanyRequest);
                    Assert.Equal(Read(body, target, baseline), Read(body, target, proposal));
                }
                foreach (var graph in new object[]
                {
                    original, new UpsertCompanyRequest(null!, null, null),
                    new List<UpsertCompanyRequest> { original, original },
                    new List<UpsertCompanyRequest> { original, original with { } },
                    new RequestPair { First = original, Second = original }
                })
                {
                    var baselineWrite = Write(graph, baseline);
                    var proposedWrite = Write(graph, proposal);
                    Assert.Equal(baselineWrite, proposedWrite);
                    if (baselineWrite.Error is null)
                        Assert.Equal(Read(baselineWrite.Json!, graph.GetType(), baseline), Read(proposedWrite.Json!, graph.GetType(), proposal));
                }
            }
        foreach (var alter in new Action<JsonSerializerOptions>[]
        {
            value => value.RespectRequiredConstructorParameters = true,
            value => value.RespectNullableAnnotations = true,
            value => value.ReferenceHandler = ReferenceHandler.Preserve,
            value => value.ReferenceHandler = ReferenceHandler.IgnoreCycles,
            value => value.PropertyNameCaseInsensitive = false,
            value => value.PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            value => value.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            value => value.PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
            value => value.IgnoreReadOnlyProperties = true
        })
        {
            var baseline = Options();
            alter(baseline);
            var proposal = new JsonSerializerOptions(baseline);
            Install(proposal);
            proposal.MakeReadOnly();
            Assert.Null(proposal.GetTypeInfo(typeof(UpsertCompanyRequest)).CreateObject);
            const string body = "{\"Name\":\"first\",\"Name\":null,\"TaxNumber\":null,\"Registrar\":null}";
            Assert.Equal(Read(body, typeof(UpsertCompanyRequest), baseline), Read(body, typeof(UpsertCompanyRequest), proposal));
        }
        foreach (var otherType in new[] { typeof(CustomerCompanyRelationRequest), typeof(CompanyResponse), typeof(RequestPair) })
        {
            var options = Options();
            var resolver = options.TypeInfoResolver!;
            var metadata = resolver.GetTypeInfo(otherType, options)!;
            var creation = metadata.CreateObject;
            var setters = metadata.Properties.Select(property => property.Set).ToArray();
            Modify(metadata);
            Assert.Same(creation, metadata.CreateObject);
            Assert.Equal(setters, metadata.Properties.Select(property => property.Set).ToArray());
        }
        var compositionOptions = Options();
        var composedInfo = compositionOptions.TypeInfoResolver!.GetTypeInfo(typeof(UpsertCompanyRequest), compositionOptions)!;
        Modify(composedInfo);
        var firstFactory = composedInfo.CreateObject;
        var firstSetter = composedInfo.Properties.Single(property => property.Name == "Name").Set;
        Modify(composedInfo);
        Assert.Same(firstFactory, composedInfo.CreateObject);
        Assert.Same(firstSetter, composedInfo.Properties.Single(property => property.Name == "Name").Set);
        var customOptions = Options();
        var customInfo = customOptions.TypeInfoResolver!.GetTypeInfo(typeof(UpsertCompanyRequest), customOptions)!;
        Func<object> customFactory = () => new UpsertCompanyRequest("custom", null, null);
        customInfo.CreateObject = customFactory;
        var customSetter = customInfo.Properties.Single(property => property.Name == "Name").Set;
        Modify(customInfo);
        Assert.Same(customFactory, customInfo.CreateObject);
        Assert.Same(customSetter, customInfo.Properties.Single(property => property.Name == "Name").Set);
        var frozenMetadata = outside.GetTypeInfo(typeof(UpsertCompanyRequest));
        var frozenSetter = frozenMetadata.Properties.Single(property => property.Name == "Name").Set;
        Assert.True(frozenMetadata.IsReadOnly);
        Modify(frozenMetadata);
        Assert.Null(frozenMetadata.CreateObject);
        Assert.Same(frozenSetter, frozenMetadata.Properties.Single(property => property.Name == "Name").Set);
        var inactiveBaseline = Options();
        inactiveBaseline.ReferenceHandler = ReferenceHandler.Preserve;
        var inactiveProposal = new JsonSerializerOptions(inactiveBaseline);
        Install(inactiveProposal);
        inactiveBaseline.MakeReadOnly();
        inactiveProposal.MakeReadOnly();
        Assert.NotSame(inactiveBaseline.GetTypeInfo(typeof(UpsertCompanyRequest)), inactiveProposal.GetTypeInfo(typeof(UpsertCompanyRequest)));
        var defaults = new[] { "{}", "{\"Name\":null}", "{\"Name\":\"\"}", "{\"Name\":\" \"}", "{\"TaxNumber\":null,\"Registrar\":null}" };
        foreach (var body in defaults)
            Assert.Equal(Read(body, typeof(UpsertCompanyRequest), Options()), Read(body, typeof(UpsertCompanyRequest), Proposed()));
        var strict = Options();
        strict.AllowDuplicateProperties = false;
        var strictProposal = new JsonSerializerOptions(strict);
        Install(strictProposal);
        Assert.Equal(Read("{\"Name\":\"first\",\"Name\":null}", typeof(UpsertCompanyRequest), strict), Read("{\"Name\":\"first\",\"Name\":null}", typeof(UpsertCompanyRequest), strictProposal));
        foreach (var disallow in new[] { false, true })
            foreach (var depth in new[] { 2, 32 })
            {
                var baseline = Options();
                baseline.UnmappedMemberHandling = disallow ? JsonUnmappedMemberHandling.Disallow : JsonUnmappedMemberHandling.Skip;
                baseline.MaxDepth = depth;
                var proposal = new JsonSerializerOptions(baseline);
                Install(proposal);
                foreach (var body in new[] { "{\"Name\":\"first\",\"unknown\":true}", "{\"Name\":\"first\",\"unknown\":{\"nested\":{\"last\":[null,true,42]}}}" })
                    Assert.Equal(Read(body, typeof(UpsertCompanyRequest), baseline), Read(body, typeof(UpsertCompanyRequest), proposal));
            }
        foreach (var indented in new[] { false, true })
        {
            var baseline = Options();
            baseline.WriteIndented = indented;
            baseline.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            baseline.Converters.Add(new PrefixStringConverter());
            var proposal = new JsonSerializerOptions(baseline);
            Install(proposal);
            Assert.Equal(Read("{\"Name\":\"บริษัท\",\"TaxNumber\":\"tax\",\"Registrar\":\"registrar\"}", typeof(UpsertCompanyRequest), baseline), Read("{\"Name\":\"บริษัท\",\"TaxNumber\":\"tax\",\"Registrar\":\"registrar\"}", typeof(UpsertCompanyRequest), proposal));
            var populated = JsonSerializer.Deserialize<UpsertCompanyRequest>("{\"Name\":\"บริษัท\",\"Name\":null,\"TaxNumber\":\"tax\",\"Registrar\":\"registrar\"}", proposal)!;
            Assert.Equal("บริษัท", populated.Name);
            Assert.Equal("prefix:tax", populated.TaxNumber);
            Assert.Equal("prefix:registrar", populated.Registrar);
            Assert.Equal(Write(original, baseline), Write(original, proposal));
        }
        Assert.Null(Compose(null));
        var nullBaseline = Options();
        nullBaseline.TypeInfoResolver = null;
        var nullProposal = new JsonSerializerOptions(nullBaseline);
        Install(nullProposal);
        Assert.Null(nullProposal.TypeInfoResolver);
        Assert.Equal(Read("{\"Name\":\"first\",\"Name\":null}", typeof(UpsertCompanyRequest), nullBaseline), Read("{\"Name\":\"first\",\"Name\":null}", typeof(UpsertCompanyRequest), nullProposal));
        var cachedOptions = Options();
        var sharedMetadata = cachedOptions.TypeInfoResolver!.GetTypeInfo(typeof(UpsertCompanyRequest), cachedOptions)!;
        var sharedFactory = sharedMetadata.CreateObject;
        var sharedSetter = sharedMetadata.Properties.Single(property => property.Name == "Name").Set;
        var sharedResolver = new SharedResolver(sharedMetadata);
        Assert.Same(sharedResolver, Compose(sharedResolver));
        Assert.Same(sharedMetadata, Compose(sharedResolver)!.GetTypeInfo(typeof(UpsertCompanyRequest), cachedOptions));
        Assert.Same(sharedFactory, sharedMetadata.CreateObject);
        Assert.Same(sharedSetter, sharedMetadata.Properties.Single(property => property.Name == "Name").Set);
        var orderOptions = Options();
        var sourceResolver = Assert.IsType<DefaultJsonTypeInfoResolver>(orderOptions.TypeInfoResolver);
        var events = new List<string>();
        sourceResolver.Modifiers.Add(metadata =>
        {
            if (metadata.Type == typeof(UpsertCompanyRequest))
            {
                Assert.Null(metadata.CreateObject);
                events.Add("existing modifier");
            }
        });
        var ordered = Compose(sourceResolver)!.GetTypeInfo(typeof(UpsertCompanyRequest), orderOptions)!;
        Assert.Equal(["existing modifier"], events);
        Assert.NotNull(ordered.CreateObject);
        Assert.Same(sourceResolver, orderOptions.TypeInfoResolver);
        Assert.Same(sharedResolver, Compose(sharedResolver));
        var wrapped = sourceResolver.WithAddedModifier(_ => { });
        Assert.Same(wrapped, Compose(wrapped));
    }

    private static JsonSerializerOptions Options()
    {
        var options = new JsonOptions().JsonSerializerOptions;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.PropertyNamingPolicy = null;
        options.DictionaryKeyPolicy = null;
        return options;
    }

    private static JsonSerializerOptions Proposed()
    {
        var options = Options();
        Install(options);
        return options;
    }

    private static void Install(JsonSerializerOptions options) =>
        options.TypeInfoResolver = Compose(options.TypeInfoResolver);

    private static IJsonTypeInfoResolver? Compose(IJsonTypeInfoResolver? resolver)
    {
        var modifier = typeof(Program).Assembly.GetType("Legacy.Maliev.CustomerService.Api.OrdinaryCompanyMvcJsonMetadata", throwOnError: true)!;
        var compose = modifier.GetMethod("Compose", BindingFlags.Static | BindingFlags.NonPublic)!;
        return compose.CreateDelegate<Func<IJsonTypeInfoResolver?, IJsonTypeInfoResolver?>>()(resolver);
    }

    private static void Modify(JsonTypeInfo metadata)
    {
        var modifier = typeof(Program).Assembly.GetType("Legacy.Maliev.CustomerService.Api.OrdinaryCompanyMvcJsonMetadata", throwOnError: true)!;
        var configure = modifier.GetMethod("Configure", BindingFlags.Static | BindingFlags.NonPublic)!;
        configure.CreateDelegate<Action<JsonTypeInfo>>()(metadata);
    }

    private static ReadOutcome Read(string json, Type target, JsonSerializerOptions options)
    {
        object? result;
        try
        {
            result = JsonSerializer.Deserialize(json, target, options);
        }
        catch (JsonException exception) { return new(exception.GetType().FullName, exception.Path, null, null, null, null, null); }
        catch (NotSupportedException exception) { return new(exception.GetType().FullName, null, null, null, null, null, null); }
        var alias = result is List<UpsertCompanyRequest> list && list.Count == 2 ? ReferenceEquals(list[0], list[1])
            : result is RequestPair pair ? ReferenceEquals(pair.First, pair.Second) : (bool?)null;
        var fields = result switch
        {
            UpsertCompanyRequest request => Fields(request),
            List<UpsertCompanyRequest> requests => requests.Count + ":" + string.Join("|", requests.Select(Fields)),
            RequestPair requests => Fields(requests.First) + "|" + Fields(requests.Second),
            null => "null root",
            _ => throw new InvalidOperationException("Unexpected differential target."),
        };
        var write = WriteTyped(result, target, options);
        return new(null, null, fields, write.Error, write.Path, write.Json, alias);
    }

    private static string Fields(UpsertCompanyRequest? value) => value is null ? "null request"
        : Field(value.Name) + ";" + Field(value.TaxNumber) + ";" + Field(value.Registrar);

    private static string Field(string? value) => value is null ? "null field"
        : string.Concat(value.Select(character => ((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));

    private static WriteOutcome Write(object value, JsonSerializerOptions options) => WriteTyped(value, value.GetType(), options);

    private static WriteOutcome WriteTyped(object? value, Type target, JsonSerializerOptions options)
    {
        try { return new(null, null, JsonSerializer.Serialize(value, target, options)); }
        catch (JsonException exception) { return new(exception.GetType().FullName, exception.Path, null); }
        catch (NotSupportedException exception) { return new(exception.GetType().FullName, null, null); }
    }

    private static string ConfigurationConflict(JsonSerializerOptions options, bool reverse)
    {
        try
        {
#pragma warning disable SYSLIB0020
            if (reverse) { options.DefaultIgnoreCondition = JsonIgnoreCondition.Never; options.IgnoreNullValues = true; options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull; }
            else options.IgnoreNullValues = true;
#pragma warning restore SYSLIB0020
            return "NO ERROR";
        }
        catch (InvalidOperationException exception) { return exception.GetType().FullName!; }
    }

    private sealed record ReadOutcome(string? ReadError, string? ReadPath, string? DecodedFields, string? WriteError, string? WritePath, string? Json, bool? Alias);
    private sealed record WriteOutcome(string? Error, string? Path, string? Json);

    private sealed class SharedResolver(JsonTypeInfo metadata) : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => type == metadata.Type ? metadata : null;
    }

    private sealed class PrefixStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => "prefix:" + reader.GetString();
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    /// <summary>Concrete reference resolver scope control.</summary>
    public sealed class RequestPair
    {
        /// <summary>First request instance.</summary>
        public UpsertCompanyRequest? First { get; set; }
        /// <summary>Second request instance.</summary>
        public UpsertCompanyRequest? Second { get; set; }
    }
}
