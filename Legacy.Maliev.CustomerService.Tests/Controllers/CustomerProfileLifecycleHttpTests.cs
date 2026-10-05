using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[CollectionDefinition("Customer profile lifecycle", DisableParallelization = true)]
public sealed class CustomerProfileLifecycleCollection;

[Collection("Customer profile lifecycle")]
public sealed class CustomerProfileLifecycleHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalProfileLifecycle_PersistsNullableFieldsAndDeletesOnlyTheNewCustomer(bool related)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        await fixture.SeedOldAsync(host);
        await using var db = fixture.Context();
        var originalCustomer = JsonSerializer.Serialize(await db.Customers.AsNoTracking().SingleAsync());
        var originalCompany = JsonSerializer.Serialize(await db.Companies.AsNoTracking().SingleAsync());
        var originalAddress = JsonSerializer.Serialize(await db.Addresses.AsNoTracking().SingleAsync());
        using var created = await client.PostAsJsonAsync("/customers/", new
        {
            Id = 999,
            FirstName = "à¸ªà¸¡à¸Šà¸²à¸¢",
            LastName = "Fixture",
            Email = "profile@example.test",
            Telephone = related ? "020000000" : null,
            Mobile = related ? "0800000000" : null,
            Fax = related ? "020000001" : null,
            DateOfBirth = related ? new DateTime(1980, 1, 1) : (DateTime?)null,
            CompanyId = related ? 1 : (int?)null,
            BillingAddressId = related ? 1 : (int?)null,
            ShippingAddressId = related ? 1 : (int?)null,
            CreatedDate = new DateTime(1900, 1, 1),
            ModifiedDate = new DateTime(1900, 1, 1),
            InternalRemark = "forged private remark"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdJson.RootElement.GetProperty("Id").GetInt32();
        Assert.True(id > 1);
        Assert.NotEqual(999, id);
        Assert.EndsWith($"/customers/{id}", created.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        var beforeUpdate = await db.Customers.AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.Equal("à¸ªà¸¡à¸Šà¸²à¸¢", beforeUpdate.FirstName);
        Assert.Equal("Fixture", beforeUpdate.LastName);
        Assert.Equal("à¸ªà¸¡à¸Šà¸²à¸¢ Fixture", beforeUpdate.FullName);
        Assert.Equal("profile@example.test", beforeUpdate.Email);
        Assert.Equal(related ? "020000000" : null, beforeUpdate.Telephone);
        Assert.Equal(related ? "0800000000" : null, beforeUpdate.Mobile);
        Assert.Equal(related ? "020000001" : null, beforeUpdate.Fax);
        Assert.Equal(related ? new DateTime(1980, 1, 1) : (DateTime?)null, beforeUpdate.DateOfBirth);
        Assert.Equal(related ? 1 : (int?)null, beforeUpdate.CompanyId);
        Assert.Equal(related ? 1 : (int?)null, beforeUpdate.BillingAddressId);
        Assert.Equal(related ? 1 : (int?)null, beforeUpdate.ShippingAddressId);
        Assert.Null(beforeUpdate.InternalRemark);
        Assert.True(beforeUpdate.CreatedDate > new DateTime(2020, 1, 1));
        using var read = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal("à¸ªà¸¡à¸Šà¸²à¸¢ Fixture", json.RootElement.GetProperty("FullName").GetString());
        Assert.False(json.RootElement.TryGetProperty("firstName", out _));
        Assert.False(json.RootElement.TryGetProperty("InternalRemark", out _));
        if (related)
        {
            Assert.Equal("Before company", json.RootElement.GetProperty("Company").GetProperty("Name").GetString());
            Assert.Equal("Before road", json.RootElement.GetProperty("BillingAddress").GetProperty("AddressLine1").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("ShippingAddress").GetProperty("Id").GetInt32());
        }
        else
        {
            foreach (var field in new[] { "Telephone", "Mobile", "Fax", "DateOfBirth", "CompanyId", "BillingAddressId", "ShippingAddressId", "Company", "BillingAddress", "ShippingAddress" })
                Assert.False(json.RootElement.TryGetProperty(field, out _));
        }
        using var updated = await client.PutAsJsonAsync($"/customers/{id}/", new
        {
            FirstName = "Updated",
            LastName = "Profile",
            Email = "updated-profile@example.test",
            Telephone = "030000000",
            Mobile = "0900000000",
            Fax = "030000001",
            DateOfBirth = new DateTime(1990, 2, 3),
            CompanyId = 1,
            BillingAddressId = 1,
            ShippingAddressId = 1,
            CreatedDate = new DateTime(1900, 1, 1),
            InternalRemark = "forged update"
        });
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        var afterUpdate = await db.Customers.AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.Equal(beforeUpdate.CreatedDate, afterUpdate.CreatedDate);
        Assert.True(afterUpdate.ModifiedDate > beforeUpdate.ModifiedDate);
        Assert.Equal("Updated", afterUpdate.FirstName);
        Assert.Equal("Profile", afterUpdate.LastName);
        Assert.Equal("Updated Profile", afterUpdate.FullName);
        Assert.Equal("updated-profile@example.test", afterUpdate.Email);
        Assert.Equal("030000000", afterUpdate.Telephone);
        Assert.Equal("0900000000", afterUpdate.Mobile);
        Assert.Equal("030000001", afterUpdate.Fax);
        Assert.Equal(new DateTime(1990, 2, 3), afterUpdate.DateOfBirth);
        Assert.Equal(1, afterUpdate.CompanyId);
        Assert.Equal(1, afterUpdate.BillingAddressId);
        Assert.Equal(1, afterUpdate.ShippingAddressId);
        Assert.Null(afterUpdate.InternalRemark);
        using var updatedRead = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, updatedRead.StatusCode);
        using var updatedJson = JsonDocument.Parse(await updatedRead.Content.ReadAsStringAsync());
        Assert.Equal("Updated Profile", updatedJson.RootElement.GetProperty("FullName").GetString());
        Assert.False(updatedJson.RootElement.TryGetProperty("InternalRemark", out _));
        using var deleted = await client.DeleteAsync($"/customers/{id}/");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(await db.Customers.AnyAsync(row => row.Id == id));
        using var missing = await client.GetAsync(created.Headers.Location);
        using var repeatedDelete = await client.DeleteAsync($"/customers/{id}/");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, repeatedDelete.StatusCode);
        Assert.Equal(originalCustomer, JsonSerializer.Serialize(await db.Customers.AsNoTracking().SingleAsync()));
        Assert.Equal(originalCompany, JsonSerializer.Serialize(await db.Companies.AsNoTracking().SingleAsync()));
        Assert.Equal(originalAddress, JsonSerializer.Serialize(await db.Addresses.AsNoTracking().SingleAsync()));
        Assert.True(await fixture.CacheExistsAsync());
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    public async Task InvalidProfileAuthority_CannotCreateReadOrDeleteStoredCustomer(string authority, HttpStatusCode expected)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, authority);
        await fixture.SeedOldAsync(host);
        var before = await fixture.SnapshotAsync();
        using var created = await client.PostAsJsonAsync("/customers/", new { FirstName = "Refused", LastName = "Profile", Email = "refused@example.test" });
        using var read = await client.GetAsync("/customers/1");
        using var deleted = await client.DeleteAsync("/customers/1");
        foreach (var response in new[] { created, read, deleted })
        {
            Assert.Equal(expected, response.StatusCode);
            Assert.DoesNotContain("detail@example.test", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.True(await fixture.CacheExistsAsync());
    }

    [Fact]
    public async Task ReadUpdateAuthority_DoesNotGrantCreateOrDelete()
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host);
        await fixture.SeedOldAsync(host);
        var before = await fixture.SnapshotAsync();
        using var created = await client.PostAsJsonAsync("/customers/", new { FirstName = "Refused", LastName = "Profile", Email = "refused@example.test" });
        using var deleted = await client.DeleteAsync("/customers/1");
        using var read = await client.GetAsync("/customers/1");
        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.True(await fixture.CacheExistsAsync());
    }

    [Theory]
    [InlineData("{\"FirstName\":\"Fixture\",\"LastName\":\"Profile\",\"Email\":\"invalid\"}")]
    [InlineData("{\"FirstName\":\"\",\"LastName\":\"Profile\",\"Email\":\"fixture@example.test\"}")]
    [InlineData("{\"FirstName\":\"Fixture\",\"LastName\":null,\"Email\":\"fixture@example.test\"}")]
    public async Task InvalidProfileInput_RejectsBeforeCreateOrUpdateWithoutMutation(string body)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        await fixture.SeedOldAsync(host);
        var before = await fixture.SnapshotAsync();
        using var created = await client.PostAsync("/customers/", new StringContent(body, Encoding.UTF8, "application/json"));
        using var updated = await client.PutAsync("/customers/1", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);
        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.True(await fixture.CacheExistsAsync());
    }
}
