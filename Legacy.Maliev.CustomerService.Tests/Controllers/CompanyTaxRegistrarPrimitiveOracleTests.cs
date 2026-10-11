using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Legacy.Maliev.CustomerService.Api.Controllers;
using Legacy.Maliev.CustomerService.Application.Models;
using Microsoft.AspNetCore.Mvc;
using Xunit.Abstractions;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

public sealed class CompanyTaxRegistrarPrimitiveOracleTests(ITestOutputHelper output)
{
    [Fact]
    public void PairedFields_RecordPinnedPrimitiveOracleAndFreezeCurrentReadControllerWriteBoundaries()
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var language in new[] { "en-US", "de-DE", "th-TH" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                foreach (var field in new[] { "TaxNumber", "Registrar" })
                {
                    var cases = Cases(field).ToArray();
                    Assert.Equal(27, cases.Length);
                    foreach (var item in cases)
                    {
                        var json = "{\"Name\":\"ok\"" + (item.Members.Length == 0 ? "" : "," + item.Members) + "}";
                        MutableReference? reference = null;
                        string? referenceError = null;
                        if (item.ReferenceRejects)
                            referenceError = Assert.ThrowsAny<Newtonsoft.Json.JsonException>(() => Reference(json)).GetType().FullName;
                        else
                        {
                            reference = Reference(json);
                            Assert.Equal("ok", reference.Name);
                            Assert.Equal(item.Expected, field == "TaxNumber" ? reference.TaxNumber : reference.Registrar);
                            Assert.Null(field == "TaxNumber" ? reference.Registrar : reference.TaxNumber);
                        }
                        foreach (var segmented in new[] { false, true })
                        {
                            var baseline = Observe(json, Options(true), segmented);
                            var current = Observe(json, Options(false), segmented);
                            if (item.CurrentRejects)
                                Assert.Equal(typeof(JsonException).FullName, baseline.ReadError);
                            else
                                Assert.Equal(baseline, current);
                            if (item.ReferenceRejects)
                            {
                                Assert.Equal(typeof(JsonException).FullName, current.ReadError);
                                Assert.Null(current.Decoded);
                                Assert.Null(current.ControllerDetails);
                                Assert.Null(current.Written);
                            }
                            else
                            {
                                Assert.Null(current.ReadError);
                                Assert.Equal(Fields(new UpsertCompanyRequest("ok", field == "TaxNumber" ? item.Expected : null,
                                    field == "Registrar" ? item.Expected : null)), current.Decoded);
                                Assert.True(current.ControllerDetails);
                                Assert.Null(current.WriteError);
                                Assert.Equal(JsonSerializer.Serialize(new UpsertCompanyRequest("ok", field == "TaxNumber" ? item.Expected : null,
                                    field == "Registrar" ? item.Expected : null), Options(true)), current.Written);
                            }
                            output.WriteLine(JsonSerializer.Serialize(new
                            {
                                Kind = "primitive-reference",
                                Culture = language,
                                Field = field,
                                Case = item.Label,
                                Segmented = segmented,
                                ReferenceValue = item.Expected,
                                ReferenceError = referenceError,
                                AcceptedBaseline = baseline,
                                Current = current,
                                PostgreSqlStorage = "NOT RUN in reference comparison",
                                Reference = "Newtonsoft.Json [13.0.3] on net10; historical resolved net8 runtime UNKNOWN",
                            }));
                        }
                    }
                    foreach (var item in UnicodeCases(field))
                        foreach (var segmented in new[] { false, true })
                        {
                            var json = "{\"Name\":\"ok\"," + item.Members + "}";
                            var current = Observe(json, Options(false), segmented);
                            Assert.Equal(Observe(json, Options(true), segmented), current);
                            if (item.Expected is not null)
                            {
                                Assert.Null(current.ReadError);
                                Assert.True(current.ControllerDetails);
                                Assert.Equal(Fields(new UpsertCompanyRequest("ok", field == "TaxNumber" ? item.Expected : null,
                                    field == "Registrar" ? item.Expected : null)), current.Decoded);
                            }
                            output.WriteLine(JsonSerializer.Serialize(new
                            {
                                Kind = "builtin-string-differential",
                                Culture = language,
                                Field = field,
                                Case = item.Label,
                                Segmented = segmented,
                                Current = current,
                                PostgreSqlStorage = "NOT RUN in string differential",
                                HistoricalMalformedParity = "UNKNOWN",
                            }));
                        }
                }
            }
            foreach (var scenario in new[]
            {
                "global", "global-null", "tax-property", "registrar-property", "tax-setter", "registrar-setter",
                "remove-tax", "remove-registrar", "unset-tax", "unset-registrar", "wrapped-resolver"
            })
                foreach (var value in new[] { "42", "null", "\"text\"" })
                {
                    var json = "{\"Name\":\"ok\",\"Name\":null,\"TaxNumber\":" + value + ",\"Registrar\":" + value + "}";
                    var accepted = Modified(scenario, true);
                    var current = Modified(scenario, false);
                    var expected = Observe(json, accepted, false);
                    var actual = Observe(json, current, false);
                    if (value != "42" || scenario is "global" or "global-null" or "wrapped-resolver")
                        Assert.Equal(expected, actual);
                    else
                    {
                        Assert.Equal(typeof(JsonException).FullName, expected.ReadError);
                        var tax = scenario switch
                        {
                            "tax-property" => "probe:42",
                            "tax-setter" => "setter:42",
                            "remove-tax" or "unset-tax" => null,
                            _ => "42",
                        };
                        var registrar = scenario switch
                        {
                            "registrar-property" => "probe:42",
                            "registrar-setter" => "setter:42",
                            "remove-registrar" or "unset-registrar" => null,
                            _ => "42",
                        };
                        var decoded = new UpsertCompanyRequest("ok", tax, registrar);
                        Assert.Null(actual.ReadError);
                        Assert.Equal(Fields(decoded), actual.Decoded);
                        Assert.True(actual.ControllerDetails);
                        Assert.Null(actual.WriteError);
                        Assert.Equal(JsonSerializer.Serialize(decoded, accepted), actual.Written);
                    }
                    if (scenario.EndsWith("-property", StringComparison.Ordinal))
                    {
                        var field = scenario.StartsWith("tax-", StringComparison.Ordinal) ? "TaxNumber" : "Registrar";
                        Assert.IsType<ProbeStringConverter>(current.GetTypeInfo(typeof(UpsertCompanyRequest)).Properties.Single(property => property.Name == field).CustomConverter);
                    }
                    output.WriteLine(JsonSerializer.Serialize(new { Kind = "custom-metadata-differential", Scenario = scenario, Token = value, AcceptedBaseline = expected, Current = actual }));
                }
            var owned = Options(false);
            owned.MakeReadOnly();
            var defaultLiteral = JsonSerializerOptions.Default.GetConverter(typeof(string));
            Assert.Same(defaultLiteral, owned.GetConverter(typeof(string)));
            foreach (var field in new[] { "TaxNumber", "Registrar" })
                Assert.Equal("Legacy.Maliev.CustomerService.Api.OrdinaryCompanyFieldPrimitiveJsonConverter",
                    owned.GetTypeInfo(typeof(UpsertCompanyRequest)).Properties.Single(property => property.Name == field).CustomConverter!.GetType().FullName);
            foreach (var field in new[] { "TaxNumber", "Registrar" })
                foreach (var malformed in new[] { "\\uD800", "\\uDC00", "\\uDC00\\uD800" })
                {
                    var json = "{\"Name\":\"ok\",\"" + field + "\":\"" + malformed + "\"}";
                    var acceptedError = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertCompanyRequest>(json, Options(true)));
                    var candidateError = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertCompanyRequest>(json, Options(false)));
                    Assert.Equal(acceptedError.Path, candidateError.Path);
                    Assert.Equal(acceptedError.LineNumber, candidateError.LineNumber);
                    Assert.Equal(acceptedError.BytePositionInLine, candidateError.BytePositionInLine);
                }
            // Even an explicitly registered default converter is user-owned global metadata.
            var registered = BaseOptions();
            registered.Converters.Add(defaultLiteral);
            registered.TypeInfoResolver = Compose(registered.TypeInfoResolver);
            registered.MakeReadOnly();
            foreach (var field in new[] { "TaxNumber", "Registrar" })
                Assert.Null(registered.GetTypeInfo(typeof(UpsertCompanyRequest)).Properties.Single(property => property.Name == field).CustomConverter);
            Assert.Equal(typeof(JsonException).FullName,
                Observe("{\"Name\":\"ok\",\"TaxNumber\":42}", registered, false).ReadError);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    internal static JsonSerializerOptions Options(bool frozen)
    {
        var options = BaseOptions();
        options.TypeInfoResolver = frozen ? FrozenAcceptedMetadata.Compose(options.TypeInfoResolver) : Compose(options.TypeInfoResolver);
        return options;
    }

    private static JsonSerializerOptions BaseOptions()
    {
        var options = new JsonOptions().JsonSerializerOptions;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.PropertyNamingPolicy = null;
        options.DictionaryKeyPolicy = null;
        return options;
    }

    private static IJsonTypeInfoResolver? Compose(IJsonTypeInfoResolver? resolver)
    {
        var compose = typeof(Program).Assembly.GetType("Legacy.Maliev.CustomerService.Api.OrdinaryCompanyMvcJsonMetadata", throwOnError: true)!
            .GetMethod("Compose", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<IJsonTypeInfoResolver?, IJsonTypeInfoResolver?>>();
        return compose(resolver);
    }

    private static JsonSerializerOptions Modified(string scenario, bool frozen)
    {
        var options = BaseOptions();
        if (scenario.StartsWith("global", StringComparison.Ordinal))
            options.Converters.Add(new ProbeStringConverter(scenario == "global-null"));
        var resolver = Assert.IsType<DefaultJsonTypeInfoResolver>(options.TypeInfoResolver);
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(UpsertCompanyRequest)) return;
            foreach (var field in new[] { "TaxNumber", "Registrar" })
            {
                var property = info.Properties.Single(item => item.Name == field);
                var prefix = field == "TaxNumber" ? "tax" : "registrar";
                if (scenario == "remove-" + prefix) info.Properties.Remove(property);
                else if (scenario == "unset-" + prefix) property.Set = null;
                else if (scenario == prefix + "-property") property.CustomConverter = new ProbeStringConverter(false);
                else if (scenario == prefix + "-setter")
                {
                    var assign = property.Set!;
                    property.Set = (instance, value) => assign(instance, value is string text ? "setter:" + text : value);
                }
            }
        });
        IJsonTypeInfoResolver selected = scenario == "wrapped-resolver" ? resolver.WithAddedModifier(_ => { }) : resolver;
        var composed = frozen ? FrozenAcceptedMetadata.Compose(selected) : Compose(selected);
        if (scenario == "wrapped-resolver") Assert.Same(selected, composed);
        options.TypeInfoResolver = composed;
        return options;
    }

    internal static bool Details(UpsertCompanyRequest request)
    {
        var details = typeof(CompaniesController).GetMethod("HasCompanyDetails", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<UpsertCompanyRequest, bool>>();
        return details(request);
    }

    private static Observation Observe(string json, JsonSerializerOptions options, bool segmented)
    {
        UpsertCompanyRequest? value;
        try { value = Read(json, options, segmented); }
        catch (JsonException error) { return new(error.GetType().FullName, error.Path, null, null, null, null); }
        catch (InvalidOperationException error) { return new(error.GetType().FullName, null, null, null, null, null); }
        catch (NotSupportedException error) { return new(error.GetType().FullName, null, null, null, null, null); }
        Assert.NotNull(value);
        var decoded = Fields(value);
        var details = Details(value);
        try { return new(null, null, decoded, details, null, JsonSerializer.Serialize(value, options)); }
        catch (JsonException error) { return new(null, null, decoded, details, error.GetType().FullName, null); }
        catch (InvalidOperationException error) { return new(null, null, decoded, details, error.GetType().FullName, null); }
        catch (NotSupportedException error) { return new(null, null, decoded, details, error.GetType().FullName, null); }
    }

    private static UpsertCompanyRequest? Read(string json, JsonSerializerOptions options, bool segmented)
    {
        if (!segmented) return JsonSerializer.Deserialize<UpsertCompanyRequest>(json, options);
        var bytes = Encoding.UTF8.GetBytes(json);
        var first = new Segment(bytes.AsMemory(0, 1));
        var last = first;
        for (var index = 1; index < bytes.Length; index++) last = last.Append(bytes.AsMemory(index, 1));
        var reader = new Utf8JsonReader(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length));
        return JsonSerializer.Deserialize<UpsertCompanyRequest>(ref reader, options);
    }

    private static MutableReference Reference(string json) => Newtonsoft.Json.JsonConvert.DeserializeObject<MutableReference>(json,
        new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore })!;

    private static string Fields(UpsertCompanyRequest value) => string.Join(";", new[] { value.Name, value.TaxNumber, value.Registrar }
        .Select(text => text is null ? "null" : string.Concat(text.Select(character => ((int)character).ToString("X4", CultureInfo.InvariantCulture)))));

    private static IEnumerable<Case> Cases(string field)
    {
        foreach (var primitive in new[] { "true", "false", "42", "1.5", "1E+03", "1.500", "1e309", "1e400", "-0", "9223372036854775808123456789" })
            yield return new("primitive/" + primitive, $"\"{field}\":{primitive}", primitive, true);
        yield return new("primitive-null", $"\"{field}\":true,\"{field}\":null", "true", true);
        yield return new("primitive-between", $"\"{field}\":\"before\",\"{field}\":42,\"{field}\":null,\"{field}\":\"later\"", "later", true);
        var longNumber = new string('1', 257);
        yield return new("long-primitive-bounded-null", $"\"{field}\":{longNumber},\"{field}\":\"bounded\",\"{field}\":null", "bounded", true);
        yield return new("long-primitive-null", $"\"{field}\":{longNumber},\"{field}\":null", longNumber, true);
        yield return new("case-primitive-null", $"\"{field.ToUpperInvariant()}\":42,\"{field.ToLowerInvariant()}\":null", "42", true);
        var escaped = field == "TaxNumber" ? "\\u0054axNumber" : "\\u0052egistrar";
        yield return new("escaped-primitive-null", $"\"{escaped}\":1E+03,\"{field}\":null", "1E+03", true);
        yield return new("missing", "", null, false);
        yield return new("null-only", $"\"{field}\":null", null, false);
        yield return new("null-text-null", $"\"{field}\":null,\"{field}\":\"text\",\"{field}\":null", "text", false);
        yield return new("empty", $"\"{field}\":\"\"", "", false);
        const string text = " ก้ 😀 \t ";
        yield return new("whitespace-thai-pair", $"\"{field}\":" + JsonSerializer.Serialize(text), text, false);
        foreach (var length in new[] { 255, 256, 257 })
            yield return new("string-length/" + length, $"\"{field}\":" + JsonSerializer.Serialize(new string('x', length)), new string('x', length), false);
        yield return new("long-string-bounded-null", $"\"{field}\":" + JsonSerializer.Serialize(new string('x', 257)) + $",\"{field}\":\"bounded\",\"{field}\":null", "bounded", false);
        foreach (var invalid in new[] { "{}", "[]" })
            yield return new("structured/" + invalid, $"\"{field}\":{invalid},\"{field}\":\"later\"", null, true, true);
    }

    private static IEnumerable<Case> UnicodeCases(string field)
    {
        foreach (var malformed in new[] { "\\uD800", "\\uDC00", "\\uDC00\\uD800" })
            yield return new("malformed/" + malformed, $"\"{field}\":\"{malformed}\",\"{field}\":null,\"{field}\":\"later\"", null, false);
        foreach (var text in new[] { "\0", " ก้ 😀 ", "ก้😀", new string('x', 255), new string('x', 256), new string('x', 257), string.Concat(Enumerable.Repeat("😀", 129)) })
            yield return new("literal/" + text.Length, $"\"{field}\":" + JsonSerializer.Serialize(text) + $",\"{field}\":null", text, false);
        yield return new("long-string-bounded-null", $"\"{field}\":" + JsonSerializer.Serialize(new string('x', 257)) + $",\"{field}\":\"bounded\",\"{field}\":null", "bounded", false);
        yield return new("nul-string-bounded-null", $"\"{field}\":\"\\u0000\",\"{field}\":\"bounded\",\"{field}\":null", "bounded", false);
    }

    private sealed record Case(string Label, string Members, string? Expected, bool CurrentRejects, bool ReferenceRejects = false);
    private sealed record Observation(string? ReadError, string? ReadPath, string? Decoded, bool? ControllerDetails, string? WriteError, string? Written);
    private sealed class MutableReference
    {
        public string? Name { get; set; }
        public string? TaxNumber { get; set; }
        public string? Registrar { get; set; }
    }
    private sealed class ProbeStringConverter(bool handleNull) : JsonConverter<string>
    {
        public override bool HandleNull => handleNull;
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String => "probe:" + reader.GetString(),
            JsonTokenType.Null => "probe:null",
            JsonTokenType.Number => "probe:" + Encoding.UTF8.GetString(reader.ValueSpan),
            _ => throw new JsonException("Unsupported test token.")
        };
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        public Segment Append(ReadOnlyMemory<byte> value)
        {
            var next = new Segment(value) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
    // Exact accepted240d metadata method bodies; production remains unchanged.
    private static class FrozenAcceptedMetadata
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
            name.Set = IgnoreNull(assignName);
            var taxNumber = typeInfo.Properties.SingleOrDefault(property => property.Name == nameof(UpsertCompanyRequest.TaxNumber));
            if (taxNumber is not null && taxNumber.PropertyType == typeof(string) && taxNumber.Set is { } assignTaxNumber)
                taxNumber.Set = IgnoreNull(assignTaxNumber);
            var registrar = typeInfo.Properties.SingleOrDefault(property => property.Name == nameof(UpsertCompanyRequest.Registrar));
            if (registrar is not null && registrar.PropertyType == typeof(string) && registrar.Set is { } assignRegistrar)
                registrar.Set = IgnoreNull(assignRegistrar);
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
}
