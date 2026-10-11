using System.Net;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[Collection("Customer company lifecycle")]
public sealed class CustomerCompanyOrdinaryPrimitiveHttpTests(CustomerDetailAuthorityFixture fixture, ITestOutputHelper output)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Fact]
    public Task OrdinaryPrimitiveNames_PersistExactLexemesAndOnlyIntendedGraphAndCacheEffects() =>
        WriteMatrixAsync([("true", "true", true), ("false", "false", true), ("42", "42", true), ("1.5", "1.5", true), ("1e3", "1e3", true)]);

    [Fact]
    public Task OrdinaryNumericNames_PreserveOverflowAndSpellingOrRefuseOver256Units() =>
        WriteMatrixAsync([
            ("1e309", "1e309", true), ("-1e309", "-1e309", true), ("1e400", "1e400", true),
            ("-0", "-0", true), ("1E+03", "1E+03", true), ("1.500", "1.500", true),
            ("9223372036854775808123456789", "9223372036854775808123456789", true),
            (new string('1', 256), new string('1', 256), true),
            (new string('1', 257), new string('1', 257), false),
        ]);

    [Fact]
    public async Task OrdinaryPrimitiveNames_InvalidAuthorityCannotChangeGraphOrCache()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        await using var host = fixture.Start();
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var authority in new[] { "anonymous", "denied" })
            foreach (var route in new[] { "create", "update" })
            {
                await SeedAsync(route);
                var before = await PhysicalSnapshotAsync(token);
                var cached = await PrimeAsync(cache, token);
                using var client = fixture.Client(host, authority);
                client.Timeout = TimeSpan.FromSeconds(15);
                foreach (var raw in new[] { "true", "false", "42", "1.5", "1e3" })
                {
                    using var request = Request(route, raw);
                    using var response = await client.SendAsync(request, token);
                    Assert.Equal(authority == "anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
                    Assert.Equal(before, await PhysicalSnapshotAsync(token));
                    await AssertCacheAsync(cache, cached, [], token);
                }
            }
    }

    private async Task WriteMatrixAsync((string Raw, string Name, bool Fits)[] cases)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var route in new[] { "create", "update" })
            foreach (var item in cases)
            {
                await SeedAsync(route);
                var create = route == "create";
                var before = await PhysicalSnapshotAsync(token);
                var cached = await PrimeAsync(cache, token);
                using var request = Request(route, item.Raw);
                var started = DateTime.UtcNow;
                using var response = await client.SendAsync(request, token);
                var ended = DateTime.UtcNow;
                Assert.Equal(item.Fits ? create ? HttpStatusCode.Created : HttpStatusCode.NoContent : HttpStatusCode.BadRequest, response.StatusCode);
                output.WriteLine(JsonSerializer.Serialize(new { Route = route, Case = item.Raw, Status = (int)response.StatusCode }));
                if (!item.Fits)
                {
                    var error = await response.Content.ReadAsStringAsync(token);
                    foreach (var privateValue in new[] { "staff-only", "PasswordHash", "SecurityStamp", "ConnectionStrings", "Npgsql", "SELECT " })
                        Assert.DoesNotContain(privateValue, error, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(before, await PhysicalSnapshotAsync(token));
                    await AssertCacheAsync(cache, cached, [], token);
                    continue;
                }

                var id = 1;
                if (create)
                {
                    using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    id = created.RootElement.GetProperty("Id").GetInt32();
                    AssertLiteral(created.RootElement, item.Name);
                    Assert.True(id > 2);
                    Assert.EndsWith($"/customers/Companies/{id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
                }
                else Assert.Equal("", await response.Content.ReadAsStringAsync(token));

                var after = await PhysicalSnapshotAsync(token);
                AssertPreservedGraph(before, after, id, false, create);
                using var postimage = JsonDocument.Parse(after);
                var changed = postimage.RootElement.GetProperty("companies").EnumerateArray().Single(row => row.GetProperty("ID").GetInt32() == id);
                Assert.Equal(item.Name, changed.GetProperty("Name").GetString());
                Assert.Equal(" 0100000000000 ", changed.GetProperty("TaxNumber").GetString());
                Assert.Equal("\tSynthetic registrar\t", changed.GetProperty("Registrar").GetString());
                Assert.InRange(changed.GetProperty("ModifiedDate").GetDateTime(), started.AddSeconds(-1), ended.AddSeconds(1));
                if (create)
                {
                    Assert.Equal(changed.GetProperty("CreatedDate").GetRawText(), changed.GetProperty("ModifiedDate").GetRawText());
                    Assert.False(string.IsNullOrWhiteSpace(changed.GetProperty("xmin").GetString()));
                }
                else
                {
                    using var original = JsonDocument.Parse(before);
                    var previous = original.RootElement.GetProperty("companies").EnumerateArray().Single(row => row.GetProperty("ID").GetInt32() == id);
                    foreach (var field in previous.EnumerateObject())
                        if (field.Name is not ("Name" or "TaxNumber" or "Registrar" or "ModifiedDate" or "xmin"))
                            Assert.Equal(field.Value.GetRawText(), changed.GetProperty(field.Name).GetRawText());
                    Assert.NotEqual(previous.GetProperty("xmin").GetString(), changed.GetProperty("xmin").GetString());
                }
                await AssertCacheAsync(cache, cached, create ? [] : [1, 2], token);
                using var read = await client.GetAsync($"/customers/companies/{id}", token);
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
                AssertLiteral(body.RootElement, item.Name);
                if (!create)
                {
                    foreach (var customerId in new[] { 1, 2 })
                    {
                        using var customer = await client.GetAsync($"/customers/{customerId}", token);
                        Assert.Equal(HttpStatusCode.OK, customer.StatusCode);
                        using var customerBody = JsonDocument.Parse(await customer.Content.ReadAsStringAsync(token));
                        AssertLiteral(customerBody.RootElement.GetProperty("Company"), item.Name);
                        Assert.False(customerBody.RootElement.TryGetProperty("InternalRemark", out _));
                    }
                }
            }
    }

    private static HttpRequestMessage Request(string route, string raw) =>
        new(route == "create" ? HttpMethod.Post : HttpMethod.Put, route == "create" ? "/customers/companies/" : "/customers/companies/1/")
        {
            Content = new StringContent("{\"Name\":" + raw + ",\"TaxNumber\":\" 0100000000000 \",\"Registrar\":\"\\tSynthetic registrar\\t\"}", Encoding.UTF8, "application/json"),
        };

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
