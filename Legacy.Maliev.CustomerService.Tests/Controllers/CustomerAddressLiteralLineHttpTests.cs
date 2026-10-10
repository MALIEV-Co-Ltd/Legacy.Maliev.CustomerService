using System.Net;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[Collection("Customer address persistence")]
public sealed class CustomerAddressLiteralLineHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
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
            ("LoneHighSurrogateJson", null, false, false, "\"\\ud800\""),
            ("LoneLowSurrogateJson", null, false, false, "\"\\udc00\""),
            ("EmptyLine", "", false, false, null),
            ("WhitespaceLine", " \t\u00a0 ", false, false, null),
            ("NullLine", null, false, false, null),
            ("NullOptionalFields", "  ถนนไม่มีรายละเอียด  ", true, true, null),
        };
        foreach (var item in cases)
        {
            await SeedAsync(route, token);
            var before = await PhysicalSnapshotAsync(token);
            var cached = await PrimeAsync(cache, token);
            var versions = scoped ? await VersionsAsync(client, route, create ? "new" : "1", token) : default;
            using var request = Request(route, item.Line, item.NullOptional, item.Raw, versions);
            using var result = await client.SendAsync(request, token);
            var expected = item.Accepted ? create && !scoped ? HttpStatusCode.Created : HttpStatusCode.NoContent : HttpStatusCode.BadRequest;
            Assert.True(expected == result.StatusCode, $"{route}/{item.Label}: expected {expected}, actual {result.StatusCode}");
            if (!item.Accepted)
            {
                Assert.Equal(before, await PhysicalSnapshotAsync(token));
                await AssertCacheAsync(cache, cached, [], token);
                continue;
            }

            Assert.True(item.Line!.Length <= 256);
            var id = 1;
            if (scoped)
            {
                id = int.Parse(result.Headers.GetValues("X-Relation-Id").Single(), System.Globalization.CultureInfo.InvariantCulture);
                using var read = await client.GetAsync(ReadPath(route, id.ToString(System.Globalization.CultureInfo.InvariantCulture)), token);
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                Assert.Equal(result.Headers.ETag, read.Headers.ETag);
                Assert.Equal(result.Headers.GetValues("X-Customer-ETag").Single(), read.Headers.GetValues("X-Customer-ETag").Single());
                using var readBody = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
                AssertLiteral(readBody.RootElement.GetProperty("Address"), item.Line!, item.NullOptional);
            }
            else if (create)
            {
                using var created = JsonDocument.Parse(await result.Content.ReadAsStringAsync(token));
                id = created.RootElement.GetProperty("Id").GetInt32();
                AssertLiteral(created.RootElement, item.Line!, item.NullOptional);
                Assert.EndsWith($"/customers/1/addresses/{id}", result.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
                using var unboundRead = await client.GetAsync(result.Headers.Location, token);
                Assert.Equal(HttpStatusCode.NotFound, unboundRead.StatusCode);
            }

            await using var db = fixture.Context();
            var stored = await db.Addresses.AsNoTracking().SingleAsync(row => row.Id == id, token);
            Assert.Equal(item.Line, stored.AddressLine1);
            Assert.Equal(Optional("Building", item.NullOptional), stored.Building);
            Assert.Equal(Optional("AddressLine2", item.NullOptional), stored.AddressLine2);
            Assert.Equal(Optional("City", item.NullOptional), stored.City);
            Assert.Equal(Optional("State", item.NullOptional), stored.State);
            Assert.Equal(Optional("PostalCode", item.NullOptional), stored.PostalCode);
            Assert.Equal(764, stored.CountryId);
            Assert.True(stored.ModifiedDate > new DateTime(2020, 1, 1));
            if (create) Assert.True(stored.CreatedDate > new DateTime(2020, 1, 1));
            else Assert.Equal(new DateTime(2020, 1, 1), stored.CreatedDate);
            Assert.Equal(item.Line!.EnumerateRunes().Count(), await db.Database.SqlQuery<int>($"SELECT char_length(\"AddressLine1\") AS \"Value\" FROM \"Address\" WHERE \"ID\" = {id}").SingleAsync(token));
            Assert.Equal(256, await db.Database.SqlQueryRaw<int>("SELECT character_maximum_length::integer AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Address' AND column_name = 'AddressLine1'").SingleAsync(token));
            AssertGraph(before, await PhysicalSnapshotAsync(token), route, id);
            await AssertCacheAsync(cache, cached, create ? [1] : [1, 2], token);
            foreach (var customerId in create ? scoped ? new[] { 1 } : Array.Empty<int>() : new[] { 1, 2 })
            {
                using var profile = await client.GetAsync($"/customers/{customerId}", token);
                Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
                using var json = JsonDocument.Parse(await profile.Content.ReadAsStringAsync(token));
                var property = create && route.StartsWith("billing-", StringComparison.Ordinal) ? "BillingAddress" : "ShippingAddress";
                AssertLiteral(json.RootElement.GetProperty(property), item.Line!, item.NullOptional);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StorageGuard_RejectsMalformedUtf16WithoutReplacement(bool high)
    {
        var value = new string(high ? '\ud800' : '\udc00', 1);
        Assert.False(AddressLine1LengthAttribute.IsStorable(value));
        Assert.False(new AddressLine1LengthAttribute().IsValid(value));
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
            ["Building"] = Optional("Building", absent), ["AddressLine2"] = Optional("AddressLine2", absent),
            ["City"] = Optional("City", absent), ["State"] = Optional("State", absent), ["PostalCode"] = Optional("PostalCode", absent), ["CountryId"] = 764,
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
