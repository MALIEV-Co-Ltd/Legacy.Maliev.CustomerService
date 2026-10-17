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
public sealed class CustomerAddressOriginalEnvelopeHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryAddressIgnoredTypedEnvelope_RejectsBeforeGraphAndCacheWrites(bool update)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = lifetime.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var fragment in new[]
        {
            "\"Id\":{}",
            "\"CreatedDate\":\"not-a-date\"",
            "\"ModifiedDate\":[]",
            "\"CustomerBillingAddress\":{}",
            "\"CustomerShippingAddress\":[{\"Id\":{}}]",
            "\"CustomerBillingAddress\":[{\"CompanyId\":[]}]",
            "\"CustomerShippingAddress\":[{\"Company\":{\"Customer\":[{\"ModifiedDate\":{}}]}}]",
            "\"CustomerBillingAddress\":[{\"ShippingAddress\":{\"CountryId\":\"not-an-integer\"}}]",
        })
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
    public async Task OrdinaryAddressValidEnvelope_ProjectsScalarsWithoutAttachingOrWritingSubmittedGraphs(bool update)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = lifetime.Token;
        await SeedAsync(token);
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        await PrimeAsync(cache, token);
        var untouched = await SnapshotAsync(update ? 1 : null, token);
        await using var beforeDb = fixture.Context();
        var createdBefore = (await beforeDb.Addresses.AsNoTracking().SingleAsync(row => row.Id == 1, token)).CreatedDate;
        const string graph = "\"Id\":999,\"CreatedDate\":\"1900-01-01\",\"ModifiedDate\":\"1900-01-01\"," +
            "\"CustomerBillingAddress\":[{\"Id\":998,\"FirstName\":\"forged\",\"Email\":\"nested@example.test\",\"Company\":{\"Id\":997,\"Name\":\"forged company\",\"Customer\":[{\"Id\":996}]}}]," +
            "\"CustomerShippingAddress\":[{\"Id\":995,\"ShippingAddress\":{\"Id\":994,\"AddressLine1\":\"forged road\",\"CountryId\":764}}]";
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
            var item = json.RootElement;
            id = item.GetProperty("Id").GetInt32();
            Assert.True(id > 1);
            Assert.NotEqual(999, id);
            Assert.NotNull(response.Headers.Location);
            Assert.EndsWith($"/customers/1/addresses/{id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
            AssertWire(item, id);
            Assert.False(item.TryGetProperty("CustomerBillingAddress", out _));
            Assert.False(item.TryGetProperty("CustomerShippingAddress", out _));
        }
        await using var db = fixture.Context();
        var stored = await db.Addresses.AsNoTracking().SingleAsync(row => row.Id == id, token);
        Assert.Equal("Building", stored.Building);
        Assert.Equal("Envelope road", stored.AddressLine1);
        Assert.Null(stored.AddressLine2);
        Assert.Equal("Bangkok", stored.City);
        Assert.Equal("State", stored.State);
        Assert.Equal("10100", stored.PostalCode);
        Assert.Equal(764, stored.CountryId);
        Assert.NotNull(stored.ModifiedDate);
        Assert.InRange(stored.ModifiedDate.GetValueOrDefault(), started, DateTime.UtcNow.AddSeconds(5));
        Assert.NotNull(stored.CreatedDate);
        if (update) Assert.Equal(createdBefore, stored.CreatedDate);
        else Assert.InRange(stored.CreatedDate.GetValueOrDefault(), started, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(update ? 1 : 2, await db.Addresses.CountAsync(token));
        Assert.Equal(untouched, await SnapshotAsync(id, token));
        await AssertCachesAsync(cache, false, token);
        using var existingRead = await client.GetAsync("/customers/1/addresses/1", token);
        Assert.Equal(HttpStatusCode.OK, existingRead.StatusCode);
        using var existingJson = JsonDocument.Parse(await existingRead.Content.ReadAsStringAsync(token));
        if (update) AssertWire(existingJson.RootElement, 1);
        else
        {
            // The retained create route creates an unattached address; a submitted
            // navigation collection must not silently attach it to the customer.
            Assert.Equal("Before road", existingJson.RootElement.GetProperty("AddressLine1").GetString());
            using var unattached = await client.GetAsync($"/customers/1/addresses/{id}", token);
            Assert.Equal(HttpStatusCode.NotFound, unattached.StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryAddressEnvelope_PreservesControllerAndScalarValidation(bool update)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = lifetime.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var body in new[]
        {
            "{\"AddressLine1\":\"road\",\"CountryId\":{},\"CustomerBillingAddress\":[]}",
            "{\"AddressLine1\":" + JsonSerializer.Serialize(new string('x', 257)) + ",\"CountryId\":764,\"CustomerBillingAddress\":[]}",
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
    public async Task OrdinaryAddressEnvelope_PreservesAuthorityBeforeMutation(string authority, HttpStatusCode status)
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
            using var request = Request(update, Body("\"CustomerBillingAddress\":[{\"Id\":{}}]"));
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

    private async Task<string> SnapshotAsync(int? excludeAddress, CancellationToken token)
    {
        await using var db = fixture.Context();
        return JsonSerializer.Serialize(new
        {
            Customers = await db.Customers.AsNoTracking().OrderBy(row => row.Id).Select(row => new
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
            Addresses = await db.Addresses.AsNoTracking().Where(row => !excludeAddress.HasValue || row.Id != excludeAddress.Value)
                .OrderBy(row => row.Id).ToArrayAsync(token),
            Replay = await db.CustomerCreateOperations.AsNoTracking().OrderBy(row => row.Key).ToArrayAsync(token),
            Completion = await db.QuotationProfileCompletionOperations.AsNoTracking().OrderBy(row => row.CustomerId).ThenBy(row => row.Key).ToArrayAsync(token),
        });
    }

    private static string Body(string fragment) =>
        "{\"Building\":\"Building\",\"AddressLine1\":\"Envelope road\",\"AddressLine2\":null," +
        "\"City\":\"Bangkok\",\"State\":\"State\",\"PostalCode\":\"10100\",\"CountryId\":764," + fragment + "}";

    private static HttpRequestMessage Request(bool update, string body) =>
        new(update ? HttpMethod.Put : HttpMethod.Post, update ? "/customers/Addresses/1" : "/customers/1/addresses")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static void AssertWire(JsonElement item, int id)
    {
        Assert.Equal(id, item.GetProperty("Id").GetInt32());
        Assert.Equal("Building", item.GetProperty("Building").GetString());
        Assert.Equal("Envelope road", item.GetProperty("AddressLine1").GetString());
        Assert.Equal("Bangkok", item.GetProperty("City").GetString());
        Assert.Equal("State", item.GetProperty("State").GetString());
        Assert.Equal("10100", item.GetProperty("PostalCode").GetString());
        Assert.Equal(764, item.GetProperty("CountryId").GetInt32());
        foreach (var field in new[] { "AddressLine2", "addressLine1", "CustomerBillingAddress", "CustomerShippingAddress", "PasswordHash" })
            Assert.False(item.TryGetProperty(field, out _));
        Assert.DoesNotContain("forged", item.GetRawText(), StringComparison.Ordinal);
    }

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
