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

/// <summary>Compares ordinary company tax and registrar projection with a pinned public Newtonsoft reference and a frozen name-only native control.</summary>
/// <param name="output">The test output sink recording culture, segmentation, field projections, and explicit current-runtime reference scope.</param>
public sealed class CompanyOrdinaryTaxRegistrarReferenceTests(ITestOutputHelper output)
{
    /// <summary>Checks ordered nulls, escaped property names, primitive text, segmentation, and malformed UTF-16 read and write outcomes against explicit controls.</summary>
    [Fact]
    public void OrdinaryTaxRegistrar_MatchPinnedReferenceForOrderedNullStringsAndEscapedCaseVariants()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var details = typeof(CompaniesController).GetMethod("HasCompanyDetails", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<Func<UpsertCompanyRequest, bool>>();
            var cases = FieldCases("TaxNumber").Concat(FieldCases("Registrar")).Concat(new[]
            {
                new Case("both-interleaved", "\"Name\":\"ok\",\"TaxNumber\":\"tax\",\"Registrar\":null,\"taxnumber\":null,\"Registrar\":\"registrar\",\"REGISTRAR\":null", "ok", "tax", "registrar", "ok", null, null, true),
                new Case("both-last-text", "\"Name\":\"ok\",\"TaxNumber\":null,\"Registrar\":\"first\",\"TaxNumber\":\"tax\",\"Registrar\":null,\"Registrar\":\"last\"", "ok", "tax", "last", "ok", "tax", "last", true),
                new Case("both-escaped-case", "\"NAME\":\"ok\",\"\\u0054axNumber\":\"tax\",\"\\u0052egistrar\":\"registrar\",\"TAXNUMBER\":null,\"registrar\":null", "ok", "tax", "registrar", "ok", null, null, true),
                new Case("both-empty-whitespace", "\"Name\":\"ok\",\"TaxNumber\":\"\",\"Registrar\":\" \\t \",\"TaxNumber\":null,\"Registrar\":null", "ok", "", " \t ", "ok", null, null, true),
                new Case("both-null-only", "\"Name\":\"ok\",\"TaxNumber\":null,\"Registrar\":null", "ok", null, null, "ok", null, null, true),
                new Case("missing-name-with-details", "\"TaxNumber\":\"tax\",\"Registrar\":\"registrar\"", null, "tax", "registrar", null, "tax", "registrar", false),
                new Case("name-null-retained", "\"Name\":\"ok\",\"Name\":null,\"TaxNumber\":\"tax\",\"TaxNumber\":null", "ok", "tax", null, "ok", null, null, true),
                new Case("name-whitespace-tax-retained", "\"Name\":\" \\t \",\"Name\":null,\"TaxNumber\":\"tax\",\"TaxNumber\":null", " \t ", "tax", null, " \t ", null, null, true),
                new Case("name-whitespace-empty-details", "\"Name\":\" \\t \",\"TaxNumber\":\"\",\"TaxNumber\":null,\"Registrar\":\" \\t \",\"Registrar\":null", " \t ", "", " \t ", " \t ", null, null, false),
                new Case("name-earlier257-bounded", "\"Name\":" + JsonSerializer.Serialize(new string('x', 257)) + ",\"Name\":\"ok\",\"TaxNumber\":\"tax\",\"TaxNumber\":null", "ok", "tax", null, "ok", null, null, true),
                new Case("name-final257-null", "\"Name\":\"ok\",\"Name\":" + JsonSerializer.Serialize(new string('x', 257)) + ",\"Name\":null,\"Registrar\":\"registrar\",\"Registrar\":null", new string('x', 257), null, "registrar", new string('x', 257), null, null, false),
            }).ToArray();
            foreach (var culture in new[] { "en-US", "de-DE", "th-TH" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                var baseline = MvcOptions();
                InstallFrozenNameOnlyBaseline(baseline);
                var candidate = MvcOptions();
                ComposeCandidate(candidate);
                foreach (var item in cases)
                {
                    var json = "{" + item.Members + "}";
                    var expected = new UpsertCompanyRequest(item.Name!, item.TaxNumber, item.Registrar);
                    var accepted = new UpsertCompanyRequest(item.BaselineName!, item.BaselineTax, item.BaselineRegistrar);
                    var reference = Newtonsoft.Json.JsonConvert.DeserializeObject<MutableReference>(json,
                        new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore })!;
                    Assert.Equal(expected, new UpsertCompanyRequest(reference.Name!, reference.TaxNumber, reference.Registrar));
                    foreach (var segmented in new[] { false, true })
                    {
                        var oldValue = Read(json, baseline, segmented);
                        var newValue = Read(json, candidate, segmented);
                        Assert.Equal(accepted, oldValue);
                        Assert.Equal(expected, newValue);
                        Assert.Equal(item.HasDetails, details(newValue));
                        Assert.Equal(details(expected), details(newValue));
                        // Reads have already succeeded. No write failure can substitute for a read failure.
                        Assert.Equal(JsonSerializer.Serialize(expected, baseline), JsonSerializer.Serialize(newValue, candidate));
                        output.WriteLine(JsonSerializer.Serialize(new
                        {
                            Culture = culture,
                            Case = item.Label,
                            Segmented = segmented,
                            TaxNumber = newValue.TaxNumber,
                            Registrar = newValue.Registrar,
                            Reference = "Newtonsoft.Json [13.0.3] on net10; original historical net8 runtime UNPROVEN",
                        }));
                    }
                }
                foreach (var field in new[] { "TaxNumber", "Registrar" })
                {
                    foreach (var invalid in new[] { "{}", "[]", "true", "false", "42", "1e309" })
                        foreach (var members in Occurrences(field, invalid))
                            foreach (var segmented in new[] { false, true })
                            {
                                var json = "{\"Name\":\"ok\"," + members + "}";
                                Assert.Throws<JsonException>(() => Read(json, baseline, segmented));
                                if (invalid is "{}" or "[]")
                                    Assert.Throws<JsonException>(() => Read(json, candidate, segmented));
                                else
                                {
                                    var reference = Newtonsoft.Json.JsonConvert.DeserializeObject<MutableReference>(json,
                                        new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore })!;
                                    var expected = new UpsertCompanyRequest(reference.Name!, reference.TaxNumber, reference.Registrar);
                                    var actual = Read(json, candidate, segmented);
                                    Assert.Equal(expected, actual);
                                    Assert.True(details(actual));
                                    Assert.Equal(JsonSerializer.Serialize(expected, baseline), JsonSerializer.Serialize(actual, candidate));
                                    output.WriteLine(JsonSerializer.Serialize(new
                                    {
                                        Kind = "older-reference-primitive-transition",
                                        Culture = culture,
                                        Field = field,
                                        Token = invalid,
                                        Segmented = segmented,
                                        CandidateDecodedWriteMatchPinnedReference = true,
                                        FrozenNameOnlyBaselineRead = "System.Text.Json.JsonException",
                                    }));
                                }
                            }
                    foreach (var malformed in new[] { "\"\\uD800\"", "\"\\uDC00\"", "\"\\uDC00\\uD800\"" })
                        foreach (var members in Occurrences(field, malformed))
                            foreach (var segmented in new[] { false, true })
                            {
                                var json = "{\"Name\":\"ok\"," + members + "}";
                                // Keep actual built-in string behavior, including decoded UTF16, and compare read/write stages separately.
                                // Do not assume a new Unicode guard or claim Newtonsoft/historical-runtime malformed parity.
                                var acceptedOutcome = Observe(json, baseline, segmented);
                                var candidateOutcome = Observe(json, candidate, segmented);
                                if (acceptedOutcome.ReadError is not null)
                                    Assert.Equal(acceptedOutcome, candidateOutcome);
                                else
                                {
                                    // If the built-in string read succeeds, only null assignment changes. Keep its decoded units/write result.
                                    var nonNullMembers = members.Replace($"\"{field}\":null,", "", StringComparison.Ordinal)
                                        .Replace($",\"{field}\":null", "", StringComparison.Ordinal);
                                    var expectedOutcome = Observe("{\"Name\":\"ok\"," + nonNullMembers + "}", baseline, segmented);
                                    Assert.Null(expectedOutcome.ReadError);
                                    Assert.Equal(expectedOutcome, candidateOutcome);
                                }
                            }
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static IEnumerable<Case> FieldCases(string field)
    {
        var escaped = field == "TaxNumber" ? "\\u0054axNumber" : "\\u0052egistrar";
        var middle = field == "TaxNumber" ? "Tax\\u004Eumber" : "Reg\\u0069strar";
        var variants = new[]
        {
            new FieldCase("missing", "", null, null),
            new FieldCase("null-only", $"\"{field}\":null,\"{field.ToLowerInvariant()}\":null", null, null),
            new FieldCase("null-before", $"\"{field}\":null,\"{field}\":\"first\"", "first", "first"),
            new FieldCase("null-between", $"\"{field}\":\"first\",\"{field}\":null,\"{field}\":\"last\"", "last", "last"),
            new FieldCase("null-after", $"\"{field}\":\"first\",\"{field}\":null", "first", null),
            new FieldCase("multiple-text-final-nulls", $"\"{field}\":\"first\",\"{field}\":\"last\",\"{field}\":null,\"{field}\":null", "last", null),
            new FieldCase("empty-retained", $"\"{field}\":\"\",\"{field}\":null", "", null),
            new FieldCase("whitespace-retained", $"\"{field}\":\" \\t \",\"{field}\":null", " \t ", null),
            new FieldCase("literal-thai-pair", $"\"{field}\":\" ก้ 😀 \\t \",\"{field}\":null", " ก้ 😀 \t ", null),
            new FieldCase("escaped-thai-pair", $"\"{field}\":\"\\u0E01\\u0E49 \\uD83D\\uDE00\",\"{field}\":null", "ก้ 😀", null),
            new FieldCase("case-variants", $"\"{field.ToUpperInvariant()}\":\"first\",\"{field.ToLowerInvariant()}\":\"last\",\"{field}\":null", "last", null),
            new FieldCase("escaped-leading", $"\"{escaped}\":\"first\",\"{field}\":null", "first", null),
            new FieldCase("escaped-middle", $"\"{middle}\":\"first\",\"{field.ToLowerInvariant()}\":null", "first", null),
        };
        foreach (var item in variants)
            yield return new Case(field + "/" + item.Label, "\"Name\":\"ok\"" + (item.Members.Length == 0 ? "" : "," + item.Members),
                "ok", field == "TaxNumber" ? item.Value : null, field == "Registrar" ? item.Value : null,
                "ok", field == "TaxNumber" ? item.BaselineValue : null, field == "Registrar" ? item.BaselineValue : null, true);
    }

    private static string[] Occurrences(string field, string token) =>
    [
        $"\"{field}\":{token},\"{field}\":\"later-valid\"",
        $"\"{field}\":\"earlier-valid\",\"{field.ToLowerInvariant()}\":{token},\"{field}\":null,\"{field}\":\"later-valid\"",
        $"\"{field}\":null,\"{field}\":{token},\"{field}\":null",
    ];

    private static JsonSerializerOptions MvcOptions()
    {
        var options = new JsonOptions().JsonSerializerOptions;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.PropertyNamingPolicy = null;
        options.DictionaryKeyPolicy = null;
        return options;
    }

    private static void ComposeCandidate(JsonSerializerOptions options)
    {
        var helper = typeof(Program).Assembly.GetType("Legacy.Maliev.CustomerService.Api.OrdinaryCompanyMvcJsonMetadata", throwOnError: true)!;
        var compose = helper.GetMethod("Compose", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<IJsonTypeInfoResolver?, IJsonTypeInfoResolver?>>();
        options.TypeInfoResolver = compose(options.TypeInfoResolver);
    }

    private static void InstallFrozenNameOnlyBaseline(JsonSerializerOptions options)
    {
        Assert.IsType<DefaultJsonTypeInfoResolver>(options.TypeInfoResolver);
        options.TypeInfoResolver = options.TypeInfoResolver!.WithAddedModifier(info =>
        {
            if (info.Type != typeof(UpsertCompanyRequest) || info.Kind != JsonTypeInfoKind.Object || info.IsReadOnly
                || info.CreateObject is not null || info.Options.DefaultIgnoreCondition != JsonIgnoreCondition.WhenWritingNull
                || !info.Options.PropertyNameCaseInsensitive || info.Options.PropertyNamingPolicy is not null
                || info.Options.DictionaryKeyPolicy is not null || info.Options.ReferenceHandler is not null
                || info.Options.RespectRequiredConstructorParameters || info.Options.RespectNullableAnnotations
                || info.Options.PreferredObjectCreationHandling != JsonObjectCreationHandling.Replace || info.Options.IgnoreReadOnlyProperties)
                return;
            var name = info.Properties.Single(property => property.Name == nameof(UpsertCompanyRequest.Name));
            Assert.Equal(typeof(string), name.PropertyType);
            var setter = name.Set!;
            Assert.NotNull(setter);
            info.CreateObject = static () => new UpsertCompanyRequest(null!, null, null);
            name.Set = (instance, value) => { if (value is not null) setter(instance, value); };
        });
    }

    private static UpsertCompanyRequest Read(string json, JsonSerializerOptions options, bool segmented)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (!segmented) return JsonSerializer.Deserialize<UpsertCompanyRequest>(bytes, options)!;
        var first = new Segment(bytes.AsMemory(0, 1));
        var last = first;
        for (var index = 1; index < bytes.Length; index++) last = last.Append(bytes.AsMemory(index, 1));
        var reader = new Utf8JsonReader(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length));
        return JsonSerializer.Deserialize<UpsertCompanyRequest>(ref reader, options)!;
    }

    private static ReadOutcome Observe(string json, JsonSerializerOptions options, bool segmented)
    {
        UpsertCompanyRequest value;
        try { value = Read(json, options, segmented); }
        catch (JsonException error) { return new(error.GetType().FullName, error.Path, null, null, null, null); }
        catch (InvalidOperationException error) { return new(error.GetType().FullName, null, null, null, null, null); }
        catch (NotSupportedException error) { return new(error.GetType().FullName, null, null, null, null, null); }
        var fields = string.Join("|", new[] { value.Name, value.TaxNumber, value.Registrar }.Select(Utf16));
        try { return new(null, null, fields, null, null, JsonSerializer.Serialize(value, options)); }
        catch (JsonException error) { return new(null, null, fields, error.GetType().FullName, error.Path, null); }
        catch (InvalidOperationException error) { return new(null, null, fields, error.GetType().FullName, null, null); }
        catch (NotSupportedException error) { return new(null, null, fields, error.GetType().FullName, null, null); }
    }

    private static string Utf16(string? value) => value is null ? "null" : string.Concat(value.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)));
    private sealed record ReadOutcome(string? ReadError, string? ReadPath, string? DecodedFields, string? WriteError, string? WritePath, string? Wire);
    private sealed record Case(string Label, string Members, string? Name, string? TaxNumber, string? Registrar,
        string? BaselineName, string? BaselineTax, string? BaselineRegistrar, bool HasDetails);
    private sealed record FieldCase(string Label, string Members, string? Value, string? BaselineValue);
    private sealed class MutableReference
    {
        public string? Name { get; set; }
        public string? TaxNumber { get; set; }
        public string? Registrar { get; set; }
    }
    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) { Memory = memory; }
        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
