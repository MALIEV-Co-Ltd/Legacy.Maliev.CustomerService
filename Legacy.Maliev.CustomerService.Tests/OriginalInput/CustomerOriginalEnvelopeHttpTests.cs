using System.Net;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Domain;
using Legacy.Maliev.CustomerService.Tests.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.OriginalInput;

[CollectionDefinition("Customer original body", DisableParallelization = true)]
public sealed class CustomerOriginalBodyCollection;

[Collection("Customer original body")]
public sealed class CustomerOriginalEnvelopeHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryIgnoredTypedEnvelope_RejectsBeforeAnyGraphOrCacheWrite(bool update)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = lifetime.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var fragments = new[]
        {
            "\"Id\":{}",
            "\"FullName\":[]",
            "\"CreatedDate\":\"not-a-date\"",
            "\"ModifiedDate\":{}",
            "\"Company\":[]",
            "\"Company\":{\"Id\":{}}",
            "\"Company\":{\"Customer\":[{\"CompanyId\":{}}]}",
            "\"BillingAddress\":{\"CountryId\":\"not-an-integer\"}",
            "\"ShippingAddress\":{\"CustomerShippingAddress\":[{\"DateOfBirth\":{}}]}",
            "\"BillingAddress\":{\"CustomerBillingAddress\":[{\"Company\":{\"CreatedDate\":[]}}]}",
        };
        foreach (var fragment in fragments)
        {
            await SeedAsync(token);
            var before = await SnapshotAsync(null, token);
            await PrimeAsync(cache, token);
            using var request = Request(update, Body(fragment));
            using var response = await client.SendAsync(request, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(before, await SnapshotAsync(null, token));
            await AssertCachesAsync(cache, true, token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryValidEnvelope_ProjectsOnlyScalarsAndNeverWritesSubmittedNavigationGraph(bool update)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = lifetime.Token;
        await SeedAsync(token);
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        await PrimeAsync(cache, token);
        var untouched = await SnapshotAsync(update ? 1 : null, token);
        await using var beforeDb = fixture.Context();
        var createdBefore = (await beforeDb.Customers.AsNoTracking().SingleAsync(row => row.Id == 1, token)).CreatedDate;
        const string graph = "\"Id\":999,\"FullName\":\"forged display\",\"CreatedDate\":\"1900-01-01\",\"ModifiedDate\":\"1900-01-01\"," +
            "\"InternalRemark\":\"forged private remark\",\"Company\":{\"Id\":999,\"Name\":\"forged company\",\"Customer\":[{\"Id\":998,\"FirstName\":\"nested\",\"Email\":\"nested@example.test\"}]}," +
            "\"BillingAddress\":{\"Id\":997,\"AddressLine1\":\"forged road\",\"CountryId\":764,\"CustomerBillingAddress\":[{\"Id\":996}]}," +
            "\"ShippingAddress\":{\"Id\":995,\"AddressLine1\":\"forged shipping\",\"CountryId\":764,\"CustomerShippingAddress\":[{\"Id\":994}]}";
        var started = DateTime.UtcNow.AddSeconds(-5);
        using var request = Request(update, Body(graph));
        using var response = await client.SendAsync(request, token);
        Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        var id = 1;
        if (update)
        {
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(token));
        }
        else
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            id = json.RootElement.GetProperty("Id").GetInt32();
            Assert.True(id > 2);
            Assert.NotEqual(999, id);
            Assert.NotNull(response.Headers.Location);
            Assert.EndsWith($"/customers/{id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        await using var db = fixture.Context();
        var stored = await db.Customers.AsNoTracking().SingleAsync(row => row.Id == id, token);
        Assert.Equal("Envelope", stored.FirstName);
        Assert.Equal("Fixture", stored.LastName);
        Assert.Equal("Envelope Fixture", stored.FullName);
        Assert.Equal("envelope@example.test", stored.Email);
        Assert.Equal("020001111", stored.Telephone);
        Assert.Null(stored.Mobile);
        Assert.Equal("020002222", stored.Fax);
        Assert.Equal(new DateTime(1990, 2, 3), stored.DateOfBirth);
        Assert.Equal(1, stored.CompanyId);
        Assert.Equal(1, stored.BillingAddressId);
        Assert.Equal(1, stored.ShippingAddressId);
        Assert.Equal(update ? "staff-only" : null, stored.InternalRemark);
        Assert.NotNull(stored.ModifiedDate);
        Assert.InRange(stored.ModifiedDate.GetValueOrDefault(), started, DateTime.UtcNow.AddSeconds(5));
        Assert.NotNull(stored.CreatedDate);
        if (update) Assert.Equal(createdBefore, stored.CreatedDate);
        else Assert.InRange(stored.CreatedDate.GetValueOrDefault(), started, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(update ? 2 : 3, await db.Customers.CountAsync(token));
        Assert.Equal(untouched, await SnapshotAsync(id, token));
        await AssertCachesAsync(cache, !update, token);
        using var read = await client.GetAsync($"/customers/{id}", token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var wire = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
        var item = wire.RootElement;
        Assert.Equal(id, item.GetProperty("Id").GetInt32());
        Assert.Equal("Envelope Fixture", item.GetProperty("FullName").GetString());
        Assert.Equal("Before company", item.GetProperty("Company").GetProperty("Name").GetString());
        Assert.Equal("Before road", item.GetProperty("BillingAddress").GetProperty("AddressLine1").GetString());
        Assert.Equal(1, item.GetProperty("ShippingAddress").GetProperty("Id").GetInt32());
        foreach (var field in new[] { "Mobile", "InternalRemark", "firstName", "PasswordHash", "SecurityStamp", "Revision" })
            Assert.False(item.TryGetProperty(field, out _));
        Assert.DoesNotContain("forged", item.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyedCreateAndVersionedPut_RetainTheirNativeIgnoredEnvelopeBehavior(bool versioned)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = lifetime.Token;
        await SeedAsync(token);
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        client.Timeout = TimeSpan.FromSeconds(15);
        var untouched = await SnapshotAsync(versioned ? 1 : null, token, includeReplay: false);
        using var request = new HttpRequestMessage(versioned ? HttpMethod.Put : HttpMethod.Post,
            versioned ? "/customers/1/versioned" : "/customers")
        {
            Content = Json(Body("\"Id\":{},\"Company\":[],\"BillingAddress\":{\"CountryId\":{}},\"CreatedDate\":[]")),
        };
        if (versioned)
        {
            using var version = await client.GetAsync("/customers/1/versioned", token);
            Assert.Equal(HttpStatusCode.OK, version.StatusCode);
            Assert.NotNull(version.Headers.ETag);
            request.Headers.TryAddWithoutValidation("If-Match", version.Headers.ETag!.ToString());
        }
        else request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request, token);
        Assert.Equal(versioned ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        var id = 1;
        if (!versioned)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            id = json.RootElement.GetProperty("Id").GetInt32();
            Assert.True(id > 2);
        }
        Assert.Equal(untouched, await SnapshotAsync(id, token, includeReplay: false));
        await using var db = fixture.Context();
        var stored = await db.Customers.AsNoTracking().SingleAsync(row => row.Id == id, token);
        Assert.Equal("Envelope", stored.FirstName);
        Assert.Equal(1, stored.CompanyId);
        Assert.Equal(versioned ? 0 : 1, await db.CustomerCreateOperations.CountAsync(token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryEnvelope_DoesNotBypassExistingRequiredAndControllerValidation(bool update)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = lifetime.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var body in new[]
        {
            "{\"LastName\":\"Fixture\",\"Email\":\"envelope@example.test\",\"Company\":{}}",
            "{\"FirstName\":null,\"LastName\":\"Fixture\",\"Email\":\"envelope@example.test\",\"Company\":{}}",
            "{\"FirstName\":\"Envelope\",\"LastName\":\"Fixture\",\"Email\":\"invalid\",\"Company\":{}}",
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

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    public async Task OrdinaryEnvelope_PreservesAuthorityBeforeMutation(string authority, HttpStatusCode status)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = lifetime.Token;
        await SeedAsync(token);
        await using var host = fixture.Start();
        using var client = fixture.Client(host, authority);
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var before = await SnapshotAsync(null, token);
        await PrimeAsync(cache, token);
        foreach (var update in new[] { false, true })
        {
            using var request = Request(update, Body("\"Company\":{\"Id\":{}}"));
            using var response = await client.SendAsync(request, token);
            Assert.Equal(status, response.StatusCode);
        }
        Assert.Equal(before, await SnapshotAsync(null, token));
        await AssertCachesAsync(cache, true, token);
    }

    private async Task SeedAsync(CancellationToken token)
    {
        await fixture.ResetAsync();
        await using var db = fixture.Context();
        db.Customers.Add(new Customer { FirstName = "Unrelated", LastName = "Survivor", Email = "unrelated@example.test" });
        await db.SaveChangesAsync(token);
    }

    private async Task<string> SnapshotAsync(int? excludeCustomer, CancellationToken token, bool includeReplay = true)
    {
        await using var db = fixture.Context();
        return JsonSerializer.Serialize(new
        {
            Customers = await db.Customers.AsNoTracking().Where(row => !excludeCustomer.HasValue || row.Id != excludeCustomer.Value)
                .OrderBy(row => row.Id).Select(row => new
                {
                    row.Id,
                    row.FirstName,
                    row.LastName,
                    row.FullName,
                    row.Email,
                    row.Telephone,
                    row.Mobile,
                    row.Fax,
                    row.DateOfBirth,
                    row.CompanyId,
                    row.BillingAddressId,
                    row.ShippingAddressId,
                    row.InternalRemark,
                    row.CreatedDate,
                    row.ModifiedDate,
                    Revision = EF.Property<uint>(row, "xmin"),
                }).ToArrayAsync(token),
            Companies = await db.Companies.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Addresses = await db.Addresses.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Replay = includeReplay ? await db.CustomerCreateOperations.AsNoTracking().OrderBy(row => row.Key).ToArrayAsync(token) : [],
            Completion = await db.QuotationProfileCompletionOperations.AsNoTracking().OrderBy(row => row.CustomerId).ThenBy(row => row.Key).ToArrayAsync(token),
        });
    }

    private static string Body(string fragment) =>
        "{\"FirstName\":\"Envelope\",\"LastName\":\"Fixture\",\"Email\":\"envelope@example.test\"," +
        "\"Telephone\":\"020001111\",\"Mobile\":null,\"Fax\":\"020002222\",\"DateOfBirth\":\"1990-02-03\"," +
        "\"CompanyId\":1,\"BillingAddressId\":1,\"ShippingAddressId\":1," + fragment + "}";

    private static HttpRequestMessage Request(bool update, string body) =>
        new(update ? HttpMethod.Put : HttpMethod.Post, update ? "/customers/1" : "/customers") { Content = Json(body) };

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task PrimeAsync(IDistributedCache cache, CancellationToken token)
    {
        await cache.SetAsync("customer:1", Encoding.UTF8.GetBytes("original-cache-one"), token);
        await cache.SetAsync("customer:2", Encoding.UTF8.GetBytes("unrelated-cache-two"), token);
    }

    private static async Task AssertCachesAsync(IDistributedCache cache, bool firstRetained, CancellationToken token)
    {
        var first = await cache.GetAsync("customer:1", token);
        if (firstRetained) Assert.Equal(Encoding.UTF8.GetBytes("original-cache-one"), first);
        else Assert.Null(first);
        Assert.Equal(Encoding.UTF8.GetBytes("unrelated-cache-two"), await cache.GetAsync("customer:2", token));
    }
}
