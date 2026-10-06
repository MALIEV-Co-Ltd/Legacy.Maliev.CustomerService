using System.Net;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

public sealed class CustomerProfileDeletionIsolationHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteProfile_PreservesSharedGraphAndSurvivorCachesAndRejectsLateOldFill(bool denyRemoval)
    {
        await fixture.ResetAsync();
        int sharedCustomerId, unrelatedCustomerId, sharedCompanyId, sharedAddressId, unrelatedCompanyId, unrelatedAddressId;
        await using (var db = fixture.Context())
        {
            var target = await db.Customers.AsNoTracking().SingleAsync(customer => customer.Id == 1);
            sharedCompanyId = target.CompanyId!.Value;
            sharedAddressId = target.BillingAddressId!.Value;
            Assert.Equal(sharedAddressId, target.ShippingAddressId);
            var shared = new Customer
            {
                FirstName = "Shared survivor",
                LastName = "Customer",
                Email = "shared-survivor@example.test",
                CompanyId = sharedCompanyId,
                BillingAddressId = sharedAddressId,
                ShippingAddressId = sharedAddressId,
                InternalRemark = "shared-private"
            };
            var unrelated = new Customer
            {
                FirstName = "Unrelated survivor",
                LastName = "Customer",
                Email = "unrelated-survivor@example.test",
                Company = new Company { Name = "Unrelated company", TaxNumber = "456", Registrar = "Unrelated registrar" },
                BillingAddress = new Address { AddressLine1 = "Unrelated road", City = "Chiang Mai", CountryId = 764 },
                InternalRemark = "unrelated-private"
            };
            unrelated.ShippingAddress = unrelated.BillingAddress;
            db.Customers.AddRange(shared, unrelated);
            await db.SaveChangesAsync();
            sharedCustomerId = shared.Id;
            unrelatedCustomerId = unrelated.Id;
            unrelatedCompanyId = unrelated.CompanyId!.Value;
            unrelatedAddressId = unrelated.BillingAddressId!.Value;
        }
        await using (var readback = fixture.Context())
        {
            var customers = await readback.Customers.AsNoTracking().OrderBy(customer => customer.Id).ToArrayAsync();
            Assert.Equal(new[] { 1, sharedCustomerId, unrelatedCustomerId }.OrderBy(id => id).ToArray(), customers.Select(customer => customer.Id).ToArray());
            var shared = Assert.Single(customers, customer => customer.Id == sharedCustomerId);
            Assert.Equal(sharedCompanyId, shared.CompanyId);
            Assert.Equal(sharedAddressId, shared.BillingAddressId);
            Assert.Equal(sharedAddressId, shared.ShippingAddressId);
            var unrelated = Assert.Single(customers, customer => customer.Id == unrelatedCustomerId);
            Assert.Equal(unrelatedCompanyId, unrelated.CompanyId);
            Assert.Equal(unrelatedAddressId, unrelated.BillingAddressId);
            Assert.Equal(unrelatedAddressId, unrelated.ShippingAddressId);
            Assert.NotEqual(sharedCompanyId, unrelatedCompanyId);
            Assert.NotEqual(sharedAddressId, unrelatedAddressId);
            Assert.Equal(2, await readback.Companies.CountAsync());
            Assert.Equal(2, await readback.Addresses.CountAsync());
        }
        var expectedSurvivingGraph = await SnapshotAsync(excludeTarget: true);
        await using var first = fixture.Start(restricted: denyRemoval);
        await using var second = fixture.Start(restricted: denyRemoval);
        using var writer = fixture.Client(first, "customer-lifecycle");
        using var reader = fixture.Client(second, "customer-lifecycle");
        var firstCache = first.Services.GetRequiredService<IDistributedCache>();
        var secondCache = second.Services.GetRequiredService<IDistributedCache>();
        await fixture.SeedOldAsync(first);
        var survivorBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            id = sharedCustomerId,
            firstName = "Shared survivor",
            lastName = "Customer",
            fullName = "Shared survivor Customer",
            email = "shared-survivor@example.test",
            companyId = sharedCompanyId,
            billingAddressId = sharedAddressId,
            shippingAddressId = sharedAddressId,
            company = new { id = sharedCompanyId, name = "Before company", taxNumber = "123" },
            billingAddress = new { id = sharedAddressId, addressLine1 = "Before road", city = "Bangkok", countryId = 764 },
            shippingAddress = new { id = sharedAddressId, addressLine1 = "Before road", city = "Bangkok", countryId = 764 }
        }));
        var unrelatedBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            id = unrelatedCustomerId,
            firstName = "Unrelated survivor",
            lastName = "Customer",
            fullName = "Unrelated survivor Customer",
            email = "unrelated-survivor@example.test",
            companyId = unrelatedCompanyId,
            billingAddressId = unrelatedAddressId,
            shippingAddressId = unrelatedAddressId,
            company = new { id = unrelatedCompanyId, name = "Unrelated company", taxNumber = "456", registrar = "Unrelated registrar" },
            billingAddress = new { id = unrelatedAddressId, addressLine1 = "Unrelated road", city = "Chiang Mai", countryId = 764 },
            shippingAddress = new { id = unrelatedAddressId, addressLine1 = "Unrelated road", city = "Chiang Mai", countryId = 764 }
        }));
        var unrelatedKey = $"customer:{unrelatedCustomerId}";
        await firstCache.SetAsync(unrelatedKey, unrelatedBytes,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
        var survivorKey = $"customer:{sharedCustomerId}";
        await firstCache.SetAsync(survivorKey, survivorBytes,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
        Assert.True(await fixture.CacheExistsAsync());
        Assert.Equal(survivorBytes, await secondCache.GetAsync(survivorKey));
        Assert.Equal(unrelatedBytes, await secondCache.GetAsync(unrelatedKey));
        Assert.Equal(survivorBytes, await firstCache.GetAsync(survivorKey));
        Assert.Equal(unrelatedBytes, await firstCache.GetAsync(unrelatedKey));
        if (denyRemoval)
        {
            await fixture.DenyRemovalAsync();
            await fixture.AssertRemovalDeniedAsync(first);
            await fixture.AssertRemovalDeniedAsync(second);
        }
        using (var deleted = await writer.DeleteAsync("/customers/1"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        Assert.Equal(expectedSurvivingGraph, await SnapshotAsync(excludeTarget: false));
        Assert.Equal(denyRemoval, await fixture.CacheExistsAsync());
        Assert.Equal(survivorBytes, await firstCache.GetAsync(survivorKey));
        Assert.Equal(unrelatedBytes, await firstCache.GetAsync(unrelatedKey));
        Assert.Equal(survivorBytes, await secondCache.GetAsync(survivorKey));
        Assert.Equal(unrelatedBytes, await secondCache.GetAsync(unrelatedKey));

        // Independent old-writer DTO fill after deletion must not resurrect the profile in either app.
        await fixture.SeedOldAsync(second);
        Assert.True(await fixture.CacheExistsAsync());
        foreach (var client in new[] { writer, reader })
        {
            using var missing = await client.GetAsync("/customers/1");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var body = await missing.Content.ReadAsStringAsync();
            Assert.DoesNotContain("detail@example.test", body, StringComparison.Ordinal);
            Assert.DoesNotContain("staff-only", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"FirstName\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("PasswordHash", body, StringComparison.Ordinal);
            await AssertSurvivorAsync(client, sharedCustomerId, "Shared survivor", sharedCompanyId, "Before company", sharedAddressId, "Before road");
            await AssertSurvivorAsync(client, unrelatedCustomerId, "Unrelated survivor", unrelatedCompanyId, "Unrelated company", unrelatedAddressId, "Unrelated road");
        }
        using var repeatedDelete = await writer.DeleteAsync("/customers/1");
        Assert.Equal(HttpStatusCode.NotFound, repeatedDelete.StatusCode);
        Assert.Equal(expectedSurvivingGraph, await SnapshotAsync(excludeTarget: false));
        Assert.Equal(survivorBytes, await firstCache.GetAsync(survivorKey));
        Assert.Equal(unrelatedBytes, await firstCache.GetAsync(unrelatedKey));
        Assert.Equal(survivorBytes, await secondCache.GetAsync(survivorKey));
        Assert.Equal(unrelatedBytes, await secondCache.GetAsync(unrelatedKey));
    }

    private static async Task AssertSurvivorAsync(HttpClient client, int id, string name, int companyId, string companyName, int addressId, string road)
    {
        using var response = await client.GetAsync($"/customers/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var customer = document.RootElement;
        Assert.Equal(id, customer.GetProperty("Id").GetInt32());
        Assert.Equal(name, customer.GetProperty("FirstName").GetString());
        Assert.Equal(companyId, customer.GetProperty("CompanyId").GetInt32());
        Assert.Equal(addressId, customer.GetProperty("BillingAddressId").GetInt32());
        Assert.Equal(addressId, customer.GetProperty("ShippingAddressId").GetInt32());
        Assert.Equal(companyId, customer.GetProperty("Company").GetProperty("Id").GetInt32());
        Assert.Equal(companyName, customer.GetProperty("Company").GetProperty("Name").GetString());
        foreach (var role in new[] { "BillingAddress", "ShippingAddress" })
        {
            Assert.Equal(addressId, customer.GetProperty(role).GetProperty("Id").GetInt32());
            Assert.Equal(road, customer.GetProperty(role).GetProperty("AddressLine1").GetString());
        }
        Assert.False(customer.TryGetProperty("id", out _));
        Assert.False(customer.TryGetProperty("InternalRemark", out _));
        Assert.False(customer.TryGetProperty("PasswordHash", out _));
        Assert.False(customer.TryGetProperty("SecurityStamp", out _));
    }

    private async Task<string> SnapshotAsync(bool excludeTarget)
    {
        await using var db = fixture.Context();
        return JsonSerializer.Serialize(new
        {
            Customers = await db.Customers.AsNoTracking().Where(customer => !excludeTarget || customer.Id != 1)
                .OrderBy(customer => customer.Id).Select(customer => new
                {
                    customer.Id,
                    customer.FirstName,
                    customer.LastName,
                    customer.FullName,
                    customer.Email,
                    customer.Telephone,
                    customer.Mobile,
                    customer.Fax,
                    customer.DateOfBirth,
                    customer.InternalRemark,
                    customer.CompanyId,
                    customer.BillingAddressId,
                    customer.ShippingAddressId,
                    customer.CreatedDate,
                    customer.ModifiedDate
                }).ToArrayAsync(),
            Companies = await db.Companies.AsNoTracking().OrderBy(company => company.Id).Select(company => new
            {
                company.Id,
                company.Name,
                company.TaxNumber,
                company.Registrar,
                company.CreatedDate,
                company.ModifiedDate
            }).ToArrayAsync(),
            Addresses = await db.Addresses.AsNoTracking().OrderBy(address => address.Id).Select(address => new
            {
                address.Id,
                address.Building,
                address.AddressLine1,
                address.AddressLine2,
                address.City,
                address.State,
                address.PostalCode,
                address.CountryId,
                address.CreatedDate,
                address.ModifiedDate
            }).ToArrayAsync()
        });
    }
}
