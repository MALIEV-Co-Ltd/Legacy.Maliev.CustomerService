using System.Net;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[Collection("Customer company lifecycle")]
public sealed class CustomerCompanyLiteralNameHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("scoped-create")]
    [InlineData("scoped-update")]
    public async Task CompanyLiteralName_PreservesWireStorageSharedGraphAndCacheOrRefusesBeforeWrite(string route)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var scoped = route.StartsWith("scoped-", StringComparison.Ordinal);
        var create = route.EndsWith("create", StringComparison.Ordinal);
        var matrix = new (string Label, string? Name, bool Fits, bool Blank, string? Raw)[]
        {
            ("EnglishLiteral", "  Fixture company  ", true, false, null),
            ("ThaiLiteral", "  บริษัท ทดสอบ  ", true, false, null),
            ("MixedPadding", " \t\u00a0บริษัท\u00a0\t ", true, false, null),
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
            ("NulNotStorable", "company\0name", false, false, null),
            ("LoneHighSurrogateJson", null, false, false, "\"\\ud800\""),
            ("LoneLowSurrogateJson", null, false, false, "\"\\udc00\""),
            ("EmptyTaxOnly", "", true, true, null),
            ("WhitespaceTaxOnly", " \t\u00a0 ", true, true, null),
            ("NullName", null, false, true, null),
        };
        foreach (var item in matrix)
        {
            await SeedAsync(route);
            var before = await PhysicalSnapshotAsync(token);
            var cacheBefore = await PrimeAsync(cache, token);
            var versions = scoped ? await VersionsAsync(client, create ? "new" : "1", token) : default;
            using var request = Request(route, item.Name, item.Raw, versions);
            using var result = await client.SendAsync(request, token);
            var accepted = item.Fits && (!scoped || !item.Blank);
            var expected = accepted ? create && !scoped ? HttpStatusCode.Created : HttpStatusCode.NoContent : HttpStatusCode.BadRequest;
            Assert.True(expected == result.StatusCode, $"{route}/{item.Label}: expected {expected}, actual {result.StatusCode}");
            if (!accepted)
            {
                Assert.Equal(before, await PhysicalSnapshotAsync(token));
                await AssertCacheAsync(cache, cacheBefore, [], token);
                continue;
            }

            Assert.True(item.Name!.Length <= 256);
            var id = 1;
            if (scoped)
            {
                id = int.Parse(result.Headers.GetValues("X-Relation-Id").Single(), System.Globalization.CultureInfo.InvariantCulture);
                Assert.NotNull(result.Headers.ETag);
                using var fresh = await client.GetAsync($"/customers/1/relations/company/{id}/edit", token);
                Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
                Assert.Equal(result.Headers.ETag, fresh.Headers.ETag);
                Assert.Equal(result.Headers.GetValues("X-Customer-ETag").Single(), fresh.Headers.GetValues("X-Customer-ETag").Single());
                using var projection = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync(token));
                AssertLiteral(projection.RootElement.GetProperty("Company"), item.Name!);
            }
            else if (create)
            {
                using var created = JsonDocument.Parse(await result.Content.ReadAsStringAsync(token));
                id = created.RootElement.GetProperty("Id").GetInt32();
                AssertLiteral(created.RootElement, item.Name!);
                Assert.EndsWith($"/customers/Companies/{id}", result.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
            }

            await using var db = fixture.Context();
            var saved = await db.Companies.AsNoTracking().SingleAsync(row => row.Id == id, token);
            Assert.Equal(item.Name, saved.Name);
            Assert.Equal(" 0100000000000 ", saved.TaxNumber);
            Assert.Equal("\tSynthetic registrar\t", saved.Registrar);
            Assert.True(saved.ModifiedDate > new DateTime(2020, 1, 1));
            if (!create) Assert.Equal(new DateTime(2020, 1, 1), saved.CreatedDate);
            else
            {
                Assert.True(saved.CreatedDate > new DateTime(2020, 1, 1));
                Assert.True(id > 2);
            }
            Assert.Equal(item.Name!.EnumerateRunes().Count(), await db.Database.SqlQuery<int>($"SELECT char_length(\"Name\") AS \"Value\" FROM \"Company\" WHERE \"ID\" = {id}").SingleAsync(token));
            Assert.Equal(256, await db.Database.SqlQueryRaw<int>("SELECT character_maximum_length::integer AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Company' AND column_name = 'Name'").SingleAsync(token));
            var after = await PhysicalSnapshotAsync(token);
            AssertPreservedGraph(before, after, id, scoped, create);
            await AssertCacheAsync(cache, cacheBefore, create ? scoped ? [1] : [] : [1, 2], token);
            using var read = await client.GetAsync($"/customers/companies/{id}", token);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
            AssertLiteral(body.RootElement, item.Name!);
            foreach (var customerId in scoped && create ? new[] { 1 } : !create ? new[] { 1, 2 } : Array.Empty<int>())
            {
                using var customer = await client.GetAsync($"/customers/{customerId}", token);
                Assert.Equal(HttpStatusCode.OK, customer.StatusCode);
                using var customerBody = JsonDocument.Parse(await customer.Content.ReadAsStringAsync(token));
                AssertLiteral(customerBody.RootElement.GetProperty("Company"), item.Name!);
                Assert.False(customerBody.RootElement.TryGetProperty("InternalRemark", out _));
            }

            if (scoped)
            {
                var staleBefore = await PhysicalSnapshotAsync(token);
                var staleCache = await PrimeAsync(cache, token);
                using var staleRequest = Request(route, "Stale replacement", null, versions);
                using var stale = await client.SendAsync(staleRequest, token);
                Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
                Assert.Equal(staleBefore, await PhysicalSnapshotAsync(token));
                await AssertCacheAsync(cache, staleCache, [], token);
            }
        }
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    public async Task CompanyLiteralName_InvalidAuthorityCannotWriteAnyRoute(string authority, HttpStatusCode expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        await using var host = fixture.Start();
        using var authorized = fixture.Client(host, "relation-editor");
        using var client = fixture.Client(host, authority);
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var route in new[] { "create", "update", "scoped-create", "scoped-update" })
        {
            await SeedAsync(route);
            var versions = route.StartsWith("scoped-", StringComparison.Ordinal)
                ? await VersionsAsync(authorized, route.EndsWith("create", StringComparison.Ordinal) ? "new" : "1", token) : default;
            var before = await PhysicalSnapshotAsync(token);
            var cached = await PrimeAsync(cache, token);
            using var request = Request(route, "  Refused literal  ", null, versions);
            using var response = await client.SendAsync(request, token);
            Assert.Equal(expected, response.StatusCode);
            Assert.Equal(before, await PhysicalSnapshotAsync(token));
            await AssertCacheAsync(cache, cached, [], token);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompanyNameGuard_RejectsMalformedUtf16WithoutNormalizing(bool high)
    {
        var value = new string(high ? '\ud800' : '\udc00', 1);
        Assert.False(CompanyNameLengthAttribute.IsStorable(value));
        Assert.False(new CompanyNameLengthAttribute().IsValid(value));
    }

    private static HttpRequestMessage Request(string route, string? name, string? raw, (string? Metadata, string? Customer) versions)
    {
        var path = route switch
        {
            "create" => "/customers/companies/",
            "update" => "/customers/companies/1/",
            "scoped-create" => "/customers/1/relations/company/new/versioned",
            "scoped-update" => "/customers/1/relations/company/1/versioned",
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };
        var body = "{\"Name\":" + (raw ?? JsonSerializer.Serialize(name)) + ",\"TaxNumber\":\" 0100000000000 \",\"Registrar\":\"\\tSynthetic registrar\\t\"}";
        var request = new HttpRequestMessage(route == "create" ? HttpMethod.Post : HttpMethod.Put, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (versions.Metadata is not null) request.Headers.TryAddWithoutValidation("If-Match", versions.Metadata);
        if (versions.Customer is not null) request.Headers.TryAddWithoutValidation("X-Customer-If-Match", versions.Customer);
        return request;
    }

    private static async Task<(string? Metadata, string? Customer)> VersionsAsync(HttpClient client, string binding, CancellationToken token)
    {
        using var read = await client.GetAsync($"/customers/1/relations/company/{binding}" + (binding == "new" ? "" : "/edit"), token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        return (read.Headers.ETag!.ToString(), read.Headers.GetValues("X-Customer-ETag").Single());
    }

    private static void AssertLiteral(JsonElement item, string name)
    {
        Assert.Equal(name, item.GetProperty("Name").GetString());
        Assert.Equal(" 0100000000000 ", item.GetProperty("TaxNumber").GetString());
        Assert.Equal("\tSynthetic registrar\t", item.GetProperty("Registrar").GetString());
        Assert.False(item.TryGetProperty("name", out _));
        foreach (var key in new[] { "InternalRemark", "PasswordHash", "SecurityStamp", "Token" }) Assert.False(item.TryGetProperty(key, out _));
    }

    private static void AssertPreservedGraph(string before, string after, int changedCompany, bool scoped, bool create)
    {
        using var old = JsonDocument.Parse(before);
        using var current = JsonDocument.Parse(after);
        Assert.Equal(old.RootElement.GetProperty("addresses").GetRawText(), current.RootElement.GetProperty("addresses").GetRawText());
        Assert.Equal(old.RootElement.GetProperty("replay").GetRawText(), current.RootElement.GetProperty("replay").GetRawText());
        var companies = current.RootElement.GetProperty("companies").EnumerateArray().ToArray();
        Assert.Equal(create ? 3 : 2, companies.Length);
        foreach (var row in old.RootElement.GetProperty("companies").EnumerateArray())
            if (row.GetProperty("ID").GetInt32() != changedCompany)
                Assert.Equal(row.GetRawText(), companies.Single(value => value.GetProperty("ID").GetInt32() == row.GetProperty("ID").GetInt32()).GetRawText());
        var customers = current.RootElement.GetProperty("customers").EnumerateArray().ToArray();
        Assert.Equal(3, customers.Length);
        foreach (var row in old.RootElement.GetProperty("customers").EnumerateArray())
        {
            var id = row.GetProperty("ID").GetInt32();
            var saved = customers.Single(value => value.GetProperty("ID").GetInt32() == id);
            if (!scoped || id != 1) Assert.Equal(row.GetRawText(), saved.GetRawText());
            else
            {
                foreach (var property in row.EnumerateObject())
                    if (property.Name is not ("CompanyID" or "ModifiedDate" or "xmin"))
                        Assert.Equal(property.Value.GetRawText(), saved.GetProperty(property.Name).GetRawText());
                Assert.Equal(changedCompany, saved.GetProperty("CompanyID").GetInt32());
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

    private async Task SeedAsync(string route)
    {
        await fixture.ResetAsync();
        await using var db = fixture.Context();
        var original = await db.Companies.SingleAsync(row => row.Id == 1);
        original.CreatedDate = new DateTime(2020, 1, 1);
        db.Customers.Add(new Customer { FirstName = "Shared", LastName = "Synthetic", Email = "shared@example.invalid", CompanyId = 1, ShippingAddressId = 1 });
        await db.SaveChangesAsync();
        db.Customers.Add(new Customer { FirstName = "Unrelated", LastName = "Synthetic", Email = "unrelated@example.invalid", Company = new Company { Name = "Unrelated company" } });
        if (route == "scoped-create") (await db.Customers.SingleAsync(row => row.Id == 1)).CompanyId = null;
        await db.SaveChangesAsync();
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
