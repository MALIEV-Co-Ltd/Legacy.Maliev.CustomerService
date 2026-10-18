using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Domain;
using Legacy.Maliev.CustomerService.Tests.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.OriginalInput;

[Collection("Customer original body")]
public sealed class CompanyOriginalEnvelopeHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyIgnoredTypedEnvelope_RejectsBeforeAnyPhysicalGraphOrCacheWrite(bool update)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = lifetime.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "company-lifecycle");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var fields in new[]
        {
            "\"Id\":{\"private-company-input-marker\":1}",
            "\"CreatedDate\":\"private-company-input-marker\"",
            "\"ModifiedDate\":[]",
            "\"Customer\":{}",
            "\"Customer\":[{\"Id\":{}}]",
            "\"Customer\":[{\"FullName\":[]}]",
            "\"Customer\":[{\"DateOfBirth\":\"private-company-input-marker\"}]",
            "\"Customer\":[{\"CompanyId\":{}}]",
            "\"Customer\":[{\"Company\":{\"Registrar\":[]}}]",
            "\"Customer\":[{\"BillingAddress\":{\"CountryId\":\"private-company-input-marker\"}}]",
            "\"Customer\":[{\"ShippingAddress\":{\"CustomerShippingAddress\":[{\"ModifiedDate\":{}}]}}]",
            "\"Customer\":[{\"Company\":{\"CreatedDate\":{}}}],\"Customer\":[]"
        })
        {
            await SeedAsync(token);
            var before = await SnapshotAsync(null, token);
            await PrimeAsync(cache, token);
            using var request = Request(update, CompanyOriginalBodyHarness.Standard[..^1] + "," + fields + "}");
            using var response = await client.SendAsync(request, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(token);
            Assert.DoesNotContain("private-company-input-marker", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Newtonsoft", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Npgsql", body, StringComparison.Ordinal);
            Assert.Equal(before, await SnapshotAsync(null, token));
            await AssertCachesAsync(cache, true, token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyValidRecursiveEnvelope_ProjectsOnlyThreeFieldsAndPreservesAllOtherPhysicalRows(bool update)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = lifetime.Token;
        await SeedAsync(token);
        var before = await SnapshotAsync(update ? 1 : null, token);
        DateTime? createdBefore;
        await using (var original = fixture.Context())
            createdBefore = (await original.Companies.AsNoTracking().SingleAsync(row => row.Id == 1, token)).CreatedDate;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "company-lifecycle");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        await PrimeAsync(cache, token);
        var started = DateTime.UtcNow.AddSeconds(-5);
        using var request = Request(update, CompanyOriginalBodyHarness.Graph);
        using var response = await client.SendAsync(request, token);
        Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        var id = 1;
        if (update)
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(token));
        else
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            id = json.RootElement.GetProperty("Id").GetInt32();
            Assert.True(id > 2);
            Assert.NotEqual(999, id);
            Assert.NotNull(response.Headers.Location);
            Assert.EndsWith($"/customers/companies/{id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
            AssertWire(json.RootElement, id);
        }
        await using (var db = fixture.Context())
        {
            var stored = await db.Companies.AsNoTracking().SingleAsync(row => row.Id == id, token);
            Assert.Equal("  Company envelope  ", stored.Name);
            Assert.Equal("123000", stored.TaxNumber);
            Assert.Equal("Office", stored.Registrar);
            Assert.NotNull(stored.ModifiedDate);
            Assert.InRange(stored.ModifiedDate.GetValueOrDefault(), started, DateTime.UtcNow.AddSeconds(5));
            Assert.NotNull(stored.CreatedDate);
            if (update) Assert.Equal(createdBefore, stored.CreatedDate);
            else Assert.InRange(stored.CreatedDate.GetValueOrDefault(), started, DateTime.UtcNow.AddSeconds(5));
            Assert.Equal(update ? 2 : 3, await db.Companies.CountAsync(token));
            Assert.Equal(2, await db.Customers.CountAsync(token));
            Assert.Equal(1, await db.Addresses.CountAsync(token));
            Assert.False(await db.Companies.AnyAsync(row => row.Id == 999, token));
            Assert.False(await db.Customers.AnyAsync(row => row.Id == 998, token));
            Assert.False(await db.Addresses.AnyAsync(row => row.Id == 997, token));
        }
        Assert.Equal(before, await SnapshotAsync(id, token));
        await AssertCachesAsync(cache, !update, token);
        using var read = await client.GetAsync($"/customers/companies/{id}", token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var wire = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
        AssertWire(wire.RootElement, id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyOriginalGrammar_ReachesNormalRegisteredHttpAndStorage(bool update)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = lifetime.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "company-lifecycle");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var json in new[]
        {
            "{'Name':'  Company envelope  ','TaxNumber':'123000','Registrar':'Office'}",
            "{Name:'  Company envelope  ',TaxNumber:'123000',Registrar:'Office'}",
            "/* company */" + CompanyOriginalBodyHarness.Standard,
            CompanyOriginalBodyHarness.Standard[..^1] + ",}"
        })
        {
            await SeedAsync(token);
            var before = await SnapshotAsync(update ? 1 : null, token);
            await PrimeAsync(cache, token);
            using var request = Request(update, json);
            using var response = await client.SendAsync(request, token);
            Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
            var id = 1;
            if (!update)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                id = body.RootElement.GetProperty("Id").GetInt32();
                Assert.True(id > 2);
            }
            Assert.Equal(before, await SnapshotAsync(id, token));
            await AssertCachesAsync(cache, !update, token);
            await using var db = fixture.Context();
            var stored = await db.Companies.AsNoTracking().SingleAsync(row => row.Id == id, token);
            Assert.Equal("  Company envelope  ", stored.Name);
            Assert.Equal("123000", stored.TaxNumber);
            Assert.Equal("Office", stored.Registrar);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task CompanyMalformedIgnoredField_PrecedesMissingRouteLookup(int id)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = lifetime.Token;
        await SeedAsync(token);
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "company-lifecycle");
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        await PrimeAsync(cache, token);
        var before = await SnapshotAsync(null, token);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/customers/companies/{id}")
        {
            Content = new StringContent("{\"Name\":\"Valid\",\"Id\":{}}", Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(null, token));
        await AssertCachesAsync(cache, true, token);
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    public async Task CompanyOriginalBody_DoesNotBypassNormalAuthenticationOrPermission(
        string authority, HttpStatusCode expected)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = lifetime.Token;
        await SeedAsync(token);
        await using var host = fixture.Start();
        using var client = fixture.Client(host, authority);
        var before = await SnapshotAsync(null, token);
        using var request = Request(false, "{\"Name\":\"Valid\",\"Customer\":[{\"Id\":{}}]}");
        using var response = await client.SendAsync(request, token);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(null, token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyOriginalBody_PreservesCurrentControllerAndStorageRefusals(bool update)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = lifetime.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "company-lifecycle");
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var body in new[]
        {
            "{}",
            "{\"Name\":\" \",\"TaxNumber\":null}",
            "{\"Name\":\"\\u0000\",\"TaxNumber\":\"T\"}",
            "{\"Name\":\"" + new string('ก', 257) + "\"}"
        })
        {
            await SeedAsync(token);
            var before = await SnapshotAsync(null, token);
            await PrimeAsync(cache, token);
            using var request = Request(update, body);
            using var response = await client.SendAsync(request, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(before, await SnapshotAsync(null, token));
            await AssertCachesAsync(cache, true, token);
        }
    }

    private async Task SeedAsync(CancellationToken token)
    {
        await fixture.ResetAsync();
        await using var db = fixture.Context();
        db.Customers.Add(new Customer
        {
            FirstName = "Unrelated", LastName = "Survivor", Email = "unrelated@example.test",
            Company = new Company { Name = "Survivor company", TaxNumber = "Other tax", Registrar = "Other registrar" },
            BillingAddressId = 1, ShippingAddressId = 1
        });
        await db.SaveChangesAsync(token);
    }

    private async Task<string> SnapshotAsync(int? excludeCompany, CancellationToken token)
    {
        await using var db = fixture.Context();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(token);
        var rows = new List<string>();
        foreach (var table in new[] { "Customer", "Company", "Address", "CustomerCreateOperation", "QuotationProfileCompletionOperation" })
        {
            using var command = connection.CreateCommand();
            var where = table == "Company" && excludeCompany.HasValue ? " WHERE r.\"ID\" <> @excluded" : "";
            command.CommandText = "SELECT COALESCE(jsonb_agg(to_jsonb(r) || jsonb_build_object('PhysicalXmin',r.xmin::text) " +
                "ORDER BY to_jsonb(r)::text)::text,'[]') AS \"Value\" FROM \"" + table + "\" AS r" + where;
            if (where.Length != 0)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = "excluded";
                parameter.DbType = DbType.Int32;
                parameter.Value = excludeCompany.GetValueOrDefault();
                command.Parameters.Add(parameter);
            }
            rows.Add(Assert.IsType<string>(await command.ExecuteScalarAsync(token)));
        }
        return JsonSerializer.Serialize(rows);
    }

    private static HttpRequestMessage Request(bool update, string body) =>
        new(update ? HttpMethod.Put : HttpMethod.Post, update ? "/customers/companies/1" : "/customers/companies")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static void AssertWire(JsonElement item, int id)
    {
        Assert.Equal(id, item.GetProperty("Id").GetInt32());
        Assert.Equal("  Company envelope  ", item.GetProperty("Name").GetString());
        Assert.Equal("123000", item.GetProperty("TaxNumber").GetString());
        Assert.Equal("Office", item.GetProperty("Registrar").GetString());
        Assert.True(item.TryGetProperty("CreatedDate", out _));
        Assert.True(item.TryGetProperty("ModifiedDate", out _));
        foreach (var name in new[] { "Customer", "name", "PasswordHash", "SecurityStamp", "Revision", "PhysicalXmin" })
            Assert.False(item.TryGetProperty(name, out _));
        Assert.DoesNotContain("forged", item.GetRawText(), StringComparison.Ordinal);
    }

    private static Task PrimeAsync(IDistributedCache cache, CancellationToken token) => Task.WhenAll(
        cache.SetAsync("customer:1", Encoding.UTF8.GetBytes("company-cache-one"), token),
        cache.SetAsync("customer:2", Encoding.UTF8.GetBytes("company-cache-two"), token));

    private static async Task AssertCachesAsync(IDistributedCache cache, bool firstRetained, CancellationToken token)
    {
        var first = await cache.GetAsync("customer:1", token);
        if (firstRetained) Assert.Equal(Encoding.UTF8.GetBytes("company-cache-one"), first);
        else Assert.Null(first);
        Assert.Equal(Encoding.UTF8.GetBytes("company-cache-two"), await cache.GetAsync("customer:2", token));
    }
}
