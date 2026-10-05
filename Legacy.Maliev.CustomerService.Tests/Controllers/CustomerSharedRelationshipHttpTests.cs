using System.Net;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[CollectionDefinition("Customer shared relationships", DisableParallelization = true)]
public sealed class CustomerSharedRelationshipCollection;

[Collection("Customer shared relationships")]
public sealed class CustomerSharedRelationshipHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Theory]
    [InlineData("company", false)]
    [InlineData("address", false)]
    [InlineData("company", true)]
    [InlineData("address", true)]
    public async Task SharedMutation_InvalidatesEveryLinkedCustomerOrReadsCommittedGraphDespiteRealRemovalDenial(string kind, bool denyRemoval)
    {
        await ResetSharedAsync();
        await using var host = fixture.Start(restricted: denyRemoval);
        using var client = fixture.Client(host, "shared-relationship");
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        await SeedCachesAsync(cache);
        var beforeCustomers = await CustomersAsync();
        var originalThird = await DetailAsync(client, 3);
        if (denyRemoval)
        {
            await fixture.DenyRemovalAsync();
            await fixture.AssertRemovalDeniedAsync(host);
        }
        using var response = await PutAsync(client, kind);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(beforeCustomers, await CustomersAsync());
        for (var id = 1; id <= 2; id++)
        {
            var bytes = await cache.GetAsync($"customer:{id}");
            if (denyRemoval) Assert.NotNull(bytes);
            else Assert.Null(bytes);
            using var json = JsonDocument.Parse(await DetailAsync(client, id));
            var field = kind == "company" ? "Company" : id == 1 ? "BillingAddress" : "ShippingAddress";
            var property = kind == "company" ? "Name" : "AddressLine1";
            Assert.Equal(kind == "company" ? "After shared company" : "After shared road", json.RootElement.GetProperty(field).GetProperty(property).GetString());
            Assert.False(json.RootElement.TryGetProperty("InternalRemark", out _));
            if (kind == "address" && id == 1)
                Assert.Equal("After shared road", json.RootElement.GetProperty("ShippingAddress").GetProperty("AddressLine1").GetString());
        }
        Assert.NotNull(await cache.GetAsync("customer:3"));
        Assert.Equal(originalThird, await DetailAsync(client, 3));
        await using var db = fixture.Context();
        Assert.Equal("Unrelated company", (await db.Companies.AsNoTracking().SingleAsync(row => row.Id == 2)).Name);
        Assert.Equal("Unrelated road", (await db.Addresses.AsNoTracking().SingleAsync(row => row.Id == 2)).AddressLine1);
    }

    [Theory]
    [InlineData("company")]
    [InlineData("address")]
    public async Task SharedReferencedDelete_GenericForeignKeyFailurePreservesAllRowsAndCacheBytes(string kind)
    {
        await ResetSharedAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "shared-relationship");
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        await SeedCachesAsync(cache);
        var before = await SnapshotAsync();
        var cached = await CacheSnapshotAsync(cache);
        using var response = await client.DeleteAsync(kind == "company" ? "/customers/companies/1" : "/customers/1/addresses/1");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(500, json.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("details").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        foreach (var privateText in new[] { "Npgsql", "FK_Customer", "23503", "detail@example.test", "second@example.test", "staff-only", "Before company", "Before road" })
            Assert.DoesNotContain(privateText, body, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(cached, await CacheSnapshotAsync(cache));
    }

    [Fact]
    public async Task SharedAddressDelete_UnrelatedCustomerRouteCannotDeleteAnotherCustomersAddress()
    {
        await ResetSharedAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "shared-relationship");
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        await SeedCachesAsync(cache);
        var before = await SnapshotAsync();
        var cached = await CacheSnapshotAsync(cache);
        using var response = await client.DeleteAsync("/customers/3/addresses/1");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(cached, await CacheSnapshotAsync(cache));
    }

    [Theory]
    [InlineData("company")]
    [InlineData("address")]
    public async Task InvalidSharedMutation_RejectsBeforeChangingAnyLinkedGraphOrCache(string kind)
    {
        await AssertNoMutationAsync(kind, invalid: true, missing: false, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("company")]
    [InlineData("address")]
    public async Task MissingSharedMutation_ReturnsNotFoundWithoutInvalidatingAnyExistingProjection(string kind)
    {
        await AssertNoMutationAsync(kind, invalid: false, missing: true, HttpStatusCode.NotFound);
    }

    private async Task AssertNoMutationAsync(string kind, bool invalid, bool missing, HttpStatusCode expected)
    {
        await ResetSharedAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "shared-relationship");
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        await SeedCachesAsync(cache);
        var before = await SnapshotAsync();
        var cached = await CacheSnapshotAsync(cache);
        using var response = await PutAsync(client, kind, invalid, missing ? 999 : 1);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(cached, await CacheSnapshotAsync(cache));
    }

    private async Task ResetSharedAsync()
    {
        await fixture.ResetAsync();
        await using var db = fixture.Context();
        db.Customers.Add(new Customer
        {
            FirstName = "Second",
            LastName = "Customer",
            Email = "second@example.test",
            CompanyId = 1,
            ShippingAddressId = 1,
            InternalRemark = "second-private"
        });
        await db.SaveChangesAsync();
        db.Customers.Add(new Customer
        {
            FirstName = "Unrelated",
            LastName = "Customer",
            Email = "unrelated@example.test",
            Company = new Company { Name = "Unrelated company" },
            BillingAddress = new Address { AddressLine1 = "Unrelated road", CountryId = 764 }
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedCachesAsync(IDistributedCache cache)
    {
        for (var id = 1; id <= 3; id++)
            await cache.SetAsync($"customer:{id}", Encoding.UTF8.GetBytes($"{{\"id\":{id},\"firstName\":\"Old writer\",\"lastName\":\"Fixture\",\"fullName\":\"Old writer Fixture\",\"email\":\"old@example.test\"}}"),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
    }

    private static async Task<string> CacheSnapshotAsync(IDistributedCache cache) =>
        JsonSerializer.Serialize(await Task.WhenAll(Enumerable.Range(1, 3).Select(id => cache.GetAsync($"customer:{id}"))));

    private static async Task<string> DetailAsync(HttpClient client, int id)
    {
        using var response = await client.GetAsync($"/customers/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<string> CustomersAsync()
    {
        await using var db = fixture.Context();
        return JsonSerializer.Serialize(await db.Customers.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.FirstName, row.LastName, row.Email, row.CompanyId, row.BillingAddressId, row.ShippingAddressId, row.CreatedDate, row.ModifiedDate, row.InternalRemark }).ToArrayAsync());
    }

    private async Task<string> SnapshotAsync()
    {
        await using var db = fixture.Context();
        return JsonSerializer.Serialize(new
        {
            Customers = await CustomersAsync(),
            Companies = await db.Companies.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.Name, row.TaxNumber, row.Registrar, row.CreatedDate, row.ModifiedDate }).ToArrayAsync(),
            Addresses = await db.Addresses.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.AddressLine1, row.City, row.CountryId, row.CreatedDate, row.ModifiedDate }).ToArrayAsync()
        });
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string kind, bool invalid = false, int id = 1)
    {
        var route = kind == "company" ? $"/customers/companies/{id}" : $"/customers/addresses/{id}";
        var body = kind == "company" ? """{"Name":"After shared company","TaxNumber":"123"}""" : """{"AddressLine1":"After shared road","City":"Bangkok","CountryId":764}""";
        if (invalid) body = kind == "company" ? """{"Name":""}""" : """{"AddressLine1":"","CountryId":764}""";
        return client.PutAsync(route, new StringContent(body, Encoding.UTF8, "application/json"));
    }
}
