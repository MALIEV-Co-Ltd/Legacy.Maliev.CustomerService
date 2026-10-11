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

public sealed class CompanyOrdinaryDuplicateNullReferenceTests(ITestOutputHelper output)
{
    [Fact]
    public void OrdinaryDuplicateNames_MatchPinnedReferenceForOrderedNullAndEscapedCaseVariants()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var validateDetails = typeof(CompaniesController)
                .GetMethod("HasCompanyDetails", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<Func<UpsertCompanyRequest, bool>>();
            var numeric256 = new string('1', 256);
            var numeric257 = new string('1', 257);
            var utf16Name256 = new string('ก', 252) + "😀้ ";
            Assert.Equal(256, utf16Name256.Length);
            var quoted256 = JsonSerializer.Serialize(utf16Name256);
            var quoted257 = JsonSerializer.Serialize(utf16Name256 + " ");
            var cases = new[]
            {
                new NameCase("missing", "", null, null, false, false),
                new NameCase("all-null", "\"Name\":null,\"name\":null,\"\\u004Eame\":null", null, null, false, false),
                new NameCase("null-before-value", "\"Name\":null,\"Name\":\"first\"", "first", "first", true, true),
                new NameCase("null-after-value", "\"Name\":\"first\",\"Name\":null", "first", null, true, true),
                new NameCase("null-between-values", "\"Name\":\"first\",\"Name\":null,\"Name\":\"second\"", "second", "second", true, true),
                new NameCase("repeated-final-nulls", "\"Name\":\"first\",\"Name\":null,\"Name\":null", "first", null, true, true),
                new NameCase("case-variants", "\"NAME\":\"first\",\"name\":\"second\",\"NaMe\":null", "second", null, true, true),
                new NameCase("escaped-leading-uppercase", "\"Name\":\"first\",\"\\u004Eame\":\"second\",\"NAME\":null", "second", null, true, true),
                new NameCase("escaped-leading-lowercase", "\"\\u006Eame\":\"first\",\"Name\":null,\"name\":\"second\"", "second", "second", true, true),
                new NameCase("escaped-middle-key", "\"Na\\u006De\":\"first\",\"name\":null", "first", null, true, true),
                new NameCase("literal-thai-surrogate-pair", "\"Name\":\" ก้ 😀 \\t \"", " ก้ 😀 \t ", " ก้ 😀 \t ", true, true),
                new NameCase("escaped-thai-surrogate-pair", "\"Name\":\"\\u0E01\\u0E49 \\uD83D\\uDE00\",\"name\":null", "ก้ 😀", null, true, true),
                new NameCase("boolean-true-null", "\"Name\":true,\"Name\":null", "true", null, true, true),
                new NameCase("boolean-false-final", "\"Name\":\"first\",\"Name\":false", "false", "false", true, true),
                new NameCase("boolean-last-non-null", "\"Name\":true,\"Name\":null,\"Name\":false,\"Name\":null", "false", null, true, true),
                new NameCase("unbounded-exponent-lexeme", "\"Name\":1e309,\"Name\":null", "1e309", null, true, true),
                new NameCase("numeric-spelling-final", "\"Name\":1e309,\"Name\":1.500", "1.500", "1.500", true, true),
                new NameCase("numeric256-null", "\"Name\":" + numeric256 + ",\"Name\":null", numeric256, null, true, true),
                new NameCase("earlier-numeric257-bounded", "\"Name\":" + numeric257 + ",\"Name\":null,\"Name\":\"bounded\"", "bounded", "bounded", true, true),
                new NameCase("final-numeric257-null", "\"Name\":\"bounded\",\"Name\":" + numeric257 + ",\"Name\":null", numeric257, null, false, false),
                new NameCase("utf16-256-null", "\"Name\":" + quoted256 + ",\"Name\":null", utf16Name256, null, true, true),
                new NameCase("earlier-utf16-257-bounded", "\"Name\":" + quoted257 + ",\"Name\":\"bounded\"", "bounded", "bounded", true, true),
                new NameCase("final-utf16-257-null", "\"Name\":\"bounded\",\"Name\":" + quoted257 + ",\"Name\":null", utf16Name256 + " ", null, false, false),
                new NameCase("empty-value-null", "\"Name\":\"\",\"Name\":null", "", null, true, false),
                new NameCase("whitespace-value-null", "\"Name\":\" \\t \",\"Name\":null", " \t ", null, true, false),
                new NameCase("whitespace-with-tax", "\"Name\":\" \\t \",\"Name\":null", " \t ", null, true, true, "tax"),
                new NameCase("missing-name-with-tax", "", null, null, false, false, "tax"),
            };

            foreach (var culture in new[] { "en-US", "de-DE", "th-TH" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                var baselineOptions = MvcOptions();
                var candidateOptions = MvcOptions();
                ComposeCandidate(candidateOptions);
                Assert.NotSame(baselineOptions, candidateOptions);

                foreach (var item in cases)
                {
                    var json = Body(item.Members, item.TaxNumber);
                    var reference = Newtonsoft.Json.JsonConvert.DeserializeObject<MutableReference>(json,
                        new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore })!;
                    Assert.Equal(item.Name, reference.Name);
                    var referenceRequest = new UpsertCompanyRequest(reference.Name!, reference.TaxNumber, reference.Registrar);
                    foreach (var segmented in new[] { false, true })
                    {
                        // Complete-object framework reads exercise duplicate assignment and constructor behavior.
                        var baseline = Read(json, baselineOptions, segmented);
                        var candidate = Read(json, candidateOptions, segmented);
                        Assert.Equal(item.BaselineName, baseline.Name);
                        Assert.Equal(referenceRequest, candidate);
                        Assert.Equal(item.Storable, CompanyNameLengthAttribute.IsStorable(candidate.Name));
                        // The 256-unit final guard remains the real controller guard, outside the converter.
                        Assert.Equal(item.HasDetails, validateDetails(candidate));
                        Assert.Equal(validateDetails(referenceRequest), validateDetails(candidate));

                        // A successful read is established before writing; write errors cannot mask read errors.
                        var expectedWire = JsonSerializer.Serialize(referenceRequest, baselineOptions);
                        var candidateWire = JsonSerializer.Serialize(candidate, candidateOptions);
                        Assert.Equal(expectedWire, candidateWire);
                        output.WriteLine(JsonSerializer.Serialize(new
                        {
                            Culture = culture,
                            Case = item.Label,
                            Segmented = segmented,
                            Name = candidate.Name,
                            FinalControllerShapeAccepted = item.HasDetails,
                            Reference = "Newtonsoft.Json [13.0.3] on net10; original net8 runtime UNPROVEN",
                        }));
                    }
                }

                foreach (var invalid in new[] { "{}", "[]", "\"\\uD800\"", "\"\\uDC00\"", "\"\\uDC00\\uD800\"" })
                {
                    foreach (var members in new[]
                    {
                        "\"Name\":" + invalid + ",\"Name\":\"later-valid\"",
                        "\"Name\":\"earlier-valid\",\"name\":" + invalid + ",\"Name\":null,\"Name\":\"later-valid\"",
                        "\"Name\":null,\"\\u004Eame\":" + invalid + ",\"Name\":null",
                    })
                    {
                        var json = Body(members, null);
                        foreach (var segmented in new[] { false, true })
                        {
                            // Every malformed non-null occurrence fails even when followed by a valid assignment.
                            Assert.Throws<JsonException>(() => Read(json, baselineOptions, segmented));
                            Assert.Throws<JsonException>(() => Read(json, candidateOptions, segmented));
                        }
                        // Newtonsoft comparisons are limited to valid Unicode tokens, not malformed-surrogate parity.
                        if (invalid is "{}" or "[]")
                            Assert.ThrowsAny<Newtonsoft.Json.JsonException>(() =>
                                Newtonsoft.Json.JsonConvert.DeserializeObject<MutableReference>(json));
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
        var metadata = typeof(Program).Assembly.GetType(
            "Legacy.Maliev.CustomerService.Api.OrdinaryCompanyMvcJsonMetadata", throwOnError: true)!;
        var compose = metadata.GetMethod("Compose", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<IJsonTypeInfoResolver?, IJsonTypeInfoResolver?>>();
        options.TypeInfoResolver = compose(options.TypeInfoResolver);
    }

    private static string Body(string members, string? taxNumber) =>
        "{" + (members.Length == 0 ? "" : members + ",") + "\"TaxNumber\":" +
        JsonSerializer.Serialize(taxNumber) + ",\"Registrar\":\"registrar\"}";

    private static UpsertCompanyRequest Read(string json, JsonSerializerOptions options, bool segmented)
    {
        if (!segmented) return JsonSerializer.Deserialize<UpsertCompanyRequest>(json, options)!;
        var bytes = Encoding.UTF8.GetBytes(json);
        var first = new Segment(bytes.AsMemory(0, 1));
        var last = first;
        // Each byte boundary, including number tokens and multibyte Thai/emoji, lies in the sequence.
        for (var index = 1; index < bytes.Length; index++) last = last.Append(bytes.AsMemory(index, 1));
        var reader = new Utf8JsonReader(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length));
        return JsonSerializer.Deserialize<UpsertCompanyRequest>(ref reader, options)!;
    }

    private sealed record NameCase(string Label, string Members, string? Name, string? BaselineName,
        bool Storable, bool HasDetails, string? TaxNumber = null);

    private sealed class MutableReference
    {
        public string? Name { get; set; }
        public string? TaxNumber { get; set; }
        public string? Registrar { get; set; }
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) { Memory = memory; }

        public Segment Append(ReadOnlyMemory<byte> nextMemory)
        {
            var next = new Segment(nextMemory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }

    }
}
