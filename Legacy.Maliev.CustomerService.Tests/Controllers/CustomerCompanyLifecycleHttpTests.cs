using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[CollectionDefinition("Customer company lifecycle", DisableParallelization = true)]
public sealed class CustomerCompanyLifecycleCollection;

[Collection("Customer company lifecycle")]
public sealed class CustomerCompanyLifecycleHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Theory]
    [InlineData("", "0100000000000")]
    [InlineData("บริษัท Fixture", null)]
    [InlineData("บริษัท Fixture", "0100000000000")]
    public async Task NormalCompanyLifecycle_PersistsFieldsAndLeavesUnrelatedCustomerProjectionIntact(string name, string? tax)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "company-lifecycle");
        await fixture.SeedOldAsync(host);
        await using var db = fixture.Context();
        var originalCustomer = JsonSerializer.Serialize(await db.Customers.AsNoTracking().SingleAsync());
        var originalCompany = JsonSerializer.Serialize(await db.Companies.AsNoTracking().SingleAsync());
        using var created = await client.PostAsJsonAsync("/customers/companies/", new
        {
            Id = 999,
            Name = name,
            TaxNumber = tax,
            Registrar = "Synthetic registrar",
            CreatedDate = new DateTime(1900, 1, 1),
            ModifiedDate = new DateTime(1900, 1, 1)
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdJson.RootElement.GetProperty("Id").GetInt32();
        Assert.True(id > 1);
        Assert.NotEqual(999, id);
        Assert.EndsWith($"/customers/companies/{id}", created.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        var beforeUpdate = await db.Companies.AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.Equal(name, beforeUpdate.Name);
        Assert.Equal(tax, beforeUpdate.TaxNumber);
        Assert.Equal("Synthetic registrar", beforeUpdate.Registrar);
        Assert.True(beforeUpdate.CreatedDate > new DateTime(2020, 1, 1));
        using var initialRead = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, initialRead.StatusCode);
        using var initialJson = JsonDocument.Parse(await initialRead.Content.ReadAsStringAsync());
        Assert.Equal(name, initialJson.RootElement.GetProperty("Name").GetString());
        if (tax is null) Assert.False(initialJson.RootElement.TryGetProperty("TaxNumber", out _));
        else Assert.Equal(tax, initialJson.RootElement.GetProperty("TaxNumber").GetString());
        Assert.False(initialJson.RootElement.TryGetProperty("name", out _));
        using var updated = await client.PutAsJsonAsync($"/customers/companies/{id}/", new
        {
            Name = "Updated company",
            TaxNumber = "0200000000000",
            Registrar = "Updated registrar"
        });
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        var afterUpdate = await db.Companies.AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.Equal(beforeUpdate.CreatedDate, afterUpdate.CreatedDate);
        Assert.True(afterUpdate.ModifiedDate > beforeUpdate.ModifiedDate);
        Assert.Equal("Updated company", afterUpdate.Name);
        Assert.Equal("0200000000000", afterUpdate.TaxNumber);
        Assert.Equal("Updated registrar", afterUpdate.Registrar);
        using var updatedRead = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, updatedRead.StatusCode);
        using var updatedJson = JsonDocument.Parse(await updatedRead.Content.ReadAsStringAsync());
        Assert.Equal("Updated company", updatedJson.RootElement.GetProperty("Name").GetString());
        using var deleted = await client.DeleteAsync($"/customers/companies/{id}/");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(await db.Companies.AnyAsync(row => row.Id == id));
        using var missing = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var repeatedDelete = await client.DeleteAsync($"/customers/companies/{id}/");
        Assert.Equal(HttpStatusCode.NotFound, repeatedDelete.StatusCode);
        Assert.Equal(originalCompany, JsonSerializer.Serialize(await db.Companies.AsNoTracking().SingleAsync()));
        Assert.Equal(originalCustomer, JsonSerializer.Serialize(await db.Customers.AsNoTracking().SingleAsync()));
        Assert.True(await fixture.CacheExistsAsync());
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("full", HttpStatusCode.Forbidden)]
    public async Task InvalidCompanyAuthority_CannotCreateReadOrDeleteStoredCompany(string authority, HttpStatusCode expected)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, authority);
        await fixture.SeedOldAsync(host);
        var before = await fixture.SnapshotAsync();
        using var created = await client.PostAsJsonAsync("/customers/companies/", new { Name = "Refused company", TaxNumber = "0100000000000" });
        using var read = await client.GetAsync("/customers/companies/1");
        using var deleted = await client.DeleteAsync("/customers/companies/1");
        foreach (var response in new[] { created, read, deleted })
        {
            Assert.Equal(expected, response.StatusCode);
            Assert.DoesNotContain("Before company", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.True(await fixture.CacheExistsAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Name\":null,\"TaxNumber\":\"0100000000000\"}")]
    [InlineData("{\"Name\":\"\",\"TaxNumber\":null}")]
    public async Task InvalidCompanyInput_RejectsBeforeCreateOrUpdateWithoutGraphOrCacheMutation(string body)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "company-lifecycle");
        await fixture.SeedOldAsync(host);
        var before = await fixture.SnapshotAsync();
        using var created = await client.PostAsync("/customers/companies/", new StringContent(body, Encoding.UTF8, "application/json"));
        using var updated = await client.PutAsync("/customers/companies/1", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);
        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.True(await fixture.CacheExistsAsync());
    }
}
