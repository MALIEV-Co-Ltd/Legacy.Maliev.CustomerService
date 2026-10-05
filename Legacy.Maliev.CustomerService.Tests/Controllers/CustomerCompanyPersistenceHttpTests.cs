using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[CollectionDefinition("Customer company persistence", DisableParallelization = true)]
public sealed class CustomerCompanyPersistenceCollection;

[Collection("Customer company persistence")]
public sealed class CustomerCompanyPersistenceHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Theory]
    [InlineData("Name", false)]
    [InlineData("TaxNumber", false)]
    [InlineData("Registrar", false)]
    [InlineData("Name", true)]
    [InlineData("TaxNumber", true)]
    [InlineData("Registrar", true)]
    public async Task CompanyLengthBoundary_Persists256AndRejects257WithoutChangingSharedGraphOrCache(string field, bool update)
    {
        await SeedAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "company-lifecycle");
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var old = await PrimeAsync(cache);
        var unrelatedBefore = await SnapshotAsync(update ? 1 : null);
        await using var db = fixture.Context();
        var originalCreated = (await db.Companies.AsNoTracking().SingleAsync(row => row.Id == 1)).CreatedDate;
        var valid = Payload(field, 256);
        using var accepted = await SendAsync(client, update, valid);
        Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, accepted.StatusCode);
        var id = 1;
        if (update)
        {
            Assert.Null(await cache.GetAsync("customer:1"));
            Assert.Null(await cache.GetAsync("customer:2"));
            Assert.Equal(string.Empty, await accepted.Content.ReadAsStringAsync());
        }
        else
        {
            using var created = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
            id = created.RootElement.GetProperty("Id").GetInt32();
            Assert.NotEqual(999999, id);
            Assert.EndsWith($"/Companies/{id}", accepted.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
            foreach (var customerId in new[] { 1, 2 }) Assert.Equal(old[customerId], await cache.GetAsync($"customer:{customerId}"));
            Assert.False(await db.Customers.AnyAsync(row => row.CompanyId == id));
        }
        Assert.Equal(old[3], await cache.GetAsync("customer:3"));
        Assert.Equal(unrelatedBefore, await SnapshotAsync(id));
        var saved = await db.Companies.AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.Equal(valid["Name"], saved.Name);
        Assert.Equal(valid["TaxNumber"], saved.TaxNumber);
        Assert.Equal(valid["Registrar"], saved.Registrar);
        Assert.True(saved.ModifiedDate > new DateTime(2020, 1, 1));
        if (update) Assert.Equal(originalCreated, saved.CreatedDate);
        else Assert.True(saved.CreatedDate > new DateTime(2020, 1, 1));
        using var read = await client.GetAsync($"/customers/companies/{id}/");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal((string)valid[field]!, json.RootElement.GetProperty(field).GetString());
        Assert.False(json.RootElement.TryGetProperty("taxNumber", out _));
        if (update)
        {
            foreach (var customerId in new[] { 1, 2 })
            {
                using var detail = await client.GetAsync($"/customers/{customerId}/");
                Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
                using var customer = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
                Assert.Equal((string)valid[field]!, customer.RootElement.GetProperty("Company").GetProperty(field).GetString());
                Assert.False(customer.RootElement.TryGetProperty("InternalRemark", out _));
            }
        }

        var beforeCache = await PrimeAsync(cache);
        var before = await SnapshotAsync();
        using var rejected = await SendAsync(client, update, Payload(field, 257));
        Assert.Equal(HttpStatusCode.InternalServerError, rejected.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
        foreach (var customerId in new[] { 1, 2, 3 })
            Assert.Equal(beforeCache[customerId], await cache.GetAsync($"customer:{customerId}"));
        using var error = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal(500, error.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, error.RootElement.GetProperty("details").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(error.RootElement.GetProperty("traceId").GetString()));
        var body = await rejected.Content.ReadAsStringAsync();
        foreach (var marker in new[] { new string('ก', 257), "Npgsql", "character varying", "staff-only" })
            Assert.DoesNotContain(marker, body, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> Payload(string field, int length)
    {
        var payload = new Dictionary<string, object?>
        {
            ["Id"] = 999999,
            ["Name"] = "Replacement company",
            ["TaxNumber"] = "0100000000000",
            ["Registrar"] = "Replacement registrar",
            ["CreatedDate"] = new DateTime(1900, 1, 1),
            ["ModifiedDate"] = new DateTime(1900, 1, 1)
        };
        payload[field] = new string('ก', length);
        return payload;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, bool update, Dictionary<string, object?> payload) => update
        ? client.PutAsJsonAsync("/customers/companies/1/", payload)
        : client.PostAsJsonAsync("/customers/companies/", payload);

    private static async Task<Dictionary<int, byte[]>> PrimeAsync(IDistributedCache cache)
    {
        var entries = new Dictionary<int, byte[]>();
        for (var id = 1; id <= 3; id++)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                id,
                firstName = "Cached",
                lastName = "Synthetic",
                fullName = "Cached Synthetic",
                email = $"cached-{id}@example.invalid"
            }));
            await cache.SetAsync($"customer:{id}", bytes,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
            Assert.Equal(bytes, await cache.GetAsync($"customer:{id}"));
            entries.Add(id, bytes);
        }
        return entries;
    }

    private async Task SeedAsync()
    {
        await fixture.ResetAsync();
        await using var db = fixture.Context();
        db.Customers.Add(new Customer
        {
            FirstName = "Second",
            LastName = "Synthetic",
            Email = "second@example.invalid",
            CompanyId = 1,
            ShippingAddressId = 1,
            InternalRemark = "second-private"
        });
        await db.SaveChangesAsync();
        db.Customers.Add(new Customer
        {
            FirstName = "Unrelated",
            LastName = "Synthetic",
            Email = "unrelated@example.invalid",
            Company = new Company { Name = "Unrelated company" },
            BillingAddress = new Address { AddressLine1 = "Unrelated road", CountryId = 764 }
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> SnapshotAsync(int? excludedCompanyId = null)
    {
        await using var db = fixture.Context();
        return JsonSerializer.Serialize(new
        {
            Customers = await db.Customers.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Companies = await db.Companies.AsNoTracking().Where(row => excludedCompanyId == null || row.Id != excludedCompanyId).OrderBy(row => row.Id).ToArrayAsync(),
            Addresses = await db.Addresses.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()
        });
    }
}
