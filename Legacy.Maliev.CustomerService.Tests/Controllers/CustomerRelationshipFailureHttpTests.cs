using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[CollectionDefinition("Customer relationship failures", DisableParallelization = true)]
public sealed class CustomerRelationshipFailureCollection;

[Collection("Customer relationship failures")]
public sealed class CustomerRelationshipFailureHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Theory]
    [InlineData("company")]
    [InlineData("billing")]
    [InlineData("shipping")]
    public async Task MissingRelationship_CreateAndUpdateFailWithoutPersistingPartialCustomerOrEvictingUnrelatedCache(string relationship)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        await fixture.SeedOldAsync(host);
        var before = await fixture.SnapshotAsync();
        var body = new
        {
            FirstName = "Rejected",
            LastName = "Relationship",
            Email = "rejected-profile@example.test",
            CompanyId = relationship == "company" ? 999 : 1,
            BillingAddressId = relationship == "billing" ? 999 : 1,
            ShippingAddressId = relationship == "shipping" ? 999 : 1
        };
        using var created = await client.PostAsJsonAsync("/customers/", body);
        using var updated = await client.PutAsJsonAsync("/customers/1", body);
        await AssertPrivateFailureAsync(created);
        await AssertPrivateFailureAsync(updated);
        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.True(await fixture.CacheExistsAsync());
        await using var db = fixture.Context();
        Assert.Equal(1, await db.Customers.CountAsync());
        Assert.False(await db.Customers.AnyAsync(row => row.Email == "rejected-profile@example.test"));
        using var read = await client.GetAsync("/customers/1");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal("Before Customer", json.RootElement.GetProperty("FullName").GetString());
        Assert.Equal("Before company", json.RootElement.GetProperty("Company").GetProperty("Name").GetString());
        Assert.Equal("Before road", json.RootElement.GetProperty("BillingAddress").GetProperty("AddressLine1").GetString());
        Assert.Equal("Before road", json.RootElement.GetProperty("ShippingAddress").GetProperty("AddressLine1").GetString());
    }

    [Fact]
    public async Task ReferencedCompany_DeleteFailsWithoutDeletingCustomerOrChangingItsGraphAndCache()
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "company-lifecycle");
        await fixture.SeedOldAsync(host);
        var before = await fixture.SnapshotAsync();
        using var deleted = await client.DeleteAsync("/customers/companies/1");
        await AssertPrivateFailureAsync(deleted);
        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.True(await fixture.CacheExistsAsync());
        using var read = await client.GetAsync("/customers/companies/1");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal("Before company", json.RootElement.GetProperty("Name").GetString());
    }

    [Fact]
    public async Task ExplicitCustomerDetach_AllowsCompanyDeleteAndKeepsCustomerAndAddresses()
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "company-lifecycle");
        await fixture.SeedOldAsync(host);
        await using var db = fixture.Context();
        var beforeAddress = JsonSerializer.Serialize(await db.Addresses.AsNoTracking().SingleAsync());
        using var detached = await client.PutAsJsonAsync("/customers/1", new
        {
            FirstName = "Before",
            LastName = "Customer",
            Email = "detail@example.test",
            CompanyId = (int?)null,
            BillingAddressId = 1,
            ShippingAddressId = 1
        });
        Assert.Equal(HttpStatusCode.NoContent, detached.StatusCode);
        Assert.False(await fixture.CacheExistsAsync());
        using var deleted = await client.DeleteAsync("/customers/companies/1");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(await db.Companies.AnyAsync());
        var customer = await db.Customers.AsNoTracking().SingleAsync();
        Assert.Equal(1, customer.Id);
        Assert.Null(customer.CompanyId);
        Assert.Equal(1, customer.BillingAddressId);
        Assert.Equal(1, customer.ShippingAddressId);
        Assert.Equal("staff-only", customer.InternalRemark);
        Assert.Equal(beforeAddress, JsonSerializer.Serialize(await db.Addresses.AsNoTracking().SingleAsync()));
        using var read = await client.GetAsync("/customers/1");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal("Before Customer", json.RootElement.GetProperty("FullName").GetString());
        Assert.False(json.RootElement.TryGetProperty("Company", out _));
        Assert.False(json.RootElement.TryGetProperty("CompanyId", out _));
        Assert.Equal(1, json.RootElement.GetProperty("BillingAddress").GetProperty("Id").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("InternalRemark", out _));
    }

    private static async Task AssertPrivateFailureAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(500, json.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("details").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        foreach (var privateText in new[] { "Npgsql", "FK_Customer", "23503", "detail@example.test", "rejected-profile@example.test", "staff-only", "Before company" })
            Assert.DoesNotContain(privateText, body, StringComparison.Ordinal);
    }
}
