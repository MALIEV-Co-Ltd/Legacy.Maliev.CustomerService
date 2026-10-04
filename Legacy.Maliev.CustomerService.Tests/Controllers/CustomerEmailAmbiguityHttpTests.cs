using System.Net;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

public sealed class CustomerEmailAmbiguityHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DuplicateExactEmail_CannotSelectAnArbitraryCustomerOrChangeStoredGraph(int matches)
    {
        await SeedDuplicatesAsync(matches);
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "directory");
        await fixture.SeedOldAsync(host);
        var before = await SnapshotAsync();

        using var response = await client.GetAsync("/customers/emails/detail%40example.test");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("detail@example.test", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Duplicate", body, StringComparison.Ordinal);
        Assert.DoesNotContain("staff-only", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SingleOrDefault", body, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync());
        Assert.True(await fixture.CacheExistsAsync());
    }

    [Theory]
    [InlineData("detail@example.test")]
    [InlineData("DETAIL@EXAMPLE.TEST")]
    [InlineData(" detail@example.test ")]
    public async Task UniqueEmail_RetainsCurrentCaseAndTrimContractAndCompleteProfile(string lookup)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "directory");
        var before = await SnapshotAsync();

        using var response = await client.GetAsync("/customers/emails/" + Uri.EscapeDataString(lookup));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var profile = document.RootElement;
        Assert.Equal(1, profile.GetProperty("Id").GetInt32());
        Assert.Equal("detail@example.test", profile.GetProperty("Email").GetString());
        Assert.Equal("Before company", profile.GetProperty("Company").GetProperty("Name").GetString());
        Assert.Equal("Before road", profile.GetProperty("BillingAddress").GetProperty("AddressLine1").GetString());
        Assert.Equal("Before road", profile.GetProperty("ShippingAddress").GetProperty("AddressLine1").GetString());
        Assert.False(profile.TryGetProperty("InternalRemark", out _));
        Assert.False(profile.TryGetProperty("PasswordHash", out _));
        Assert.False(profile.TryGetProperty("email", out _));
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    [InlineData("full", HttpStatusCode.Forbidden)]
    public async Task InvalidOrReadOnlyAuthority_CannotReachAmbiguousEmailLookup(string authority, HttpStatusCode expected)
    {
        await SeedDuplicatesAsync(2);
        await using var host = fixture.Start();
        using var client = fixture.Client(host, authority);
        var before = await SnapshotAsync();

        using var response = await client.GetAsync("/customers/emails/detail%40example.test");

        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain("detail@example.test", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync());
        Assert.False(await fixture.CacheExistsAsync());
    }

    [Fact]
    public async Task AbsentEmail_RemainsNotFoundWithoutMutatingTheExistingGraph()
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "directory");
        var before = await SnapshotAsync();

        using var response = await client.GetAsync("/customers/emails/absent%40example.test");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    private async Task SeedDuplicatesAsync(int matches)
    {
        await fixture.ResetAsync();
        await using var db = fixture.Context();
        for (var index = 1; index < matches; index++)
        {
            db.Customers.Add(new Customer
            {
                FirstName = "Duplicate" + index,
                LastName = "Synthetic",
                Email = "detail@example.test",
                InternalRemark = "duplicate-private-fixture"
            });
        }
        await db.SaveChangesAsync();
        Assert.Equal(matches, await db.Customers.CountAsync(row => row.Email == "detail@example.test"));
    }

    private async Task<string> SnapshotAsync()
    {
        await using var db = fixture.Context();
        return JsonSerializer.Serialize(new
        {
            Customers = await db.Customers.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.FirstName, row.LastName, row.Email, row.CompanyId,
                    row.BillingAddressId, row.ShippingAddressId, row.InternalRemark, row.ModifiedDate }).ToArrayAsync(),
            Companies = await db.Companies.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.Name, row.TaxNumber, row.ModifiedDate }).ToArrayAsync(),
            Addresses = await db.Addresses.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.AddressLine1, row.CountryId, row.ModifiedDate }).ToArrayAsync()
        });
    }
}
