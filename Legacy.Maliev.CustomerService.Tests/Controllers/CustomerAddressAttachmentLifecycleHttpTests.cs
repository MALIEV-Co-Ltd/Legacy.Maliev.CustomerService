using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Serializes customer address creation and attachment lifecycle tests sharing graph state.</summary>
[CollectionDefinition("Customer address attachment lifecycle", DisableParallelization = true)]
public sealed class CustomerAddressAttachmentLifecycleCollection;

/// <summary>Checks separate address creation, explicit consumer attachment, bound readback, cache invalidation, and refused writes.</summary>
/// <param name="fixture">The shared authority fixture supplying isolated customer graph setup, HTTP hosts, signed clients, and cache observations.</param>
[Collection("Customer address attachment lifecycle")]
public sealed class CustomerAddressAttachmentLifecycleHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    /// <summary>Checks the create-then-attach address workflow without implicit attachment or changes to the original address.</summary>
    /// <param name="billing">Whether the returned new address identifier is attached as billing instead of shipping.</param>
    /// <returns>A task that completes after asserting new address persistence, not found before attachment, selected binding and cache eviction, and unrelated-parent refusal.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateThenAttach_PersistsConsumerSelectedAddressAndInvalidatesOldProjection(bool billing)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "address-lifecycle");
        await fixture.SeedOldAsync(host);
        Assert.True(await fixture.CacheExistsAsync());
        await using var db = fixture.Context();
        var before = await db.Customers.AsNoTracking().SingleAsync();
        var originalAddress = await db.Addresses.AsNoTracking().SingleAsync();
        using var created = await client.PostAsJsonAsync("/customers/1/addresses/", AddressPayload());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdJson.RootElement.GetProperty("Id").GetInt32();
        Assert.NotEqual(999, id);
        Assert.True(id > originalAddress.Id);
        Assert.EndsWith($"/customers/1/addresses/{id}", created.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        var stored = await db.Addresses.AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.Equal("New road", stored.AddressLine1);
        Assert.Equal("New building", stored.Building);
        Assert.Equal("Bangkok", stored.City);
        Assert.Equal("10100", stored.PostalCode);
        Assert.Equal(764, stored.CountryId);
        Assert.True(stored.CreatedDate > new DateTime(2020, 1, 1));
        Assert.True(stored.ModifiedDate > new DateTime(2020, 1, 1));
        Assert.False(createdJson.RootElement.TryGetProperty("AddressLine2", out _));
        // Intranet first creates the row, then supplies its returned Id in the customer update.
        var unbound = await db.Customers.AsNoTracking().SingleAsync();
        Assert.Equal(before.BillingAddressId, unbound.BillingAddressId);
        Assert.Equal(before.ShippingAddressId, unbound.ShippingAddressId);
        using var unattachedRead = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.NotFound, unattachedRead.StatusCode);
        Assert.False(await fixture.CacheExistsAsync());
        await fixture.SeedOldAsync(host);
        Assert.True(await fixture.CacheExistsAsync());
        using var attached = await client.PutAsJsonAsync("/customers/1/", new
        {
            before.FirstName,
            before.LastName,
            before.Email,
            before.CompanyId,
            BillingAddressId = billing ? id : before.BillingAddressId,
            ShippingAddressId = billing ? before.ShippingAddressId : id
        });
        Assert.Equal(HttpStatusCode.NoContent, attached.StatusCode);
        Assert.False(await fixture.CacheExistsAsync());
        var after = await db.Customers.AsNoTracking().SingleAsync();
        Assert.Equal(before.CreatedDate, after.CreatedDate);
        Assert.Equal(before.InternalRemark, after.InternalRemark);
        Assert.Equal(billing ? id : before.BillingAddressId, after.BillingAddressId);
        Assert.Equal(billing ? before.ShippingAddressId : id, after.ShippingAddressId);
        using var addressRead = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, addressRead.StatusCode);
        using var detail = await client.GetAsync("/customers/1");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var nested = detailJson.RootElement.GetProperty(billing ? "BillingAddress" : "ShippingAddress");
        Assert.Equal(id, nested.GetProperty("Id").GetInt32());
        Assert.Equal("New road", nested.GetProperty("AddressLine1").GetString());
        Assert.False(detailJson.RootElement.TryGetProperty("InternalRemark", out _));
        Assert.Equal(originalAddress.AddressLine1, (await db.Addresses.AsNoTracking().SingleAsync(row => row.Id == originalAddress.Id)).AddressLine1);
        db.Customers.Add(new Customer { FirstName = "Other", LastName = "Customer", Email = "other@example.invalid" });
        await db.SaveChangesAsync();
        var otherId = await db.Customers.Where(row => row.Email == "other@example.invalid").Select(row => row.Id).SingleAsync();
        using var wrongParent = await client.GetAsync($"/customers/{otherId}/addresses/{id}");
        Assert.Equal(HttpStatusCode.NotFound, wrongParent.StatusCode);
        Assert.DoesNotContain("New road", await wrongParent.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>Checks that invalid address-create authority preserves the existing graph and projection.</summary>
    /// <param name="authority">The anonymous, invalid-signature, expired, or insufficient-permission client scenario.</param>
    /// <param name="expected">The expected unauthorized or forbidden response for the selected actor.</param>
    /// <returns>A task that completes after asserting the authority refusal, unchanged graph, and retained cache.</returns>
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("full", HttpStatusCode.Forbidden)]
    public async Task InvalidCreateAuthority_PreservesGraphAndExistingProjection(string authority, HttpStatusCode expected)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, authority);
        await fixture.SeedOldAsync(host);
        var before = await fixture.SnapshotAsync();
        using var response = await client.PostAsJsonAsync("/customers/1/addresses/", AddressPayload());
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.True(await fixture.CacheExistsAsync());
    }

    /// <summary>Checks that null, oversized, or invalid-scalar address create input cannot change graph or cache state.</summary>
    /// <param name="body">The retained theory body identifying null or invalid input; the former blank-address case is replaced locally by an oversized address-line payload.</param>
    /// <returns>A task that completes after asserting a bad-request response, unchanged graph, and retained cache.</returns>
    [Theory]
    [InlineData("null")]
    [InlineData("{\"AddressLine1\":\" \"}")]
    [InlineData("{\"AddressLine1\":\"Valid road\",\"CountryId\":\"not-an-integer\"}")]
    public async Task InvalidCreateInput_PreservesGraphAndExistingProjection(string body)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "address-lifecycle");
        await fixture.SeedOldAsync(host);
        var before = await fixture.SnapshotAsync();
        // Retain the original theory identity while replacing its formerly invalid blank fixture.
        // Literal blanks are now covered as successful writes by the six-route literal matrix.
        var invalidBody = body == "{\"AddressLine1\":\" \"}"
            ? JsonSerializer.Serialize(new { AddressLine1 = new string('a', 257) })
            : body;
        using var response = await client.PostAsync("/customers/1/addresses/", new StringContent(invalidBody, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.True(await fixture.CacheExistsAsync());
    }

    private static object AddressPayload() => new
    {
        Id = 999,
        Building = "New building",
        AddressLine1 = "New road",
        AddressLine2 = (string?)null,
        City = "Bangkok",
        State = "Bangkok",
        PostalCode = "10100",
        CountryId = 764,
        CreatedDate = new DateTime(1900, 1, 1),
        ModifiedDate = new DateTime(1900, 1, 1)
    };
}
