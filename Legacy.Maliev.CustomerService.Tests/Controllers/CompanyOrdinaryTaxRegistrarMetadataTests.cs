using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Legacy.Maliev.CustomerService.Application.Models;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Tax and registrar serializer contract differentials for the ordinary MVC policy.</summary>
public sealed class CompanyOrdinaryTaxRegistrarMetadataTests
{
    /// <summary>Verifies ordinary tax and registrar binding while preserving resolver composition, custom metadata, serialization, and public record behavior.</summary>
    [Fact]
    public void OrdinaryTaxRegistrar_PreserveOptionsResolverWriteAndPublicRecordContracts()
    {
        Assert.Empty(typeof(UpsertCompanyRequest).GetCustomAttributes<JsonConverterAttribute>());
        var original = new UpsertCompanyRequest("บริษัท", " tax ", " registrar ");
        var (name, tax, registrar) = original;
        Assert.Equal(("บริษัท", " tax ", " registrar "), (name, tax, registrar));
        var equal = new UpsertCompanyRequest("บริษัท", " tax ", " registrar ");
        Assert.Equal(original, equal);
        Assert.Equal(original.GetHashCode(), equal.GetHashCode());
        Assert.Equal(new UpsertCompanyRequest("บริษัท", null, null), original with { TaxNumber = null, Registrar = null });
        Assert.Null((original with { Name = null! }).Name);

        var cases = new (string Body, string? Tax, string? Registrar)[]
        {
            ("{\"Name\":\"บริษัท\"}", null, null),
            ("{\"Name\":\"บริษัท\",\"TaxNumber\":null,\"Registrar\":null}", null, null),
            ("{\"Name\":\"บริษัท\",\"TaxNumber\":\" tax \",\"TaxNumber\":null,\"Registrar\":\" registrar \",\"Registrar\":null}", " tax ", " registrar "),
            ("{\"Name\":\"บริษัท\",\"taxnumber\":null,\"TaxNumber\":\"tax\",\"TAXNUMBER\":null,\"registrar\":null,\"Registrar\":\"r\",\"REGISTRAR\":null}", "tax", "r"),
            ("{\"Name\":\"บริษัท\",\"\\u0054axNumber\":\"tax\",\"\\u0074axNumber\":null,\"\\u0052egistrar\":\"r\",\"\\u0072egistrar\":null}", "tax", "r"),
            ("{\"Name\":\"บริษัท\",\"TaxNumber\":\"first\",\"TaxNumber\":null,\"TaxNumber\":\"last\",\"Registrar\":\"first\",\"Registrar\":null,\"Registrar\":\"last\"}", "last", "last"),
            ("{\"Name\":\"บริษัท\",\"TaxNumber\":\"\",\"TaxNumber\":null,\"Registrar\":\" \",\"Registrar\":null}", "", " ")
        };
        var active = Proposed();
        active.MakeReadOnly();
        Assert.NotNull(active.GetTypeInfo(typeof(UpsertCompanyRequest)).CreateObject);
        foreach (var item in cases)
        {
            var expected = new UpsertCompanyRequest("บริษัท", item.Tax, item.Registrar);
            var actual = JsonSerializer.Deserialize<UpsertCompanyRequest>(item.Body, active);
            Assert.Equal(expected, actual);
            var outcome = Read(item.Body, typeof(UpsertCompanyRequest), active);
            Assert.Null(outcome.ReadError);
            Assert.Equal(Fields(expected), outcome.DecodedFields);
            Assert.Null(outcome.WriteError);
            Assert.Equal(Write(expected, Options()), Write(actual!, active));
            Assert.Equal(expected, JsonSerializer.Deserialize<UpsertCompanyRequest>(outcome.Json!, active));
        }
        const string retained = "{\"Name\":\"first\",\"TaxNumber\":\"tax\",\"TaxNumber\":null,\"Registrar\":\"r\",\"Registrar\":null}";
        var knownBaseline = JsonSerializer.Deserialize<UpsertCompanyRequest>(retained, Options())!;
        Assert.Equal(new UpsertCompanyRequest("first", null, null), knownBaseline);
        Assert.Equal(new UpsertCompanyRequest("first", "tax", "r"), JsonSerializer.Deserialize<UpsertCompanyRequest>(retained, active));
        foreach (var field in new[] { "TaxNumber", "Registrar" })
            foreach (var invalid in new[] { "42", "true", "{}", "[]" })
            {
                var body = "{\"Name\":\"first\",\"" + field + "\":" + invalid + ",\"" + field + "\":\"later\",\"" + field + "\":null}";
                var baseline = Read(body, typeof(UpsertCompanyRequest), Options());
                Assert.Equal(typeof(JsonException).FullName, baseline.ReadError);
                var candidate = Read(body, typeof(UpsertCompanyRequest), Proposed());
                if (invalid is "42" or "true")
                {
                    var expected = new UpsertCompanyRequest("first", field == "TaxNumber" ? "later" : null,
                        field == "Registrar" ? "later" : null);
                    Assert.Null(candidate.ReadError);
                    Assert.Equal(Fields(expected), candidate.DecodedFields);
                    Assert.Null(candidate.WriteError);
                    Assert.Equal(Write(expected, Options()).Json, candidate.Json);
                }
                else Assert.Equal(baseline, candidate);
            }

        var inactiveBodies = cases.Select(item => item.Body).Concat(new[]
        {
            "{}", "null", "{\"Name\":null}", "{\"Name\":\"first\",\"TaxNumber\":null}",
            "{\"Name\":\"first\",\"Registrar\":null}", "{\"TaxNumber\":\"tax\",\"TaxNumber\":null}",
            "{\"Name\":\"first\",\"TaxNumber\":{},\"TaxNumber\":\"later\"}"
        }).ToArray();
        foreach (var required in new[] { false, true })
            foreach (var nullable in new[] { false, true })
                foreach (var ignoreNull in new[] { false, true })
                {
                    var baseline = Options();
                    baseline.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
#pragma warning disable SYSLIB0020
                    baseline.IgnoreNullValues = ignoreNull;
#pragma warning restore SYSLIB0020
                    baseline.RespectRequiredConstructorParameters = required;
                    baseline.RespectNullableAnnotations = nullable;
                    CompareInactive(baseline, inactiveBodies, original);
                }
        foreach (var change in new Action<JsonSerializerOptions>[]
        {
            options => options.RespectRequiredConstructorParameters = true,
            options => options.RespectNullableAnnotations = true,
            options => options.ReferenceHandler = ReferenceHandler.Preserve,
            options => options.ReferenceHandler = ReferenceHandler.IgnoreCycles,
            options => options.PropertyNameCaseInsensitive = false,
            options => options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            options => options.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            options => options.PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
            options => options.IgnoreReadOnlyProperties = true
        })
        {
            var baseline = Options();
            change(baseline);
            CompareInactive(baseline, inactiveBodies, original);
        }
        foreach (var reverse in new[] { false, true })
            Assert.Equal(ConfigurationConflict(Options(), reverse), ConfigurationConflict(Proposed(), reverse));

        foreach (var handler in new[] { ReferenceHandler.Preserve, ReferenceHandler.IgnoreCycles })
            foreach (var outOfOrder in new[] { false, true })
            {
                var baseline = Options();
                baseline.ReferenceHandler = handler;
                baseline.AllowOutOfOrderMetadataProperties = outOfOrder;
                var proposal = CopyProposed(baseline);
                foreach (var body in new[]
                {
                    retained, "{\"$id\":\"1\",\"Name\":\"first\",\"TaxNumber\":\"tax\",\"TaxNumber\":null}",
                    "{\"Name\":\"first\",\"Registrar\":\"r\",\"Registrar\":null,\"$id\":\"1\"}",
                    "[{\"$id\":\"1\",\"Name\":\"first\",\"TaxNumber\":\"tax\",\"TaxNumber\":null},{\"$ref\":\"1\"}]",
                    "{\"$id\":\"1\",\"$values\":[{\"$id\":\"2\",\"Name\":\"first\",\"Registrar\":\"r\",\"Registrar\":null},{\"$ref\":\"2\"}]}",
                    "{\"First\":{\"$id\":\"1\",\"Name\":\"first\",\"TaxNumber\":\"tax\",\"TaxNumber\":null},\"Second\":{\"$ref\":\"1\"}}"
                })
                {
                    var target = body.Contains("\"First\"", StringComparison.Ordinal) ? typeof(RequestPair)
                        : body.StartsWith("[", StringComparison.Ordinal) || body.Contains("\"$values\"", StringComparison.Ordinal)
                            ? typeof(List<UpsertCompanyRequest>) : typeof(UpsertCompanyRequest);
                    var expected = Read(body, target, baseline);
                    Assert.Equal(expected, Read(body, target, proposal));
                    if (handler == ReferenceHandler.Preserve && target == typeof(List<UpsertCompanyRequest>) && expected.ReadError is null)
                        Assert.True(expected.Alias);
                }
                foreach (var graph in new object[]
                {
                    original, original with { TaxNumber = null, Registrar = null },
                    new List<UpsertCompanyRequest> { original, original },
                    new List<UpsertCompanyRequest> { original, original with { } },
                    new RequestPair { First = original, Second = original }
                })
                {
                    var expected = Write(graph, baseline);
                    var actual = Write(graph, proposal);
                    Assert.Equal(expected, actual);
                    if (expected.Error is null)
                        Assert.Equal(Read(expected.Json!, graph.GetType(), baseline), Read(actual.Json!, graph.GetType(), proposal));
                }
            }

        foreach (var handleNull in new[] { false, true })
        {
            var converter = new PrefixStringConverter(handleNull);
            var baseline = Options();
            baseline.Converters.Add(converter);
            var proposal = CopyProposed(baseline);
            const string body = "{\"Name\":\"first\",\"TaxNumber\":\"tax\",\"TaxNumber\":null,\"TaxNumber\":\"last\",\"Registrar\":\"r\",\"Registrar\":null}";
            var value = JsonSerializer.Deserialize<UpsertCompanyRequest>(body, proposal)!;
            Assert.Equal("first", value.Name);
            Assert.Equal("prefix:last", value.TaxNumber);
            Assert.Equal(handleNull ? "prefix:null" : "prefix:r", value.Registrar);
            Assert.Equal(handleNull ? 5 : 3, converter.ReadCount);
            Assert.Equal(handleNull ? 2 : 0, converter.NullReadCount);
            var expected = new UpsertCompanyRequest("first", "prefix:last", handleNull ? "prefix:null" : "prefix:r");
            Assert.Equal(Write(expected, baseline), Write(value, proposal));
            var written = Write(value, proposal);
            Assert.Null(written.Error);
            using var document = JsonDocument.Parse(written.Json!);
            Assert.Equal("write:prefix:last", document.RootElement.GetProperty("TaxNumber").GetString());
            Assert.Equal("write:" + expected.Registrar, document.RootElement.GetProperty("Registrar").GetString());
            const string invalid = "{\"Name\":\"first\",\"TaxNumber\":{},\"TaxNumber\":null,\"TaxNumber\":\"later\"}";
            Assert.Equal(Read(invalid, typeof(UpsertCompanyRequest), baseline), Read(invalid, typeof(UpsertCompanyRequest), proposal));
        }

        foreach (var disallow in new[] { false, true })
            foreach (var depth in new[] { 2, 32 })
            {
                var baseline = Options();
                baseline.UnmappedMemberHandling = disallow ? JsonUnmappedMemberHandling.Disallow : JsonUnmappedMemberHandling.Skip;
                baseline.MaxDepth = depth;
                var proposal = CopyProposed(baseline);
                foreach (var body in new[]
                {
                    "{\"Name\":\"first\",\"TaxNumber\":\"tax\",\"unknown\":true}",
                    "{\"Name\":\"first\",\"Registrar\":\"r\",\"unknown\":{\"nested\":{\"last\":[null,true,42]}}}"
                })
                    Assert.Equal(Read(body, typeof(UpsertCompanyRequest), baseline), Read(body, typeof(UpsertCompanyRequest), proposal));
            }
        var strict = Options();
        strict.AllowDuplicateProperties = false;
        var strictProposal = CopyProposed(strict);
        foreach (var body in new[] { retained, "{\"Name\":\"first\",\"Registrar\":\"r\",\"registrar\":null}" })
        {
            var expected = Read(body, typeof(UpsertCompanyRequest), strict);
            Assert.Equal(typeof(JsonException).FullName, expected.ReadError);
            Assert.Equal(expected, Read(body, typeof(UpsertCompanyRequest), strictProposal));
        }
        foreach (var indented in new[] { false, true })
        {
            var baseline = Options();
            baseline.WriteIndented = indented;
            baseline.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            Assert.Equal(Write(original, baseline), Write(original, CopyProposed(baseline)));
        }

        var metadataOptions = Options();
        var metadata = metadataOptions.TypeInfoResolver!.GetTypeInfo(typeof(UpsertCompanyRequest), metadataOptions)!;
        Modify(metadata);
        var factory = metadata.CreateObject;
        var setters = metadata.Properties.Select(property => property.Set).ToArray();
        Assert.NotNull(factory);
        Modify(metadata);
        Assert.Same(factory, metadata.CreateObject);
        Assert.Equal(setters, metadata.Properties.Select(property => property.Set).ToArray());
        var customOptions = Options();
        var customMetadata = customOptions.TypeInfoResolver!.GetTypeInfo(typeof(UpsertCompanyRequest), customOptions)!;
        Func<object> customFactory = () => new UpsertCompanyRequest("custom", "custom tax", "custom registrar");
        customMetadata.CreateObject = customFactory;
        var customSetters = customMetadata.Properties.Select(property => property.Set).ToArray();
        Modify(customMetadata);
        Assert.Same(customFactory, customMetadata.CreateObject);
        Assert.Equal(customSetters, customMetadata.Properties.Select(property => property.Set).ToArray());
        var frozen = Options();
        frozen.MakeReadOnly();
        var frozenMetadata = frozen.GetTypeInfo(typeof(UpsertCompanyRequest));
        var frozenSetters = frozenMetadata.Properties.Select(property => property.Set).ToArray();
        Assert.True(frozenMetadata.IsReadOnly);
        Modify(frozenMetadata);
        Assert.Null(frozenMetadata.CreateObject);
        Assert.Equal(frozenSetters, frozenMetadata.Properties.Select(property => property.Set).ToArray());

        Assert.Null(Compose(null));
        var noResolver = Options();
        noResolver.TypeInfoResolver = null;
        var noResolverProposal = CopyProposed(noResolver);
        Assert.Null(noResolverProposal.TypeInfoResolver);
        Assert.Equal(Read(retained, typeof(UpsertCompanyRequest), noResolver), Read(retained, typeof(UpsertCompanyRequest), noResolverProposal));
        foreach (var resolver in new IJsonTypeInfoResolver[]
        {
            new DerivedResolver(), new DelegatingResolver(), new DefaultJsonTypeInfoResolver().WithAddedModifier(_ => { })
        })
        {
            Assert.Same(resolver, Compose(resolver));
            var baseline = Options();
            baseline.TypeInfoResolver = resolver;
            Assert.Equal(Read(retained, typeof(UpsertCompanyRequest), baseline), Read(retained, typeof(UpsertCompanyRequest), CopyProposed(baseline)));
        }
        var cachedOptions = Options();
        var cachedMetadata = cachedOptions.TypeInfoResolver!.GetTypeInfo(typeof(UpsertCompanyRequest), cachedOptions)!;
        var cachedFactory = cachedMetadata.CreateObject;
        var cachedSetters = cachedMetadata.Properties.Select(property => property.Set).ToArray();
        var shared = new SharedResolver(cachedMetadata);
        Assert.Same(shared, Compose(shared));
        Assert.Same(cachedMetadata, Compose(shared)!.GetTypeInfo(typeof(UpsertCompanyRequest), cachedOptions));
        Assert.Same(cachedFactory, cachedMetadata.CreateObject);
        Assert.Equal(cachedSetters, cachedMetadata.Properties.Select(property => property.Set).ToArray());
        cachedOptions.TypeInfoResolver = shared;
        var cachedOutcome = Read(retained, typeof(UpsertCompanyRequest), cachedOptions);
        Assert.Equal(cachedOutcome, Read(retained, typeof(UpsertCompanyRequest), cachedOptions));
        Assert.Same(shared, Compose(cachedOptions.TypeInfoResolver));
        var orderOptions = Options();
        var orderedResolver = Assert.IsType<DefaultJsonTypeInfoResolver>(orderOptions.TypeInfoResolver);
        var events = new List<string>();
        orderedResolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(UpsertCompanyRequest))
            {
                Assert.Null(info.CreateObject);
                events.Add("existing modifier");
            }
        });
        var ordered = Compose(orderedResolver)!.GetTypeInfo(typeof(UpsertCompanyRequest), orderOptions)!;
        Assert.Equal(new[] { "existing modifier" }, events);
        Assert.NotNull(ordered.CreateObject);
        Assert.Same(orderedResolver, orderOptions.TypeInfoResolver);
        foreach (var otherType in new[] { typeof(CustomerCompanyRelationRequest), typeof(CompanyResponse), typeof(RequestPair) })
        {
            var options = Options();
            var other = options.TypeInfoResolver!.GetTypeInfo(otherType, options)!;
            var creation = other.CreateObject;
            var otherSetters = other.Properties.Select(property => property.Set).ToArray();
            Modify(other);
            Assert.Same(creation, other.CreateObject);
            Assert.Equal(otherSetters, other.Properties.Select(property => property.Set).ToArray());
        }
        var scoped = JsonSerializer.Deserialize<CustomerCompanyRelationRequest>(retained, Proposed());
        Assert.Equal(new CustomerCompanyRelationRequest("first", null, null), scoped);
        foreach (var body in new[]
        {
            retained,
            "{\"Name\":\"first\",\"TaxNumber\":null,\"TaxNumber\":\"last\",\"Registrar\":null,\"Registrar\":\"last\"}",
            "{\"Name\":\"first\",\"TaxNumber\":42,\"TaxNumber\":\"later\"}",
            "{\"Name\":true,\"Name\":\"later\",\"Registrar\":\"r\",\"Registrar\":null}"
        })
            Assert.Equal(Read(body, typeof(CustomerCompanyRelationRequest), Options()), Read(body, typeof(CustomerCompanyRelationRequest), Proposed()));

        const string metadataBody = "{\"Name\":\"first\",\"Name\":null,\"TaxNumber\":\"tax\",\"TaxNumber\":null,\"Registrar\":\"r\",\"Registrar\":null}";
        foreach (var scenario in new[]
        {
            "removed-tax", "removed-registrar", "both-removed", "null-tax-setter", "null-registrar-setter", "both-null-setters",
            "custom-tax-setter", "custom-registrar-setter", "tax-converter", "registrar-converter"
        })
        {
            var baseline = ModifiedOptions(scenario, frozenNameOnly: true, out var baselineProbe);
            var proposal = ModifiedOptions(scenario, frozenNameOnly: false, out var proposalProbe);
            baseline.MakeReadOnly();
            proposal.MakeReadOnly();
            var baselineInfo = baseline.GetTypeInfo(typeof(UpsertCompanyRequest));
            var proposedInfo = proposal.GetTypeInfo(typeof(UpsertCompanyRequest));
            Assert.NotNull(baselineInfo.CreateObject);
            Assert.NotNull(proposedInfo.CreateObject);
            var previous = JsonSerializer.Deserialize<UpsertCompanyRequest>(metadataBody, baseline)!;
            var actual = JsonSerializer.Deserialize<UpsertCompanyRequest>(metadataBody, proposal)!;
            Assert.Equal("first", previous.Name);
            Assert.Equal(previous.Name, actual.Name);
            Assert.Null(previous.TaxNumber);
            Assert.Null(previous.Registrar);
            var availableTax = proposalProbe.Properties.TryGetValue("TaxNumber", out var taxProperty) && proposalProbe.Setters["TaxNumber"] is not null;
            var availableRegistrar = proposalProbe.Properties.TryGetValue("Registrar", out var registrarProperty) && proposalProbe.Setters["Registrar"] is not null;
            var expectedTax = !availableTax ? null : scenario == "custom-tax-setter" ? "setter:tax" : scenario == "tax-converter" ? "prefix:tax" : "tax";
            var expectedRegistrar = !availableRegistrar ? null : scenario == "custom-registrar-setter" ? "setter:r" : scenario == "registrar-converter" ? "prefix:r" : "r";
            var expected = new UpsertCompanyRequest("first", expectedTax, expectedRegistrar);
            Assert.Equal(expected, actual);
            foreach (var field in new[] { "TaxNumber", "Registrar" })
            {
                if (!proposalProbe.Properties.TryGetValue(field, out var property))
                {
                    Assert.DoesNotContain(proposedInfo.Properties, item => item.Name == field);
                    continue;
                }
                Assert.Same(property, proposedInfo.Properties.Single(item => item.Name == field));
                if (proposalProbe.Converters[field] is not null || proposalProbe.Setters[field] is null)
                    Assert.Same(proposalProbe.Converters[field], property.CustomConverter);
                else
                {
                    Assert.NotNull(property.CustomConverter);
                    Assert.Equal("Legacy.Maliev.CustomerService.Api.OrdinaryCompanyFieldPrimitiveJsonConverter", property.CustomConverter.GetType().FullName);
                    Assert.False(Assert.IsAssignableFrom<JsonConverter<string>>(property.CustomConverter).HandleNull);
                    Assert.Same(JsonSerializerOptions.Default.GetConverter(typeof(string)), proposal.GetConverter(typeof(string)));
                }
                if (proposalProbe.Setters[field] is null)
                    Assert.Null(property.Set);
                else
                    Assert.NotSame(proposalProbe.Setters[field], property.Set);
            }
            foreach (var field in proposalProbe.Calls.Keys)
            {
                Assert.Equal(new object?[] { field == "TaxNumber" ? "tax" : "r" }, proposalProbe.Calls[field]);
                Assert.Equal(new object?[] { field == "TaxNumber" ? "tax" : "r", null }, baselineProbe.Calls[field]);
            }
            if (scenario == "tax-converter")
                Assert.Equal(1, Assert.IsType<PrefixStringConverter>(taxProperty!.CustomConverter).ReadCount);
            if (scenario == "registrar-converter")
                Assert.Equal(1, Assert.IsType<PrefixStringConverter>(registrarProperty!.CustomConverter).ReadCount);
            Assert.Equal(Write(expected, baseline), Write(actual, proposal));
            var outcome = Read(metadataBody, typeof(UpsertCompanyRequest), proposal);
            Assert.Null(outcome.ReadError);
            Assert.Equal(Fields(expected), outcome.DecodedFields);
            Assert.Null(outcome.WriteError);
        }
    }

    private static JsonSerializerOptions Options()
    {
        var options = new JsonOptions().JsonSerializerOptions;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.PropertyNamingPolicy = null;
        options.DictionaryKeyPolicy = null;
        return options;
    }

    private static JsonSerializerOptions Proposed() => CopyProposed(Options());

    private static JsonSerializerOptions ModifiedOptions(string scenario, bool frozenNameOnly, out ModifierProbe probe)
    {
        var observation = new ModifierProbe();
        probe = observation;
        var options = Options();
        var resolver = Assert.IsType<DefaultJsonTypeInfoResolver>(options.TypeInfoResolver);
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(UpsertCompanyRequest))
                return;
            foreach (var field in new[] { "TaxNumber", "Registrar" })
            {
                var property = info.Properties.Single(item => item.Name == field);
                var tax = field == "TaxNumber";
                if (scenario == "both-removed" || scenario == (tax ? "removed-tax" : "removed-registrar"))
                {
                    info.Properties.Remove(property);
                    continue;
                }
                if (scenario == "both-null-setters" || scenario == (tax ? "null-tax-setter" : "null-registrar-setter"))
                    property.Set = null;
                if (scenario == (tax ? "custom-tax-setter" : "custom-registrar-setter"))
                {
                    var assign = property.Set!;
                    var calls = new List<object?>();
                    observation.Calls[field] = calls;
                    property.Set = (instance, value) =>
                    {
                        calls.Add(value);
                        assign(instance, value is string literal ? "setter:" + literal : value);
                    };
                }
                if (scenario == (tax ? "tax-converter" : "registrar-converter"))
                    property.CustomConverter = new PrefixStringConverter(handleNull: false);
                observation.Properties[field] = property;
                observation.Setters[field] = property.Set;
                observation.Converters[field] = property.CustomConverter;
            }
        });
        options.TypeInfoResolver = frozenNameOnly ? FrozenB988NameOnlyMetadata.Compose(resolver) : Compose(resolver);
        return options;
    }

    private static JsonSerializerOptions CopyProposed(JsonSerializerOptions baseline)
    {
        var proposal = new JsonSerializerOptions(baseline);
        proposal.TypeInfoResolver = Compose(proposal.TypeInfoResolver);
        return proposal;
    }

    private static void CompareInactive(JsonSerializerOptions baseline, IEnumerable<string> bodies, UpsertCompanyRequest original)
    {
        var proposal = CopyProposed(baseline);
        proposal.MakeReadOnly();
        Assert.Null(proposal.GetTypeInfo(typeof(UpsertCompanyRequest)).CreateObject);
        foreach (var body in bodies)
            Assert.Equal(Read(body, typeof(UpsertCompanyRequest), baseline), Read(body, typeof(UpsertCompanyRequest), proposal));
        Assert.Equal(Write(original, baseline), Write(original, proposal));
        var dictionary = new Dictionary<string, UpsertCompanyRequest> { ["MixedKey"] = original };
        Assert.Equal(Write(dictionary, baseline), Write(dictionary, proposal));
    }

    private static IJsonTypeInfoResolver? Compose(IJsonTypeInfoResolver? resolver)
    {
        var modifier = typeof(Program).Assembly.GetType("Legacy.Maliev.CustomerService.Api.OrdinaryCompanyMvcJsonMetadata", throwOnError: true)!;
        var compose = modifier.GetMethod("Compose", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<IJsonTypeInfoResolver?, IJsonTypeInfoResolver?>>();
        return compose(resolver);
    }

    private static void Modify(JsonTypeInfo metadata)
    {
        var modifier = typeof(Program).Assembly.GetType("Legacy.Maliev.CustomerService.Api.OrdinaryCompanyMvcJsonMetadata", throwOnError: true)!;
        var configure = modifier.GetMethod("Configure", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Action<JsonTypeInfo>>();
        configure(metadata);
    }

    private static ReadOutcome Read(string json, Type target, JsonSerializerOptions options)
    {
        object? result;
        try { result = JsonSerializer.Deserialize(json, target, options); }
        catch (JsonException exception) { return new(exception.GetType().FullName, exception.Path, null, null, null, null, null); }
        catch (NotSupportedException exception) { return new(exception.GetType().FullName, null, null, null, null, null, null); }
        catch (InvalidOperationException exception) { return new(exception.GetType().FullName, null, null, null, null, null, null); }
        var alias = result is List<UpsertCompanyRequest> list && list.Count == 2 ? ReferenceEquals(list[0], list[1])
            : result is RequestPair pair ? ReferenceEquals(pair.First, pair.Second) : (bool?)null;
        var fields = result switch
        {
            UpsertCompanyRequest request => Fields(request),
            CustomerCompanyRelationRequest request => Fields(new UpsertCompanyRequest(request.Name, request.TaxNumber, request.Registrar)),
            List<UpsertCompanyRequest> requests => requests.Count + ":" + string.Join("|", requests.Select(Fields)),
            RequestPair requests => Fields(requests.First) + "|" + Fields(requests.Second),
            null => "null root",
            _ => throw new InvalidOperationException("Unexpected differential target.")
        };
        var write = WriteTyped(result, target, options);
        return new(null, null, fields, write.Error, write.Path, write.Json, alias);
    }

    private static string Fields(UpsertCompanyRequest? value) => value is null ? "null request"
        : Field(value.Name) + ";" + Field(value.TaxNumber) + ";" + Field(value.Registrar);

    private static string Field(string? value) => value is null ? "null field"
        : string.Concat(value.Select(character => ((int)character).ToString("X4", CultureInfo.InvariantCulture)));

    private static WriteOutcome Write(object value, JsonSerializerOptions options) => WriteTyped(value, value.GetType(), options);

    private static WriteOutcome WriteTyped(object? value, Type target, JsonSerializerOptions options)
    {
        try { return new(null, null, JsonSerializer.Serialize(value, target, options)); }
        catch (JsonException exception) { return new(exception.GetType().FullName, exception.Path, null); }
        catch (NotSupportedException exception) { return new(exception.GetType().FullName, null, null); }
        catch (InvalidOperationException exception) { return new(exception.GetType().FullName, null, null); }
    }

    private static string ConfigurationConflict(JsonSerializerOptions options, bool reverse)
    {
        try
        {
#pragma warning disable SYSLIB0020
            if (reverse)
            {
                options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
                options.IgnoreNullValues = true;
                options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            }
            else
                options.IgnoreNullValues = true;
#pragma warning restore SYSLIB0020
            return "NO ERROR";
        }
        catch (InvalidOperationException exception) { return exception.GetType().FullName!; }
    }

    private sealed record ReadOutcome(string? ReadError, string? ReadPath, string? DecodedFields, string? WriteError, string? WritePath, string? Json, bool? Alias);
    private sealed record WriteOutcome(string? Error, string? Path, string? Json);

    private sealed class ModifierProbe
    {
        internal Dictionary<string, JsonPropertyInfo> Properties { get; } = new();
        internal Dictionary<string, Action<object, object?>?> Setters { get; } = new();
        internal Dictionary<string, JsonConverter?> Converters { get; } = new();
        internal Dictionary<string, List<object?>> Calls { get; } = new();
    }

    // Frozen Name-only comparator from b9888968e1bb58efe8947c4d081c44c607c245fa:
    // Legacy.Maliev.CustomerService.Api/OrdinaryCompanyMvcJsonMetadata.cs.
    // Only class visibility/name change for test isolation; method bodies are copied unchanged.
    private static class FrozenB988NameOnlyMetadata
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

    private sealed class DerivedResolver : DefaultJsonTypeInfoResolver
    {
    }

    private sealed class DelegatingResolver : IJsonTypeInfoResolver
    {
        private readonly DefaultJsonTypeInfoResolver inner = new();
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => inner.GetTypeInfo(type, options);
    }

    private sealed class SharedResolver(JsonTypeInfo metadata) : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => type == metadata.Type ? metadata : null;
    }

    private sealed class PrefixStringConverter(bool handleNull) : JsonConverter<string>
    {
        public override bool HandleNull => handleNull;
        internal int ReadCount { get; private set; }
        internal int NullReadCount { get; private set; }

        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            ReadCount++;
            if (reader.TokenType == JsonTokenType.Null)
            {
                NullReadCount++;
                return "prefix:null";
            }
            return "prefix:" + reader.GetString();
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue("write:" + value);
    }

    /// <summary>Request alias graph used to compare reference metadata and writes.</summary>
    public sealed class RequestPair
    {
        /// <summary>First request instance.</summary>
        public UpsertCompanyRequest? First { get; set; }
        /// <summary>Second request instance.</summary>
        public UpsertCompanyRequest? Second { get; set; }
    }
}
