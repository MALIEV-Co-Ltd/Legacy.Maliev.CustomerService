using System.Net;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Exercises literal address input across ordinary and scoped HTTP routes with physical graph, cache, and revision checks.</summary>
/// <param name="fixture">The shared fixture providing customer hosts and disposable persistence for these scenarios.</param>
[Collection("Customer address persistence")]
public sealed class CustomerAddressLiteralLineHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    /// <summary>Verifies optional address string admission and refusal boundaries without unintended graph or cache changes.</summary>
    /// <param name="route">The ordinary or scoped create or update route under test.</param>
    /// <returns>A task representing completion of the address HTTP, physical storage, graph, cache, and revision assertions.</returns>
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task OrdinaryOptionalFields_RefusalsAndStringBoundariesPreserveGraphAndCaches(string route)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = deadline.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var longNumber = new string('1', 257);
        var cases = new[]
        {
            (Raw: "{}", Refusal: (HttpStatusCode?)HttpStatusCode.BadRequest, Literal: (string?)null),
            (Raw: "[]", Refusal: (HttpStatusCode?)HttpStatusCode.BadRequest, Literal: (string?)null),
            (Raw: longNumber, Refusal: (HttpStatusCode?)HttpStatusCode.InternalServerError, Literal: (string?)null),
            (Raw: JsonSerializer.Serialize(longNumber), Refusal: (HttpStatusCode?)HttpStatusCode.InternalServerError, Literal: (string?)null),
            (Raw: "\"\\ud800\"", Refusal: (HttpStatusCode?)null, Literal: "\uFFFD"),
            (Raw: "\"\\udc00\"", Refusal: (HttpStatusCode?)null, Literal: "\uFFFD"),
            (Raw: "\"\uFFFD\"", Refusal: (HttpStatusCode?)null, Literal: "\uFFFD"),
        };
        foreach (var field in new[] { "Building", "AddressLine2", "City", "State", "PostalCode" })
        {
            foreach (var item in cases)
            {
                await SeedAsync(route, token);
                var before = await PhysicalSnapshotAsync(token);
                var cached = await PrimeAsync(cache, token);
                var payload = "{\"AddressLine1\":\"road\",\"CountryId\":764,\"" + field + "\":" + item.Raw + "}";
                using var request = new HttpRequestMessage(route == "create" ? HttpMethod.Post : HttpMethod.Put,
                    route == "create" ? "/customers/1/addresses" : "/customers/Addresses/1")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                using var response = await client.SendAsync(request, token);
                Assert.Equal(item.Refusal ?? (route == "create" ? HttpStatusCode.Created : HttpStatusCode.NoContent), response.StatusCode);
                if (item.Refusal.HasValue)
                {
                    Assert.Equal(before, await PhysicalSnapshotAsync(token));
                    await AssertCacheAsync(cache, cached, [], token);
                }
                else
                {
                    // The original reader normalizes lone escapes before any
                    // canonical storage guard sees the resulting valid scalar.
                    var original = await global::Legacy.Maliev.CustomerService.Tests.OriginalInput.CustomerOriginalInputOracle
                        .ReadAsync("address", payload);
                    Assert.Equal("success", original.Kind);
                    Assert.Equal(item.Literal, original.Model!.GetType().GetProperty(field)!.GetValue(original.Model));
                    Assert.True(AddressLine1LengthAttribute.IsStorable(item.Literal));
                    var id = 1;
                    if (route == "create")
                    {
                        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                        id = json.RootElement.GetProperty("Id").GetInt32();
                        Assert.Equal(item.Literal, json.RootElement.GetProperty(field).GetString());
                        Assert.Equal("road", json.RootElement.GetProperty("AddressLine1").GetString());
                        Assert.EndsWith($"/customers/1/addresses/{id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
                    }
                    await using var db = fixture.Context();
                    var stored = await db.Addresses.AsNoTracking().SingleAsync(value => value.Id == id, token);
                    Assert.Equal(item.Literal, typeof(Address).GetProperty(field)!.GetValue(stored));
                    Assert.Equal("road", stored.AddressLine1);
                    Assert.Equal(764, stored.CountryId);
                    foreach (var other in new[] { "Building", "AddressLine2", "City", "State", "PostalCode" }.Where(value => value != field))
                        Assert.Null(typeof(Address).GetProperty(other)!.GetValue(stored));
                    AssertGraph(before, await PhysicalSnapshotAsync(token), route, id);
                    await AssertCacheAsync(cache, cached, route == "create" ? [1] : [1, 2], token);
                }
            }
            await SeedAsync(route, token);
            var authorityBefore = await PhysicalSnapshotAsync(token);
            var authorityCache = await PrimeAsync(cache, token);
            using var denied = fixture.Client(host, "denied");
            using var denialRequest = new HttpRequestMessage(route == "create" ? HttpMethod.Post : HttpMethod.Put,
                route == "create" ? "/customers/1/addresses" : "/customers/Addresses/1")
            {
                Content = new StringContent("{\"AddressLine1\":\"road\",\"CountryId\":764,\"" + field + "\":true}", Encoding.UTF8, "application/json"),
            };
            using var denial = await denied.SendAsync(denialRequest, token);
            Assert.Equal(HttpStatusCode.Forbidden, denial.StatusCode);
            Assert.Equal(authorityBefore, await PhysicalSnapshotAsync(token));
            await AssertCacheAsync(cache, authorityCache, [], token);
        }
    }

    /// <summary>Verifies that optional primitive address fields retain their token spelling and match Newtonsoft string conversion.</summary>
    /// <param name="route">The ordinary or scoped create or update route under test.</param>
    /// <returns>A task representing completion of the address HTTP, physical storage, graph, cache, and revision assertions.</returns>
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task OrdinaryOptionalFields_PreservePrimitiveLexemesAndMatchReferenceStrings(string route)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = deadline.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var fields = new[] { "Building", "AddressLine2", "City", "State", "PostalCode" };
        var primitives = new[] { (Token: "42", Literal: "42"), (Token: "-0", Literal: "-0"),
            (Token: "1.50", Literal: "1.50"), (Token: "1e+2", Literal: "1e+2"),
            (Token: "true", Literal: "true"), (Token: "false", Literal: "false") };
        var baselineOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = null,
            DictionaryKeyPolicy = null,
        };
        foreach (var field in fields)
            foreach (var item in primitives)
            {
                var reference = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string?>>("{\"" + field + "\":" + item.Token + "}");
                Assert.Equal(item.Literal, reference![field]);
                var primitivePayload = "{\"AddressLine1\":\"  Fixture road  \",\"CountryId\":764,\"" + field + "\":" + item.Token + "}";
                Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertAddressRequest>(primitivePayload, baselineOptions));
                foreach (var submitted in new[] { item.Token, JsonSerializer.Serialize(item.Literal) })
                {
                    await SeedAsync(route, token);
                    var before = await PhysicalSnapshotAsync(token);
                    var cached = await PrimeAsync(cache, token);
                    var payload = "{\"AddressLine1\":\"  Fixture road  \",\"CountryId\":764,\"" + field + "\":" + submitted + "}";
                    using var request = new HttpRequestMessage(route == "create" ? HttpMethod.Post : HttpMethod.Put,
                        route == "create" ? "/customers/1/addresses" : "/customers/Addresses/1")
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                    };
                    using var response = await client.SendAsync(request, token);
                    Assert.Equal(route == "create" ? HttpStatusCode.Created : HttpStatusCode.NoContent, response.StatusCode);
                    var id = 1;
                    if (route == "create")
                    {
                        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                        id = json.RootElement.GetProperty("Id").GetInt32();
                        Assert.Equal(item.Literal, json.RootElement.GetProperty(field).GetString());
                        Assert.Equal("  Fixture road  ", json.RootElement.GetProperty("AddressLine1").GetString());
                        Assert.EndsWith($"/customers/1/addresses/{id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
                    }
                    await using var db = fixture.Context();
                    var stored = await db.Addresses.AsNoTracking().SingleAsync(value => value.Id == id, token);
                    Assert.Equal(item.Literal, typeof(Address).GetProperty(field)!.GetValue(stored));
                    Assert.Equal("  Fixture road  ", stored.AddressLine1);
                    Assert.Equal(764, stored.CountryId);
                    foreach (var other in fields.Where(value => value != field))
                        Assert.Null(typeof(Address).GetProperty(other)!.GetValue(stored));
                    AssertGraph(before, await PhysicalSnapshotAsync(token), route, id);
                    await AssertCacheAsync(cache, cached, route == "create" ? [1] : [1, 2], token);
                }
            }
    }

    /// <summary>Verifies literal address storage and response values while preserving relation bindings, cache scope, and version preconditions.</summary>
    /// <param name="route">The ordinary or scoped create or update route under test.</param>
    /// <returns>A task representing completion of the address HTTP, physical storage, graph, cache, and revision assertions.</returns>
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("billing-create")]
    [InlineData("billing-update")]
    [InlineData("shipping-create")]
    [InlineData("shipping-update")]
    public async Task LiteralLine_PreservesStorageBindingsOptionalFieldsCachesAndVersions(string route)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var scoped = route.Contains('-', StringComparison.Ordinal);
        var create = route.EndsWith("create", StringComparison.Ordinal);
        var cases = new (string Label, string? Line, bool Accepted, bool NullOptional, string? Raw)[]
        {
            ("EnglishLiteral", "  Fixture road  ", true, false, null),
            ("ThaiLiteral", "  ถนน ทดสอบ  ", true, false, null),
            ("MixedPadding", " \t\u00a0ถนน\u00a0\t ", true, false, null),
            ("Ascii256", new string('a', 256), true, false, null),
            ("Ascii257", new string('a', 257), false, false, null),
            ("Thai256", new string('ก', 256), true, false, null),
            ("Thai257", new string('ก', 257), false, false, null),
            ("Supplementary128", string.Concat(Enumerable.Repeat("😀", 128)), true, false, null),
            ("Supplementary129", string.Concat(Enumerable.Repeat("😀", 129)), false, false, null),
            ("MixedThaiSupplementary256Units", new string('ก', 254) + "😀", true, false, null),
            ("MixedThaiSupplementary257Units", new string('ก', 255) + "😀", false, false, null),
            ("Supplementary256", string.Concat(Enumerable.Repeat("😀", 256)), false, false, null),
            ("Supplementary257", string.Concat(Enumerable.Repeat("😀", 257)), false, false, null),
            ("Combining256", string.Concat(Enumerable.Repeat("ก้", 128)), true, false, null),
            ("Combining257", string.Concat(Enumerable.Repeat("ก้", 128)) + "้", false, false, null),
            ("TrailingSpace257", new string('a', 256) + " ", false, false, null),
            ("NulNotStorable", "road\0line", false, false, null),
            ("ReplacementLiteral", "\uFFFD", true, false, null),
            ("LoneHighSurrogateJson", null, false, false, "\"\\ud800\""),
            ("LoneLowSurrogateJson", null, false, false, "\"\\udc00\""),
            ("EmptyLine", "", true, false, null),
            ("WhitespaceLine", " \t\u00a0 ", true, false, null),
            ("NullLine", null, false, false, null),
            ("IntegerLine", "42", true, false, "42"),
            ("DecimalLine", "1.50", true, false, "1.50"),
            ("ExponentLine", "1e+2", true, false, "1e+2"),
            ("TrueLine", "true", true, false, "true"),
            ("FalseLine", "false", true, false, "false"),
            ("NullOptionalFields", "  ถนนไม่มีรายละเอียด  ", true, true, null),
        };
        foreach (var item in cases)
        {
            await SeedAsync(route, token);
            var before = await PhysicalSnapshotAsync(token);
            var cached = await PrimeAsync(cache, token);
            var versions = scoped ? await VersionsAsync(client, route, create ? "new" : "1", token) : default;
            var expectedLine = item.Line;
            var accepted = item.Accepted;
            if (!scoped && (item.Label is "LoneHighSurrogateJson" or "LoneLowSurrogateJson"))
            {
                var original = await global::Legacy.Maliev.CustomerService.Tests.OriginalInput.CustomerOriginalInputOracle
                    .ReadAsync("address", "{\"AddressLine1\":" + item.Raw + ",\"CountryId\":764}");
                Assert.Equal("success", original.Kind);
                expectedLine = (string?)original.Model!.GetType().GetProperty("AddressLine1")!.GetValue(original.Model);
                Assert.Equal("\uFFFD", expectedLine);
                Assert.True(AddressLine1LengthAttribute.IsStorable(expectedLine));
                accepted = true;
            }
            using var request = Request(route, item.Line, item.NullOptional, item.Raw, versions);
            using var result = await client.SendAsync(request, token);
            var expected = accepted ? create && !scoped ? HttpStatusCode.Created : HttpStatusCode.NoContent : item.Label == "NullLine" ? HttpStatusCode.InternalServerError : HttpStatusCode.BadRequest;
            Assert.True(expected == result.StatusCode, $"{route}/{item.Label}: expected {expected}, actual {result.StatusCode}");
            if (!accepted)
            {
                Assert.Equal(before, await PhysicalSnapshotAsync(token));
                await AssertCacheAsync(cache, cached, [], token);
                continue;
            }

            Assert.True(expectedLine!.Length <= 256);
            var id = 1;
            if (scoped)
            {
                id = int.Parse(result.Headers.GetValues("X-Relation-Id").Single(), System.Globalization.CultureInfo.InvariantCulture);
                using var read = await client.GetAsync(ReadPath(route, id.ToString(System.Globalization.CultureInfo.InvariantCulture)), token);
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                Assert.Equal(result.Headers.ETag, read.Headers.ETag);
                Assert.Equal(result.Headers.GetValues("X-Customer-ETag").Single(), read.Headers.GetValues("X-Customer-ETag").Single());
                using var readBody = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
                AssertLiteral(readBody.RootElement.GetProperty("Address"), expectedLine!, item.NullOptional);
            }
            else if (create)
            {
                using var created = JsonDocument.Parse(await result.Content.ReadAsStringAsync(token));
                id = created.RootElement.GetProperty("Id").GetInt32();
                AssertLiteral(created.RootElement, expectedLine!, item.NullOptional);
                Assert.EndsWith($"/customers/1/addresses/{id}", result.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
                using var unboundRead = await client.GetAsync(result.Headers.Location, token);
                Assert.Equal(HttpStatusCode.NotFound, unboundRead.StatusCode);
            }

            await using var db = fixture.Context();
            var stored = await db.Addresses.AsNoTracking().SingleAsync(row => row.Id == id, token);
            Assert.Equal(expectedLine, stored.AddressLine1);
            Assert.Equal(Optional("Building", item.NullOptional), stored.Building);
            Assert.Equal(Optional("AddressLine2", item.NullOptional), stored.AddressLine2);
            Assert.Equal(Optional("City", item.NullOptional), stored.City);
            Assert.Equal(Optional("State", item.NullOptional), stored.State);
            Assert.Equal(Optional("PostalCode", item.NullOptional), stored.PostalCode);
            Assert.Equal(764, stored.CountryId);
            Assert.True(stored.ModifiedDate > new DateTime(2020, 1, 1));
            if (create) Assert.True(stored.CreatedDate > new DateTime(2020, 1, 1));
            else Assert.Equal(new DateTime(2020, 1, 1), stored.CreatedDate);
            Assert.Equal(expectedLine!.EnumerateRunes().Count(), await db.Database.SqlQuery<int>($"SELECT char_length(\"AddressLine1\") AS \"Value\" FROM \"Address\" WHERE \"ID\" = {id}").SingleAsync(token));
            Assert.Equal(256, await db.Database.SqlQueryRaw<int>("SELECT character_maximum_length::integer AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Address' AND column_name = 'AddressLine1'").SingleAsync(token));
            AssertGraph(before, await PhysicalSnapshotAsync(token), route, id);
            await AssertCacheAsync(cache, cached, create ? [1] : [1, 2], token);
            foreach (var customerId in create ? scoped ? new[] { 1 } : Array.Empty<int>() : new[] { 1, 2 })
            {
                using var profile = await client.GetAsync($"/customers/{customerId}", token);
                Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
                using var json = JsonDocument.Parse(await profile.Content.ReadAsStringAsync(token));
                var property = create && route.StartsWith("billing-", StringComparison.Ordinal) ? "BillingAddress" : "ShippingAddress";
                AssertLiteral(json.RootElement.GetProperty(property), expectedLine!, item.NullOptional);
                Assert.False(json.RootElement.TryGetProperty("InternalRemark", out _));
            }
            if (scoped)
            {
                var staleBefore = await PhysicalSnapshotAsync(token);
                var staleCache = await PrimeAsync(cache, token);
                using var staleRequest = Request(route, "Stale road", false, null, versions);
                using var stale = await client.SendAsync(staleRequest, token);
                Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
                Assert.Equal(staleBefore, await PhysicalSnapshotAsync(token));
                await AssertCacheAsync(cache, staleCache, [], token);
            }
        }
    }

    /// <summary>Verifies that invalid authority cannot write addresses through ordinary or scoped routes.</summary>
    /// <param name="authority">The authentication and permission scenario used by the test client.</param>
    /// <param name="expected">The expected HTTP status for the supplied scenario.</param>
    /// <returns>A task representing completion of the address HTTP, physical storage, graph, cache, and revision assertions.</returns>
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    public async Task InvalidAuthority_CannotWriteOrdinaryOrScopedAddress(string authority, HttpStatusCode expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        await using var host = fixture.Start();
        using var permitted = fixture.Client(host, "relation-editor");
        using var client = fixture.Client(host, authority);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var route in new[] { "create", "update", "billing-create", "billing-update", "shipping-create", "shipping-update" })
        {
            await SeedAsync(route, token);
            var scoped = route.Contains('-', StringComparison.Ordinal);
            var versions = scoped ? await VersionsAsync(permitted, route, route.EndsWith("create", StringComparison.Ordinal) ? "new" : "1", token) : default;
            var before = await PhysicalSnapshotAsync(token);
            var cached = await PrimeAsync(cache, token);
            using var request = Request(route, "  Refused road  ", false, null, versions);
            using var response = await client.SendAsync(request, token);
            Assert.Equal(expected, response.StatusCode);
            Assert.Equal(before, await PhysicalSnapshotAsync(token));
            await AssertCacheAsync(cache, cached, [], token);
        }
    }

    /// <summary>Verifies that the storage guard rejects isolated UTF-16 surrogates without replacing them.</summary>
    /// <param name="high">Whether to use an isolated high surrogate; false selects an isolated low surrogate.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StorageGuard_RejectsMalformedUtf16WithoutReplacement(bool high)
    {
        var value = new string(high ? '\ud800' : '\udc00', 1);
        Assert.False(AddressLine1LengthAttribute.IsStorable(value));
        Assert.False(new AddressLine1LengthAttribute().IsValid(value));
    }

    /// <summary>Verifies that invalid JSON roots and field shapes are refused before graph, cache, or revision changes.</summary>
    /// <param name="route">The ordinary or scoped create or update route under test.</param>
    /// <returns>A task representing completion of the address HTTP, physical storage, graph, cache, and revision assertions.</returns>
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("billing-create")]
    [InlineData("billing-update")]
    [InlineData("shipping-create")]
    [InlineData("shipping-update")]
    public async Task JsonPrimitiveInputs_RefuseBeforeGraphCacheOrVersionMutation(string route)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var scoped = route.Contains('-', StringComparison.Ordinal);
        var create = route.EndsWith("create", StringComparison.Ordinal);
        await SeedAsync(route, token);
        var before = await PhysicalSnapshotAsync(token);
        var cached = await PrimeAsync(cache, token);
        var versions = scoped ? await VersionsAsync(client, route, create ? "new" : "1", token) : default;
        var bodies = new List<string> { "null", "true", "42", "\"line\"", "[]", "" };
        foreach (var primitive in new[] { "{}", "[]" })
        {
            bodies.Add("{\"AddressLine1\":" + primitive + ",\"CountryId\":764}");
        }
        foreach (var primitive in new[] { "true", "1.5", "2147483648", "{}", "[]" })
        {
            bodies.Add("{\"AddressLine1\":\"  Literal road  \",\"CountryId\":" + primitive + "}");
        }
        foreach (var body in bodies)
        {
            using var request = Request(route, "  Literal road  ", false, null, versions);
            request.Content!.Dispose();
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(before, await PhysicalSnapshotAsync(token));
            await AssertCacheAsync(cache, cached, [], token);
            if (scoped) Assert.Equal(versions, await VersionsAsync(client, route, create ? "new" : "1", token));
        }
    }

    /// <summary>Verifies that a missing required address line rolls back storage and leaves caches and revisions unchanged.</summary>
    /// <param name="route">The ordinary or scoped create or update route under test.</param>
    /// <returns>A task representing completion of the address HTTP, physical storage, graph, cache, and revision assertions.</returns>
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("billing-create")]
    [InlineData("billing-update")]
    [InlineData("shipping-create")]
    [InlineData("shipping-update")]
    public async Task MissingLine_PreservesRequiredColumnAndRollsBackWithoutCacheOrVersionMutation(string route)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        await SeedAsync(route, token);
        var before = await PhysicalSnapshotAsync(token);
        var cached = await PrimeAsync(cache, token);
        var scoped = route.Contains('-', StringComparison.Ordinal);
        var versions = scoped ? await VersionsAsync(client, route, route.EndsWith("create", StringComparison.Ordinal) ? "new" : "1", token) : default;
        using var request = Request(route, null, false, null, versions);
        request.Content!.Dispose();
        request.Content = new StringContent("{\"CountryId\":764}", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, token);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync(token);
        Assert.DoesNotContain("23502", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", error, StringComparison.Ordinal);
        Assert.Equal(before, await PhysicalSnapshotAsync(token));
        await AssertCacheAsync(cache, cached, [], token);
        if (scoped) Assert.Equal(versions, await VersionsAsync(client, route, route.EndsWith("create", StringComparison.Ordinal) ? "new" : "1", token));
        await using var db = fixture.Context();
        Assert.Equal("NO", await db.Database.SqlQueryRaw<string>("SELECT is_nullable::text AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Address' AND column_name = 'AddressLine1'").SingleAsync(token));
    }

    /// <summary>Verifies address country admission for zero, Int32 limits, numeric strings, and default-valued input.</summary>
    /// <param name="route">The ordinary or scoped create or update route under test.</param>
    /// <returns>A task representing completion of the address HTTP, physical storage, graph, cache, and revision assertions.</returns>
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("billing-create")]
    [InlineData("billing-update")]
    [InlineData("shipping-create")]
    [InlineData("shipping-update")]
    public async Task CountryId_PreservesZeroInt32AndNumericStringAdmission(string route)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var scoped = route.Contains('-', StringComparison.Ordinal);
        var create = route.EndsWith("create", StringComparison.Ordinal);
        foreach (var item in new (string? Json, int Expected)[] { ("0", 0), ("-1", -1), ("-2147483648", int.MinValue), ("2147483647", int.MaxValue), ("\" 764 \"", 764), ("null", 0), ("\"\"", 0), (null, 0) })
        {
            await SeedAsync(route, token);
            var before = await PhysicalSnapshotAsync(token);
            var cached = await PrimeAsync(cache, token);
            var versions = scoped ? await VersionsAsync(client, route, create ? "new" : "1", token) : default;
            using var request = Request(route, "  Literal road  ", false, null, versions);
            request.Content!.Dispose();
            var body = "{\"AddressLine1\":\"  Literal road  \"" + (item.Json is null ? "}" : ",\"CountryId\":" + item.Json + "}");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, token);
            Assert.Equal(create && !scoped ? HttpStatusCode.Created : HttpStatusCode.NoContent, response.StatusCode);
            var id = scoped ? int.Parse(response.Headers.GetValues("X-Relation-Id").Single(), System.Globalization.CultureInfo.InvariantCulture) : 1;
            if (create && !scoped)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                id = json.RootElement.GetProperty("Id").GetInt32();
                Assert.Equal(item.Expected, json.RootElement.GetProperty("CountryId").GetInt32());
                Assert.Equal("  Literal road  ", json.RootElement.GetProperty("AddressLine1").GetString());
                Assert.EndsWith($"/customers/1/addresses/{id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            await using var db = fixture.Context();
            var address = await db.Addresses.AsNoTracking().SingleAsync(row => row.Id == id, token);
            Assert.Equal(item.Expected, address.CountryId);
            Assert.Equal("  Literal road  ", address.AddressLine1);
            AssertGraph(before, await PhysicalSnapshotAsync(token), route, id);
            await AssertCacheAsync(cache, cached, create ? [1] : [1, 2], token);
        }
    }

    /// <summary>Verifies that a missing ordinary address target is reported before null-line storage is attempted.</summary>
    /// <param name="create">Whether the request creates an address under a missing customer; false updates a missing address.</param>
    /// <returns>A task representing completion of the address HTTP, physical storage, graph, cache, and revision assertions.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NullLine_WithMissingOrdinaryTarget_ReturnsNotFoundBeforeStorage(bool create)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        await SeedAsync(create ? "create" : "update", token);
        var before = await PhysicalSnapshotAsync(token);
        using var request = new HttpRequestMessage(create ? HttpMethod.Post : HttpMethod.Put,
            create ? "/customers/999999/addresses/" : "/customers/addresses/999999/")
        {
            Content = new StringContent("{\"AddressLine1\":null,\"CountryId\":764}", Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request, token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await PhysicalSnapshotAsync(token));
    }

    private static string? Optional(string field, bool absent) => absent ? null : " \t" + field + "\u00a0 ";

    private static HttpRequestMessage Request(string route, string? line, bool absent, string? raw, (string? Metadata, string? Customer) versions)
    {
        var scoped = route.Contains('-', StringComparison.Ordinal);
        var create = route.EndsWith("create", StringComparison.Ordinal);
        var path = scoped ? "/customers/1/relations/" + (route.StartsWith("billing-", StringComparison.Ordinal) ? "billing" : "shipping") + "/address/" + (create ? "new" : "1") + "/versioned"
            : create ? "/customers/1/addresses/" : "/customers/addresses/1/";
        var fields = new Dictionary<string, object?>
        {
            ["Building"] = Optional("Building", absent),
            ["AddressLine2"] = Optional("AddressLine2", absent),
            ["City"] = Optional("City", absent),
            ["State"] = Optional("State", absent),
            ["PostalCode"] = Optional("PostalCode", absent),
            ["CountryId"] = 764,
        };
        var tail = JsonSerializer.Serialize(fields);
        var body = "{\"AddressLine1\":" + (raw ?? JsonSerializer.Serialize(line)) + "," + tail[1..];
        var request = new HttpRequestMessage(create && !scoped ? HttpMethod.Post : HttpMethod.Put, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (versions.Metadata is not null) request.Headers.TryAddWithoutValidation("If-Match", versions.Metadata);
        if (versions.Customer is not null) request.Headers.TryAddWithoutValidation("X-Customer-If-Match", versions.Customer);
        return request;
    }

    private static string ReadPath(string route, string binding) => "/customers/1/relations/" + (route.StartsWith("billing-", StringComparison.Ordinal) ? "billing" : "shipping") + "/address/" + binding + (binding == "new" ? "" : "/edit");

    private static async Task<(string? Metadata, string? Customer)> VersionsAsync(HttpClient client, string route, string binding, CancellationToken token)
    {
        using var read = await client.GetAsync(ReadPath(route, binding), token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        return (read.Headers.ETag!.ToString(), read.Headers.GetValues("X-Customer-ETag").Single());
    }

    private static void AssertLiteral(JsonElement address, string line, bool absent)
    {
        Assert.Equal(line, address.GetProperty("AddressLine1").GetString());
        Assert.Equal(764, address.GetProperty("CountryId").GetInt32());
        foreach (var field in new[] { "Building", "AddressLine2", "City", "State", "PostalCode" })
            if (absent) Assert.False(address.TryGetProperty(field, out _));
            else Assert.Equal(Optional(field, false), address.GetProperty(field).GetString());
        foreach (var field in new[] { "addressLine1", "InternalRemark", "PasswordHash", "SecurityStamp", "Token" }) Assert.False(address.TryGetProperty(field, out _));
    }

    private static void AssertGraph(string before, string after, string route, int addressId)
    {
        using var old = JsonDocument.Parse(before);
        using var current = JsonDocument.Parse(after);
        foreach (var table in new[] { "companies", "replay" }) Assert.Equal(old.RootElement.GetProperty(table).GetRawText(), current.RootElement.GetProperty(table).GetRawText());
        var create = route.EndsWith("create", StringComparison.Ordinal);
        var scoped = route.Contains('-', StringComparison.Ordinal);
        var addresses = current.RootElement.GetProperty("addresses").EnumerateArray().ToArray();
        Assert.Equal(create ? 3 : 2, addresses.Length);
        foreach (var row in old.RootElement.GetProperty("addresses").EnumerateArray())
            if (row.GetProperty("ID").GetInt32() != addressId)
                Assert.Equal(row.GetRawText(), addresses.Single(value => value.GetProperty("ID").GetInt32() == row.GetProperty("ID").GetInt32()).GetRawText());
        var customers = current.RootElement.GetProperty("customers").EnumerateArray().ToArray();
        Assert.Equal(3, customers.Length);
        var selected = route.StartsWith("billing-", StringComparison.Ordinal) ? "BillingAddressID" : "ShippingAddressID";
        foreach (var row in old.RootElement.GetProperty("customers").EnumerateArray())
        {
            var id = row.GetProperty("ID").GetInt32();
            var saved = customers.Single(value => value.GetProperty("ID").GetInt32() == id);
            if (!scoped || id != 1) Assert.Equal(row.GetRawText(), saved.GetRawText());
            else
            {
                foreach (var property in row.EnumerateObject())
                    if (property.Name != selected && property.Name is not ("ModifiedDate" or "xmin"))
                        Assert.Equal(property.Value.GetRawText(), saved.GetProperty(property.Name).GetRawText());
                Assert.Equal(addressId, saved.GetProperty(selected).GetInt32());
                Assert.NotEqual(row.GetProperty("xmin").GetString(), saved.GetProperty("xmin").GetString());
            }
        }
    }

    private static async Task<Dictionary<int, byte[]>> PrimeAsync(IDistributedCache cache, CancellationToken token)
    {
        var entries = new Dictionary<int, byte[]>();
        foreach (var id in new[] { 1, 2, 3 })
        {
            var bytes = Encoding.UTF8.GetBytes($"{{\"id\":{id},\"firstName\":\"Cached\",\"lastName\":\"Synthetic\",\"email\":\"cached-{id}@example.invalid\"}}");
            await cache.SetAsync($"customer:{id}", bytes, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }, token);
            entries.Add(id, bytes);
        }
        return entries;
    }

    private static async Task AssertCacheAsync(IDistributedCache cache, Dictionary<int, byte[]> before, int[] evicted, CancellationToken token)
    {
        foreach (var pair in before)
            if (evicted.Contains(pair.Key)) Assert.Null(await cache.GetAsync($"customer:{pair.Key}", token));
            else Assert.Equal(pair.Value, await cache.GetAsync($"customer:{pair.Key}", token));
    }

    private async Task SeedAsync(string route, CancellationToken token)
    {
        await fixture.ResetAsync();
        await using var db = fixture.Context();
        (await db.Addresses.SingleAsync(row => row.Id == 1, token)).CreatedDate = new DateTime(2020, 1, 1);
        db.Customers.Add(new Customer { FirstName = "Shared", LastName = "Synthetic", Email = "shared@example.invalid", ShippingAddressId = 1 });
        await db.SaveChangesAsync(token);
        db.Customers.Add(new Customer { FirstName = "Unrelated", LastName = "Synthetic", Email = "unrelated@example.invalid", ShippingAddress = new Address { AddressLine1 = "Unrelated road", CountryId = 764 } });
        var target = await db.Customers.SingleAsync(row => row.Id == 1, token);
        if (route == "billing-create") target.BillingAddressId = null;
        if (route == "shipping-create") target.ShippingAddressId = null;
        await db.SaveChangesAsync(token);
    }

    private async Task<string> PhysicalSnapshotAsync(CancellationToken token)
    {
        await using var db = fixture.Context();
        return await db.Database.SqlQueryRaw<string>("""
            SELECT jsonb_build_object(
              'customers', (SELECT jsonb_agg(to_jsonb(c) || jsonb_build_object('xmin', c.xmin::text) ORDER BY c."ID") FROM "Customer" c),
              'companies', (SELECT jsonb_agg(to_jsonb(c) || jsonb_build_object('xmin', c.xmin::text) ORDER BY c."ID") FROM "Company" c),
              'addresses', (SELECT jsonb_agg(to_jsonb(a) || jsonb_build_object('xmin', a.xmin::text) ORDER BY a."ID") FROM "Address" a),
              'replay', (SELECT jsonb_agg(to_jsonb(r) || jsonb_build_object('xmin', r.xmin::text) ORDER BY r."Key") FROM "CustomerCreateOperation" r)
            )::text AS "Value"
            """).SingleAsync(token);
    }
}
