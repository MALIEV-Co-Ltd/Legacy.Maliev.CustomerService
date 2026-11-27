using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Tests.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Stj = System.Text.Json.JsonSerializer;

namespace Legacy.Maliev.CustomerService.Tests.OriginalInput;

/// <summary>Compares source-shaped Newtonsoft body reads with stock and candidate MVC readers for ordinary address and customer input.</summary>
/// <param name="fixture">The shared fixture providing customer hosts and disposable persistence for these scenarios.</param>
[Collection("Customer original body")]
public sealed class CustomerOriginalBodyReaderTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    private static readonly string[] Kinds = ["address", "customer"];
    private static readonly string[] AddressSlots =
        ["Building", "AddressLine1", "AddressLine2", "City", "State", "PostalCode", "CountryId"];
    private static readonly string[] CustomerSlots =
        ["FirstName", "LastName", "Telephone", "Mobile", "Fax", "Email", "DateOfBirth", "CompanyId", "BillingAddressId", "ShippingAddressId"];

    private static string Body(string kind) => kind == "address"
        ? """{"Building":"B","AddressLine1":"road","AddressLine2":"A2","City":"C","State":"S","PostalCode":"P","CountryId":7}"""
        : """{"FirstName":"N","LastName":"L","Telephone":"T","Mobile":"M","Fax":"F","Email":"e@x","DateOfBirth":"2000-01-02T00:00:00Z","CompanyId":7,"BillingAddressId":8,"ShippingAddressId":9}""";

    private static string Append(string body, string members) => body[..^1] + "," + members + "}";

    private static string Expected(string kind, params (string Field, object? Value)[] changes)
    {
        object?[] values = kind == "address" ? ["B", "road", "A2", "C", "S", "P", 7]
            : ["N", "L", "T", "M", "F", "e@x", new DateTime(2000, 1, 2, 0, 0, 0, DateTimeKind.Utc), 7, 8, 9];
        var slots = kind == "address" ? AddressSlots : CustomerSlots;
        foreach (var change in changes)
        {
            var index = Array.IndexOf(slots, change.Field);
            Assert.True(index >= 0);
            values[index] = change.Value;
        }
        return Stj.Serialize(values);
    }

    private static string Empty(string kind) => kind == "address"
        ? """[null,null,null,null,null,null,0]""" : """[null,null,null,null,null,null,null,null,null,null]""";

    private static object[] Row(string kind, string label, string json, string sourceKind, string nativeKind,
        string? projection = null, string? nativeProjection = null) =>
        [kind, label, json, sourceKind, nativeKind, projection ?? Expected(kind), nativeProjection ?? projection ?? Expected(kind)];

    /// <summary>Supplies ordinary body cases covering source grammar, duplicate values, primitive tokens, and typed graph members.</summary>
    /// <returns>Theory rows containing JSON bodies, expected reader outcomes, and request-field projections.</returns>
    public static IEnumerable<object[]> Cases()
    {
        foreach (var kind in Kinds)
        {
            var body = Body(kind);
            yield return Row(kind, "standard", body, "success", "success");
            yield return Row(kind, "empty object", "{}", "success", "success", Empty(kind));
            yield return Row(kind, "single quotes", body.Replace('"', '\''), "success", "failure");
            yield return Row(kind, "unquoted names",
                System.Text.RegularExpressions.Regex.Replace(body, "\\\"([A-Za-z][A-Za-z0-9]*)\\\":", "$1:"),
                "success", "failure");
            yield return Row(kind, "leading comments", "/* synthetic */" + body, "success", "failure");
            yield return Row(kind, "member comments", body.Insert(1, "/* synthetic */"), "success", "failure");
            yield return Row(kind, "trailing comma", body.Insert(body.Length - 1, ","), "success", "failure");
            yield return Row(kind, "additional object", body + """{"Id":"private-input-marker"}""", "success", "failure");
            yield return Row(kind, "trailing garbage", body + " private-input-marker", "success", "failure");
            yield return Row(kind, "unknown skipped", Append(body, "\"Unknown\":{\"arbitrary\":[true,{}]}"), "success", "success");
            yield return Row(kind, "whole null", "null", "no-value", "no-value");
            yield return Row(kind, "empty body", "", "no-value", "no-value");
            yield return Row(kind, "whitespace", "  ", "no-value", "failure");
            yield return Row(kind, "empty string root", "\"\"", "no-value", "failure");
            yield return Row(kind, "wrong string root", "\"wrong\"", "failure", "failure");
            yield return Row(kind, "array root", "[]", "failure", "failure");
            yield return Row(kind, "unresolved reference", """{"$ref":"missing"}""", "no-value", "success", nativeProjection: Empty(kind));
            yield return Row(kind, "invalid reference type", """{"$ref":42}""", "failure", "success", nativeProjection: Empty(kind));
            yield return Row(kind, "reference additional members", """{"$ref":"missing","Id":1}""",
                "failure", "success", nativeProjection: Empty(kind));
            yield return Row(kind, "null reference marker", body.Insert(1, "\"$ref\":null,"), "success", "success");
            yield return Row(kind, "type activation disabled",
                body.Insert(1, "\"$type\":\"System.IO.FileInfo, System.Private.CoreLib\","), "success", "success");
            yield return Row(kind, "wrong values wrapper", """{"$values":[]}""", "failure", "success", nativeProjection: Empty(kind));
            yield return Row(kind, "exact source metadata exception", """{"$type":null}""",
                "throw:System.NullReferenceException", "success", nativeProjection: Empty(kind));

            var strings = kind == "address" ? AddressSlots[..6] : CustomerSlots[..6];
            foreach (var field in strings)
            {
                var quoted = "\"" + field + "\"";
                yield return Row(kind, field + " last null", Append(body, quoted + ":\" keep \"," + quoted + ":null"),
                    "success", "success", Expected(kind, (field, " keep ")), Expected(kind, (field, null)));
                yield return Row(kind, field + " null then value", Append(body, quoted + ":null," + quoted + ":\" keep \""),
                    "success", "success", Expected(kind, (field, " keep ")));
                yield return Row(kind, field + " empty then null", Append(body, quoted + ":\"\"," + quoted + ":null"),
                    "success", "success", Expected(kind, (field, "")), Expected(kind, (field, null)));
                yield return Row(kind, field + " primitive then null", Append(body, quoted + ":42," + quoted + ":null"),
                    "success", kind == "address" ? "success" : "failure",
                    Expected(kind, (field, "42")), Expected(kind, (field, null)));
                foreach (var sample in new[]
                {
                    (Label: "lone high", Json: "\"\\ud800\"", Value: "\uFFFD", Native: "failure"),
                    (Label: "lone low", Json: "\"\\udc00\"", Value: "\uFFFD", Native: "failure"),
                    (Label: "escaped pair", Json: "\"\\ud83d\\ude00\"", Value: "\U0001F600", Native: "success"),
                    (Label: "literal replacement", Json: "\"\uFFFD\"", Value: "\uFFFD", Native: "success")
                })
                {
                    yield return Row(kind, field + " " + sample.Label, Append(body, quoted + ":" + sample.Json),
                        "success", sample.Native, Expected(kind, (field, sample.Value)));
                }
            }
        }

        yield return Row("address", "all source primitive strings",
            """{"Building":true,"AddressLine1":false,"AddressLine2":-12.50,"City":1e+02,"State":1e400,"PostalCode":123456789012345678901234567890,"CountryId":7}""",
            "success", "success", Expected("address", ("Building", "true"), ("AddressLine1", "false"),
                ("AddressLine2", "-12.50"), ("City", "1e+02"), ("State", "1e400"), ("PostalCode", "123456789012345678901234567890")));
        yield return Row("customer", "all source primitive strings",
            """{"FirstName":true,"LastName":false,"Telephone":-12.50,"Mobile":1e+02,"Fax":1e400,"Email":123456789012345678901234567890}""",
            "success", "failure", Expected("customer", ("FirstName", "true"), ("LastName", "false"),
                ("Telephone", "-12.50"), ("Mobile", "1e+02"), ("Fax", "1e400"), ("Email", "123456789012345678901234567890"),
                ("DateOfBirth", null), ("CompanyId", null), ("BillingAddressId", null), ("ShippingAddressId", null)));

        foreach (var sample in new[]
        {
            (Label: "last null", Json: "\"CountryId\":7,\"CountryId\":null", Source: 7, Native: 0),
            (Label: "last empty", Json: "\"CountryId\":7,\"CountryId\":\"\"", Source: 7, Native: 0),
            (Label: "explicit zero", Json: "\"CountryId\":7,\"CountryId\":0", Source: 0, Native: 0),
            (Label: "signed whitespace", Json: "\"CountryId\":\" -7 \"", Source: -7, Native: -7),
            (Label: "minimum", Json: "\"CountryId\":-2147483648", Source: int.MinValue, Native: int.MinValue),
            (Label: "maximum", Json: "\"CountryId\":2147483647", Source: int.MaxValue, Native: int.MaxValue)
        })
        {
            yield return Row("address", "country " + sample.Label, Append(Body("address"), sample.Json),
                "success", "success", Expected("address", ("CountryId", sample.Source)), Expected("address", ("CountryId", sample.Native)));
        }
        foreach (var token in new[] { "\" \"", "true", "{}", "[]", "7.0", "2147483648" })
        {
            yield return Row("address", "invalid country " + token, Append(Body("address"), "\"CountryId\":" + token),
                "failure", "failure");
        }
        foreach (var token in new[] { "null", "\"\"" })
        {
            yield return Row("address", "country default " + token, "{\"CountryId\":" + token + "}",
                "success", "success", Empty("address"));
        }

        foreach (var field in new[] { "CompanyId", "BillingAddressId", "ShippingAddressId" })
        {
            var key = "\"" + field + "\"";
            yield return Row("customer", field + " last null", Append(Body("customer"), key + ":17," + key + ":null"),
                "success", "success", Expected("customer", (field, 17)), Expected("customer", (field, null)));
            yield return Row("customer", field + " last empty", Append(Body("customer"), key + ":17," + key + ":\"\""),
                "success", "failure", Expected("customer", (field, 17)));
            yield return Row("customer", field + " signed whitespace", Append(Body("customer"), key + ":\" -7 \""),
                "success", "failure", Expected("customer", (field, -7)));
            yield return Row("customer", field + " null only", "{" + key + ":null}", "success", "success", Empty("customer"));
        }

        yield return Row("customer", "nullable date last null",
            Append(Body("customer"), "\"DateOfBirth\":null"), "success", "success",
            nativeProjection: Expected("customer", ("DateOfBirth", null)));
        yield return Row("customer", "nullable date last empty",
            Append(Body("customer"), "\"DateOfBirth\":\"\""), "success", "failure");
        yield return Row("customer", "nullable date malformed then valid",
            Append(Body("customer"), "\"DateOfBirth\":{},\"DateOfBirth\":\"2000-01-02T00:00:00Z\""), "failure", "failure");

        foreach (var kind in Kinds)
        {
            foreach (var field in kind == "address" ? new[] { "Id", "CreatedDate", "ModifiedDate" }
                : new[] { "Id", "FullName", "CreatedDate", "ModifiedDate" })
            {
                yield return Row(kind, "ignored typed " + field, Append(Body(kind), "\"" + field + "\":{}"),
                    "failure", "success");
            }
            foreach (var type in new[] { "address", "customer", "company" })
            {
                foreach (var field in AllScalars(type))
                {
                    yield return Row(kind, "nested " + type + "." + field,
                        Append(Body(kind), GraphMember(kind, type, "{\"" + field + "\":{\"private-input-marker\":1}}")),
                        "failure", "success");
                }
                foreach (var field in Navigation(type))
                {
                    yield return Row(kind, "nested wrong navigation " + type + "." + field,
                        Append(Body(kind), GraphMember(kind, type, "{\"" + field + "\":42}")), "failure", "success");
                }
            }
            var invalidThenValid = kind == "address"
                ? "\"CustomerBillingAddress\":[{\"Id\":{}}],\"CustomerBillingAddress\":[]"
                : "\"Company\":{\"CreatedDate\":{}},\"Company\":null";
            yield return Row(kind, "earlier malformed graph then valid", Append(Body(kind), invalidThenValid), "failure", "success");
            yield return Row(kind, "complete recognized graph cycle", CompleteGraph(kind), "success", "success");
        }
    }

    private static string[] AllScalars(string type) => type switch
    {
        "address" => ["Id", "Building", "AddressLine1", "AddressLine2", "City", "State", "PostalCode", "CountryId", "CreatedDate", "ModifiedDate"],
        "customer" => ["Id", "FirstName", "LastName", "FullName", "Telephone", "Mobile", "Fax", "Email", "DateOfBirth",
            "CompanyId", "BillingAddressId", "ShippingAddressId", "CreatedDate", "ModifiedDate"],
        "company" => ["Id", "Name", "TaxNumber", "Registrar", "CreatedDate", "ModifiedDate"],
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static string[] Navigation(string type) => type switch
    {
        "address" => ["CustomerBillingAddress", "CustomerShippingAddress"],
        "customer" => ["BillingAddress", "Company", "ShippingAddress"],
        "company" => ["Customer"],
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static string GraphMember(string root, string type, string leaf) => (root, type) switch
    {
        ("address", "address") => "\"CustomerBillingAddress\":[{\"BillingAddress\":" + leaf + "}]",
        ("address", "customer") => "\"CustomerBillingAddress\":[" + leaf + "]",
        ("address", "company") => "\"CustomerShippingAddress\":[{\"Company\":" + leaf + "}]",
        ("customer", "address") => "\"BillingAddress\":" + leaf,
        ("customer", "customer") => "\"BillingAddress\":{\"CustomerShippingAddress\":[" + leaf + "]}",
        ("customer", "company") => "\"Company\":" + leaf,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static string CompleteGraph(string kind)
    {
        const string customer = """
            {"$id":"C","Id":102,"FirstName":"Child","LastName":"Graph","FullName":"Ignored full",
             "Telephone":"T","Mobile":"M","Fax":"F","Email":"e@x","DateOfBirth":"2000-01-02T00:00:00Z",
             "CompanyId":103,"BillingAddressId":101,"ShippingAddressId":101,
             "CreatedDate":"2001-01-01T00:00:00Z","ModifiedDate":"2002-01-01T00:00:00Z",
             "BillingAddress":{"$ref":"A"},"ShippingAddress":{"$ref":"A"},
             "Company":{"$id":"K","Id":103,"Name":"Nested","TaxNumber":"Tax","Registrar":"Registrar",
             "CreatedDate":"2001-01-01T00:00:00Z","ModifiedDate":"2002-01-01T00:00:00Z","Customer":[{"$ref":"C"}]}}
            """;
        if (kind == "address")
        {
            return Append(Body(kind).Insert(1, "\"$id\":\"A\","),
                "\"Id\":101,\"CreatedDate\":\"2001-01-01T00:00:00Z\",\"ModifiedDate\":\"2002-01-01T00:00:00Z\","
                + "\"CustomerBillingAddress\":[" + customer + "],\"CustomerShippingAddress\":[{\"$ref\":\"C\"}]");
        }
        return Append(Body(kind).Insert(1, "\"$id\":\"C\","),
            "\"Id\":102,\"FullName\":\"Ignored full\",\"CreatedDate\":\"2001-01-01T00:00:00Z\",\"ModifiedDate\":\"2002-01-01T00:00:00Z\","
            + "\"BillingAddress\":{\"$id\":\"A\",\"Id\":101,\"Building\":\"B\",\"AddressLine1\":\"road\",\"AddressLine2\":\"A2\","
            + "\"City\":\"C\",\"State\":\"S\",\"PostalCode\":\"P\",\"CountryId\":7,"
            + "\"CreatedDate\":\"2001-01-01T00:00:00Z\",\"ModifiedDate\":\"2002-01-01T00:00:00Z\","
            + "\"CustomerBillingAddress\":[{\"$ref\":\"C\"}],\"CustomerShippingAddress\":[{\"$ref\":\"C\"}]},"
            + "\"ShippingAddress\":{\"$ref\":\"A\"},\"Company\":{\"$id\":\"K\",\"Id\":103,\"Name\":\"Nested\",\"TaxNumber\":\"Tax\","
            + "\"Registrar\":\"Registrar\",\"CreatedDate\":\"2001-01-01T00:00:00Z\",\"ModifiedDate\":\"2002-01-01T00:00:00Z\","
            + "\"Customer\":[{\"$ref\":\"C\"}]}");
    }

    /// <summary>Verifies binding outcomes and projected values before controller validation across the source-shaped, stock, and candidate readers.</summary>
    /// <param name="kind">The address or customer body model to bind.</param>
    /// <param name="label">The descriptive case label supplied by the theory data.</param>
    /// <param name="json">The JSON body read by all three formatter paths.</param>
    /// <param name="sourceKind">The expected outcome category of the source-shaped and candidate readers.</param>
    /// <param name="nativeKind">The expected outcome category of the stock System.Text.Json reader.</param>
    /// <param name="projection">The expected serialized request-field projection for source-shaped and candidate reads.</param>
    /// <param name="nativeProjection">The expected serialized request-field projection for a successful stock read.</param>
    /// <returns>A task representing completion of the source-shaped and MVC body-reader comparisons.</returns>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FullSourceNativeAndCandidate_PinBindingBeforeControllerValidation(string kind, string label, string json,
        string sourceKind, string nativeKind, string projection, string nativeProjection)
    {
        var source = await CustomerOriginalInputOracle.ReadAsync(kind, json);
        Assert.Equal(sourceKind, source.Kind);
        if (source.Kind == "success") Assert.Equal(projection, CustomerOriginalInputOracle.Projection(source.Model));
        await using var host = fixture.Start();
        var services = host.Services;
        var nativeOptions = CustomerOriginalBodyNative.Create(services);
        var candidateOptions = services.GetRequiredService<IOptions<MvcOptions>>().Value;
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var operation in new[] { "create", "update" })
        {
            // Reconstructed native MVC inside the candidate host is not an
            // executed unmodified-target baseline or a historical deployment.
            var native = await CustomerOriginalBodyNative.ReadAsync(services, nativeOptions, kind, bytes, operation);
            Assert.Equal(nativeKind, native.Kind);
            if (native.Kind == "success") Assert.Equal(nativeProjection, CustomerOriginalInputOracle.Projection(native.Model));
            var candidate = await CustomerOriginalBodyNative.ReadAsync(services, candidateOptions, kind, bytes, operation);
            Assert.Equal(sourceKind, candidate.Kind);
            if (candidate.Kind == "success") Assert.Equal(projection, CustomerOriginalInputOracle.Projection(candidate.Model));
            if (candidate.Kind == "failure")
            {
                Assert.True(candidate.ErrorCount > 0);
                Assert.DoesNotContain(candidate.Keys, key => key.Contains("private-input-marker", StringComparison.Ordinal));
                Assert.DoesNotContain(candidate.Messages, message => message.Contains("private-input-marker", StringComparison.Ordinal));
            }
        }
        _ = label;
    }

    /// <summary>Verifies the shared depth limit of 32 for source-shaped, stock, and candidate body reads.</summary>
    /// <param name="kind">The address or customer body model to bind.</param>
    /// <param name="depth">The nesting depth of the unknown JSON member graph.</param>
    /// <param name="expected">The expected reader outcome at the supplied nesting depth.</param>
    /// <returns>A task representing completion of the source-shaped and MVC body-reader comparisons.</returns>
    [Theory]
    [InlineData("address", 31, "success")]
    [InlineData("address", 32, "success")]
    [InlineData("address", 33, "failure")]
    [InlineData("customer", 31, "success")]
    [InlineData("customer", 32, "success")]
    [InlineData("customer", 33, "failure")]
    public async Task OriginalAndMvcDepthBudgets_Are32(string kind, int depth, string expected)
    {
        var json = CustomerOriginalInputOracle.NestedUnknown(depth);
        var source = await CustomerOriginalInputOracle.ReadAsync(kind, json);
        Assert.Equal(expected, source.Kind);
        await using var host = fixture.Start();
        var services = host.Services;
        var bytes = Encoding.UTF8.GetBytes(json);
        var native = await CustomerOriginalBodyNative.ReadAsync(services, CustomerOriginalBodyNative.Create(services), kind, bytes);
        var candidate = await CustomerOriginalBodyNative.ReadAsync(services, services.GetRequiredService<IOptions<MvcOptions>>().Value, kind, bytes);
        Assert.Equal(expected, native.Kind);
        Assert.Equal(expected, candidate.Kind);
    }

    /// <summary>Supplies media type, charset, and byte-encoding combinations for ordinary body reads.</summary>
    /// <returns>Theory rows containing model kinds, media types, encoding flags, and expected reader outcomes.</returns>
    public static IEnumerable<object[]> Encodings()
    {
        foreach (var kind in Kinds)
        {
            yield return [kind, "application/json", false, "success"];
            yield return [kind, "text/json", false, "success"];
            yield return [kind, "application/vnd.synthetic+json", false, "success"];
            yield return [kind, "application/json; charset=utf-16", true, "success"];
            yield return [kind, "application/json; charset=x-private-charset-marker", false, "failure"];
            yield return [kind, "application/json; charset=utf-16BE", false, "failure"];
            yield return [kind, "application/xml", false, "unsupported"];
        }
    }

    /// <summary>Verifies supported JSON media and encoding behavior and safe errors for unsupported charsets.</summary>
    /// <param name="kind">The address or customer body model to bind.</param>
    /// <param name="contentType">The request media type and optional charset.</param>
    /// <param name="unicode">Whether to encode the request bytes as UTF-16; false selects UTF-8.</param>
    /// <param name="expected">The expected success, failure, or unsupported-media outcome.</param>
    /// <returns>A task representing completion of the source-shaped and MVC body-reader comparisons.</returns>
    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task StockMediaEncodingAndSafeUnsupportedCharset_ArePreserved(string kind, string contentType, bool unicode, string expected)
    {
        var bytes = (unicode ? Encoding.Unicode : Encoding.UTF8).GetBytes(Body(kind));
        var source = await CustomerOriginalInputOracle.ReadBytesAsync(CustomerOriginalInputOracle.Formatter(),
            CustomerOriginalInputOracle.SourceType(kind), new EmptyModelMetadataProvider(), bytes, contentType);
        Assert.Equal(expected, source.Kind);
        await using var host = fixture.Start();
        var candidate = await CustomerOriginalBodyNative.ReadAsync(host.Services,
            host.Services.GetRequiredService<IOptions<MvcOptions>>().Value, kind, bytes, contentType: contentType);
        Assert.Equal(expected, candidate.Kind);
        if (candidate.Kind == "success") Assert.Equal(Expected(kind), CustomerOriginalInputOracle.Projection(candidate.Model));
        if (candidate.Kind == "failure")
        {
            Assert.Contains(typeof(UnsupportedContentTypeException).FullName!, candidate.ExceptionTypes);
            Assert.DoesNotContain(candidate.Messages, message => message.Contains("x-private-charset-marker", StringComparison.Ordinal));
        }
    }

    /// <summary>Verifies source graph constructors and reference aliases while restricting candidate output to request fields.</summary>
    /// <returns>A task representing completion of the source-shaped and MVC body-reader comparisons.</returns>
    [Fact]
    public async Task FrozenGraph_ConstructorsReferencesAndAllMembers_AreObservableOnlyInSourceObjects()
    {
        var address = Assert.IsType<SourceAddress>((await CustomerOriginalInputOracle.ReadAsync("address", CompleteGraph("address"))).Model);
        Assert.IsType<HashSet<SourceCustomer>>(address.CustomerBillingAddress);
        Assert.IsType<HashSet<SourceCustomer>>(address.CustomerShippingAddress);
        var child = Assert.Single(address.CustomerBillingAddress);
        Assert.Same(child, Assert.Single(address.CustomerShippingAddress));
        Assert.Same(address, child.BillingAddress);
        Assert.Same(address, child.ShippingAddress);
        Assert.Equal(101, address.Id);
        Assert.Equal(102, child.Id);
        Assert.Equal("Ignored full", child.FullName);
        Assert.Equal(103, child.Company.Id);
        Assert.Equal("Nested", child.Company.Name);
        Assert.Equal("Tax", child.Company.TaxNumber);
        Assert.Equal("Registrar", child.Company.Registrar);
        Assert.Same(child, Assert.Single(child.Company.Customer));
        Assert.IsType<HashSet<SourceCustomer>>(child.Company.Customer);
        Assert.Equal(new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc), address.CreatedDate);
        var customer = Assert.IsType<SourceCustomer>((await CustomerOriginalInputOracle.ReadAsync("customer", CompleteGraph("customer"))).Model);
        Assert.Same(customer.BillingAddress, customer.ShippingAddress);
        Assert.Same(customer, Assert.Single(customer.BillingAddress.CustomerBillingAddress));
        Assert.Same(customer, Assert.Single(customer.Company.Customer));
        Assert.Equal(Expected("customer"), CustomerOriginalInputOracle.Projection(customer));
        Assert.All(new[] { new SourceAddress().CustomerBillingAddress, new SourceAddress().CustomerShippingAddress,
            new SourceCompany().Customer }, collection => Assert.Empty(collection));
        Assert.Null(new SourceCustomer().BillingAddress);
        Assert.Null(new SourceCustomer().ShippingAddress);
        Assert.Null(new SourceCustomer().Company);
    }

    /// <summary>Verifies the frozen source graph has 30 scalars, six typed navigation edges, and unvalidated reference metadata.</summary>
    [Fact]
    public void FrozenSourceGraph_HasOnlyThirtyScalarsSixTypedEdgesAndObliviousUnvalidatedReferences()
    {
        var nullable = new NullabilityInfoContext();
        var total = 0;
        foreach (var type in new[] { "address", "customer", "company" })
        {
            var model = CustomerOriginalInputOracle.SourceType(type);
            var properties = model.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            Assert.Equal(AllScalars(type).Concat(Navigation(type)).Order(StringComparer.Ordinal),
                properties.Select(property => property.Name).Order(StringComparer.Ordinal));
            foreach (var property in properties)
            {
                Assert.True(property.CanRead && property.CanWrite);
                Assert.Empty(property.GetCustomAttributes<ValidationAttribute>());
                Assert.Empty(property.GetCustomAttributes<JsonPropertyAttribute>());
                Assert.Empty(property.GetCustomAttributes<JsonIgnoreAttribute>());
                if (!property.PropertyType.IsValueType)
                {
                    Assert.Equal(NullabilityState.Unknown, nullable.Create(property).ReadState);
                    Assert.Equal(NullabilityState.Unknown, nullable.Create(property).WriteState);
                }
            }
            total += AllScalars(type).Length;
        }
        Assert.Equal(30, total);
        Assert.Equal(typeof(ICollection<SourceCustomer>), typeof(SourceAddress).GetProperty(nameof(SourceAddress.CustomerBillingAddress))!.PropertyType);
        Assert.Equal(typeof(ICollection<SourceCustomer>), typeof(SourceAddress).GetProperty(nameof(SourceAddress.CustomerShippingAddress))!.PropertyType);
        Assert.Equal(typeof(ICollection<SourceCustomer>), typeof(SourceCompany).GetProperty(nameof(SourceCompany.Customer))!.PropertyType);
        Assert.Equal(typeof(SourceAddress), typeof(SourceCustomer).GetProperty(nameof(SourceCustomer.BillingAddress))!.PropertyType);
        Assert.Equal(typeof(SourceAddress), typeof(SourceCustomer).GetProperty(nameof(SourceCustomer.ShippingAddress))!.PropertyType);
        Assert.Equal(typeof(SourceCompany), typeof(SourceCustomer).GetProperty(nameof(SourceCustomer.Company))!.PropertyType);
    }

    /// <summary>Verifies that pooled reads do not reuse reference identifiers or duplicate-null state across requests.</summary>
    /// <returns>A task representing completion of the source-shaped and MVC body-reader comparisons.</returns>
    [Fact]
    public async Task PooledReaders_DoNotCarryReferencesOrNullHistoryIntoLaterRequests()
    {
        await using var host = fixture.Start();
        var services = host.Services;
        var options = services.GetRequiredService<IOptions<MvcOptions>>().Value;
        foreach (var kind in Kinds)
        {
            var first = await CustomerOriginalBodyNative.ReadAsync(services, options, kind, Encoding.UTF8.GetBytes(CompleteGraph(kind)));
            Assert.Equal("success", first.Kind);
            var previousId = kind == "address" ? "A" : "C";
            var unresolved = await CustomerOriginalBodyNative.ReadAsync(services, options, kind,
                Encoding.UTF8.GetBytes("{\"$ref\":\"" + previousId + "\"}"));
            Assert.Equal("no-value", unresolved.Kind);
            var empty = await CustomerOriginalBodyNative.ReadAsync(services, options, kind, Encoding.UTF8.GetBytes("{}"));
            Assert.Equal("success", empty.Kind);
            Assert.Equal(Empty(kind), CustomerOriginalInputOracle.Projection(empty.Model));
        }
    }

    /// <summary>Verifies source-shaped MVC serializer defaults, depth, buffering, and additional-content behavior.</summary>
    [Fact]
    public void OfficialMvcSettings_RetainSourceDefaultsWithoutJsonConvertAdditionalContentBehavior()
    {
        var options = CustomerOriginalInputOracle.OriginalOptions();
        Assert.Equal(NullValueHandling.Ignore, options.SerializerSettings.NullValueHandling);
        Assert.Equal(ReferenceLoopHandling.Ignore, options.SerializerSettings.ReferenceLoopHandling);
        Assert.Equal(TypeNameHandling.None, options.SerializerSettings.TypeNameHandling);
        Assert.Equal(DefaultValueHandling.Include, options.SerializerSettings.DefaultValueHandling);
        Assert.Equal(ObjectCreationHandling.Auto, options.SerializerSettings.ObjectCreationHandling);
        Assert.Equal(MetadataPropertyHandling.Default, options.SerializerSettings.MetadataPropertyHandling);
        Assert.Equal(DateParseHandling.DateTime, options.SerializerSettings.DateParseHandling);
        Assert.Equal(32, options.SerializerSettings.MaxDepth);
        Assert.False(options.SerializerSettings.CheckAdditionalContent);
        Assert.False(options.ReadJsonWithRequestCulture);
        Assert.Equal(30 * 1024, options.InputFormatterMemoryBufferThreshold);
    }
    /// <summary>Verifies that the custom reader selects only ordinary address and unkeyed customer create or update actions.</summary>
    /// <returns>A task representing completion of the source-shaped and MVC body-reader comparisons.</returns>
    [Fact]
    public async Task ExactSelectors_AdmitOnlyAddressAndUnkeyedOrdinaryCustomerActions()
    {
        await using var host = fixture.Start();
        var services = host.Services;
        var options = services.GetRequiredService<IOptions<MvcOptions>>().Value;
        var owned = Assert.Single(options.InputFormatters, formatter =>
            formatter.GetType().Assembly == typeof(Program).Assembly
            && formatter.GetType().FullName == CustomerOriginalBodyNative.FormatterName);
        foreach (var kind in Kinds)
        {
            foreach (var operation in new[] { "create", "update" })
            {
                var context = CustomerOriginalBodyNative.Context(services, kind, Encoding.UTF8.GetBytes(Body(kind)), operation);
                using var input = context.HttpContext.Request.Body;
                Assert.True(owned.CanRead(context));
                var endpoint = context.HttpContext.GetEndpoint();
                context.HttpContext.SetEndpoint(null);
                Assert.False(owned.CanRead(context));
                context.HttpContext.SetEndpoint(endpoint);
                context.HttpContext.Request.Method = "DELETE";
                Assert.False(owned.CanRead(context));
            }
        }
        var keyed = CustomerOriginalBodyNative.Context(services, "customer", Encoding.UTF8.GetBytes(Body("customer")));
        using var keyedBody = keyed.HttpContext.Request.Body;
        keyed.HttpContext.Request.Headers["Idempotency-Key"] = "00000000-0000-0000-0000-000000000001";
        Assert.False(owned.CanRead(keyed));
        var versioned = CustomerOriginalBodyNative.Context(services, "customer", Encoding.UTF8.GetBytes(Body("customer")), "update");
        using var versionedBody = versioned.HttpContext.Request.Body;
        versioned.HttpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(
            new ControllerActionDescriptor
            {
                ControllerTypeInfo = typeof(global::Legacy.Maliev.CustomerService.Api.Controllers.CustomersController).GetTypeInfo(),
                MethodInfo = typeof(global::Legacy.Maliev.CustomerService.Api.Controllers.CustomersController)
                    .GetMethod("UpdateCustomerVersionedAsync")!
            }), "versioned-control"));
        Assert.False(owned.CanRead(versioned));
        var wrong = CustomerOriginalBodyNative.Context(services, "address", Encoding.UTF8.GetBytes(Body("address")));
        using var wrongBody = wrong.HttpContext.Request.Body;
        wrong.HttpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(
            new ControllerActionDescriptor
            {
                ControllerTypeInfo = typeof(global::Legacy.Maliev.CustomerService.Api.Controllers.CustomersController).GetTypeInfo(),
                MethodInfo = typeof(global::Legacy.Maliev.CustomerService.Api.Controllers.CustomersController)
                    .GetMethod("CreateCustomerAsync")!
            }), "wrong-controller-control"));
        Assert.False(owned.CanRead(wrong));
    }

    /// <summary>Verifies that unowned serializer options disable adapter selection and preserve stock direct-read failures.</summary>
    /// <returns>A task representing completion of the source-shaped and MVC body-reader comparisons.</returns>
    [Fact]
    public async Task UnownedOptions_KeepAddressSelectionClosedAndDelegateDirectReadToStock()
    {
        await using var host = fixture.Start();
        var source = new JsonOptions();
        source.JsonSerializerOptions.PropertyNamingPolicy = null;
        source.JsonSerializerOptions.DictionaryKeyPolicy = null;
        source.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        var native = new SystemTextJsonInputFormatter(source, NullLogger<SystemTextJsonInputFormatter>.Instance);
        var type = typeof(Program).Assembly.GetType(CustomerOriginalBodyNative.FormatterName, true)!;
        var adapter = Assert.IsAssignableFrom<TextInputFormatter>(Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: [source, native, NullLogger<SystemTextJsonInputFormatter>.Instance], culture: null));
        const string json = """{"AddressLine1":"road","Building":true,"CountryId":7}""";
        var baseline = CustomerOriginalBodyNative.Context(host.Services, "address", Encoding.UTF8.GetBytes(json));
        using var baselineBody = baseline.HttpContext.Request.Body;
        var candidate = CustomerOriginalBodyNative.Context(host.Services, "address", Encoding.UTF8.GetBytes(json));
        using var candidateBody = candidate.HttpContext.Request.Body;
        Assert.False(adapter.CanRead(candidate));
        var original = await CustomerOriginalInputOracle.ObserveAsync(native, baseline);
        Assert.Equal("failure", original.Kind);
        var delegated = await adapter.ReadRequestBodyAsync(candidate, Encoding.UTF8);
        Assert.True(delegated.HasError);
        Assert.Equal(baseline.ModelState.ErrorCount, candidate.ModelState.ErrorCount);
        Assert.Equal(baseline.ModelState.Keys, candidate.ModelState.Keys);
    }

    /// <summary>Verifies that MVC projection leaves direct System.Text.Json record construction, metadata, and serialization unchanged.</summary>
    /// <returns>A task representing completion of the source-shaped and MVC body-reader comparisons.</returns>
    [Fact]
    public async Task InputProjection_DoesNotMutateDirectStjRecordsMetadataOrWrites()
    {
        await using var host = fixture.Start();
        var services = host.Services;
        var jsonOptions = services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var record = new UpsertAddressRequest("B", " road ", "A2", "C", "S", "P", 7);
        var (building, line, line2, city, state, postal, country) = record;
        Assert.Equal(("B", " road ", "A2", "C", "S", "P", 7), (building, line, line2, city, state, postal, country));
        var before = Stj.Serialize(record, jsonOptions);
        const string body = """{"Building":"keep","Building":null,"AddressLine1":"road","CountryId":7,"CountryId":null}""";
        var directBefore = Stj.Deserialize<UpsertAddressRequest>(body, jsonOptions)!;
        Assert.Null(directBefore.Building);
        Assert.Equal(0, directBefore.CountryId);
        Assert.Null(jsonOptions.GetTypeInfo(typeof(UpsertAddressRequest)).CreateObject);
        Assert.Null(jsonOptions.GetTypeInfo(typeof(UpsertCustomerRequest)).CreateObject);
        var candidate = await CustomerOriginalBodyNative.ReadAsync(services,
            services.GetRequiredService<IOptions<MvcOptions>>().Value, "address", Encoding.UTF8.GetBytes(body));
        Assert.Equal("success", candidate.Kind);
        var projected = Assert.IsType<UpsertAddressRequest>(candidate.Model);
        Assert.Equal("keep", projected.Building);
        Assert.Equal(7, projected.CountryId);
        Assert.Equal(directBefore, Stj.Deserialize<UpsertAddressRequest>(body, jsonOptions));
        Assert.Equal(before, Stj.Serialize(record, jsonOptions));
        Assert.Equal(record, record with { });
        Assert.Empty(typeof(UpsertAddressRequest).GetCustomAttributes<System.Text.Json.Serialization.JsonConverterAttribute>());
        Assert.Empty(typeof(UpsertCustomerRequest).GetCustomAttributes<System.Text.Json.Serialization.JsonConverterAttribute>());
    }

    /// <summary>Verifies that primitive spelling and date projection remain stable across English, German, and Thai request cultures.</summary>
    /// <returns>A task representing completion of the source-shaped and MVC body-reader comparisons.</returns>
    [Fact]
    public async Task SourcePrimitiveLexemesAndDates_AreIndependentOfRequestCulture()
    {
        var before = CultureInfo.CurrentCulture;
        var beforeUi = CultureInfo.CurrentUICulture;
        await using var host = fixture.Start();
        try
        {
            foreach (var culture in new[] { "en-US", "de-DE", "th-TH" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                const string json = """{"FirstName":true,"LastName":false,"Telephone":-12.50,"Mobile":1e+02,"Fax":1e400,"Email":"e@x","DateOfBirth":"2000-01-02T00:00:00Z"}""";
                var expected = Expected("customer", ("FirstName", "true"), ("LastName", "false"), ("Telephone", "-12.50"),
                    ("Mobile", "1e+02"), ("Fax", "1e400"), ("CompanyId", null), ("BillingAddressId", null), ("ShippingAddressId", null));
                var source = await CustomerOriginalInputOracle.ReadAsync("customer", json);
                Assert.Equal("success", source.Kind);
                Assert.Equal(expected, CustomerOriginalInputOracle.Projection(source.Model));
                var candidate = await CustomerOriginalBodyNative.ReadAsync(host.Services,
                    host.Services.GetRequiredService<IOptions<MvcOptions>>().Value, "customer", Encoding.UTF8.GetBytes(json));
                Assert.Equal("success", candidate.Kind);
                Assert.Equal(expected, CustomerOriginalInputOracle.Projection(candidate.Model));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
            CultureInfo.CurrentUICulture = beforeUi;
        }
    }

}
