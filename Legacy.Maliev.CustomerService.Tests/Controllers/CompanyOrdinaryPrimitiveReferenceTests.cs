using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Tests.OriginalInput;
using Xunit.Abstractions;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Compares ordinary company-name primitive conversion with a pinned Newtonsoft 13 reference under multiple cultures and segmented input.</summary>
/// <param name="output">The xUnit output sink for recording comparison or process diagnostics.</param>
public sealed class CompanyOrdinaryPrimitiveReferenceTests(ITestOutputHelper output)
{
    /// <summary>Verifies primitive token spelling and string conversion across cultures and segmented reads against the pinned Newtonsoft 13 reference.</summary>
    [Fact]
    public async Task OrdinaryNameConverter_MatchesPinnedReferenceLexemesAcrossCulturesAndSegmentedInput()
    {
        var cultures = new[] { "en-US", "de-DE", "th-TH" };
        var rawValues = new[] { "true", "false", "42", "1.5", "1e3", "1e309", "-1e309", "1e400", "-0", "1E+03", "1.500", "9223372036854775808123456789", new string('1', 256), new string('1', 257), "\" ก้ 😀 \\t \"" };
        var cases = cultures.SelectMany(culture => rawValues.Select(raw => new CustomerOriginalOracleProcess.PrimitiveCase("{\"Name\":" + raw + "}", culture))
            .Concat(new[] { "{}", "[]" }.Select(raw => new CustomerOriginalOracleProcess.PrimitiveCase("{\"Name\":" + raw + "}", culture, false)))
            .Concat(new[] { "{}", "{\"Name\":null}" }.Select(json => new CustomerOriginalOracleProcess.PrimitiveCase(json, culture)))).ToArray();
        var observations = await CustomerOriginalOracleProcess.ReadPrimitivesAsync(cases);
        Assert.Equal(cases.Length, observations.Length);
        var cursor = 0;
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in cultures)
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var raw in rawValues)
                {
                    var json = "{\"Name\":" + raw + "}";
                    var reference = observations[cursor++];
                    Assert.False(reference.IsJsonException);
                    Assert.True(reference.ModelPresent);
                    var ordinary = System.Text.Json.JsonSerializer.Deserialize<UpsertCompanyRequest>(json)!;
                    Assert.Equal(reference.Name, ordinary.Name);
                    Assert.Equal(raw.StartsWith('"') ? System.Text.Json.JsonSerializer.Deserialize<string>(raw) : raw, ordinary.Name);
                    var bytes = Encoding.UTF8.GetBytes(raw);
                    var first = new Segment(bytes.AsMemory(0, 1));
                    var last = first.Append(bytes.AsMemory(1));
                    var reader = new Utf8JsonReader(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length));
                    Assert.True(reader.Read());
                    Assert.Equal(reference.Name, new OrdinaryCompanyNameJsonConverter().Read(ref reader, typeof(string), new JsonSerializerOptions()));
                    Assert.Equal(ordinary.Name.Length <= 256, CompanyNameLengthAttribute.IsStorable(ordinary.Name));
                    output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Culture = culture, Lexeme = raw, Name = ordinary.Name, Reference = "Newtonsoft.Json [13.0.3] on net10; not original runtime" }));
                }
                foreach (var raw in new[] { "{}", "[]" })
                {
                    var json = "{\"Name\":" + raw + "}";
                    Assert.True(observations[cursor++].IsJsonException);
                    Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<UpsertCompanyRequest>(json));
                }
                foreach (var json in new[] { "{}", "{\"Name\":null}" })
                {
                    var reference = observations[cursor++];
                    Assert.False(reference.IsJsonException);
                    Assert.True(reference.ModelPresent);
                    Assert.Null(reference.Name);
                    Assert.Null(System.Text.Json.JsonSerializer.Deserialize<UpsertCompanyRequest>(json)!.Name);
                }
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) { Memory = memory; }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = this.RunningIndex + Memory.Length };
            Next = next;
            return next;
        }

    }
}
