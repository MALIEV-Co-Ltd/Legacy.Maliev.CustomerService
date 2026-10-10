using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[Collection("Customer profile lifecycle")]
public sealed class CustomerNameLiteralHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    private const string First = " \tสมชาย\u00a0 ";
    private const string Last = "\u00a0 ใจดี\t ";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_PreservesLiteralNamesInStorageWireAndKeyedReplay(bool keyed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        var before = await fixture.SnapshotAsync();
        var key = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/customers/")
        {
            Content = JsonContent.Create(Payload(First, Last)),
        };
        if (keyed) request.Headers.Add("Idempotency-Key", key.ToString());
        using var created = await client.SendAsync(request, token);
        Assert.True(created.StatusCode == HttpStatusCode.Created,
            $"keyed={keyed}: actual {created.StatusCode}; body {await created.Content.ReadAsStringAsync(token)}");
        using var wire = JsonDocument.Parse(await created.Content.ReadAsStringAsync(token));
        AssertNames(wire.RootElement, First, Last);
        var id = wire.RootElement.GetProperty("Id").GetInt32();
        Assert.True(id > 1);
        Assert.EndsWith($"/customers/{id}", created.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        await AssertStoredAsync(id, First, Last, token);
        using var read = await client.GetAsync(created.Headers.Location, token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
        AssertNames(readJson.RootElement, First, Last);
        if (keyed)
        {
            using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/customers/")
            {
                Content = JsonContent.Create(Payload(First, Last)),
            };
            replayRequest.Headers.Add("Idempotency-Key", key.ToString());
            using var replay = await client.SendAsync(replayRequest, token);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(token));
            AssertNames(replayJson.RootElement, First, Last);
            Assert.Equal(id, replayJson.RootElement.GetProperty("Id").GetInt32());
            await using var check = fixture.Context();
            Assert.Equal(2, await check.Customers.CountAsync(token));
            Assert.Equal(1, await check.CustomerCreateOperations.CountAsync(token));
        }
        using var deleted = await client.DeleteAsync($"/customers/{id}", token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(before, await fixture.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_PreservesLiteralNamesAndInvalidatesOldCacheWithoutChangingRelations(bool versioned)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host);
        await fixture.SeedOldAsync(host);
        var relations = await RelationsAsync(token);
        await using var db = fixture.Context();
        var original = await db.Customers.AsNoTracking().SingleAsync(token);
        var oldRevision = await db.Customers.Select(row => EF.Property<uint>(row, "xmin")).SingleAsync(token);
        using var read = await client.GetAsync("/customers/1/versioned", token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var etag = read.Headers.ETag!.ToString();
        using var request = new HttpRequestMessage(HttpMethod.Put, versioned ? "/customers/1/versioned" : "/customers/1/")
        {
            Content = JsonContent.Create(Payload(First, Last)),
        };
        if (versioned) request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var updated = await client.SendAsync(request, token);
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        Assert.False(await fixture.CacheExistsAsync());
        await AssertStoredAsync(1, First, Last, token);
        var stored = await db.Customers.AsNoTracking().SingleAsync(token);
        Assert.Equal(original.CreatedDate, stored.CreatedDate);
        Assert.Equal(original.InternalRemark, stored.InternalRemark);
        Assert.NotNull(stored.ModifiedDate);
        if (original.ModifiedDate.HasValue) Assert.True(stored.ModifiedDate >= original.ModifiedDate);
        Assert.NotEqual(oldRevision, await db.Customers.Select(row => EF.Property<uint>(row, "xmin")).SingleAsync(token));
        Assert.Equal(relations, await RelationsAsync(token));
        using var afterRead = await client.GetAsync("/customers/1", token);
        Assert.Equal(HttpStatusCode.OK, afterRead.StatusCode);
        using var afterJson = JsonDocument.Parse(await afterRead.Content.ReadAsStringAsync(token));
        AssertNames(afterJson.RootElement, First, Last);
        if (versioned)
        {
            var snapshot = await fixture.SnapshotAsync();
            using var staleRequest = new HttpRequestMessage(HttpMethod.Put, "/customers/1/versioned")
            {
                Content = JsonContent.Create(Payload("Stale", "Writer")),
            };
            staleRequest.Headers.TryAddWithoutValidation("If-Match", etag);
            using var stale = await client.SendAsync(staleRequest, token);
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            Assert.Equal(snapshot, await fixture.SnapshotAsync());
            await AssertStoredAsync(1, First, Last, token);
        }
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    public async Task PaddedNames_DoNotBypassAuthority(string authority, HttpStatusCode status)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, authority);
        client.Timeout = TimeSpan.FromSeconds(90);
        await fixture.SeedOldAsync(host);
        var before = await fixture.SnapshotAsync();
        using var created = await client.PostAsJsonAsync("/customers/", Payload(First, Last));
        using var updated = await client.PutAsJsonAsync("/customers/1", Payload(First, Last));
        using var versioned = await client.PutAsJsonAsync("/customers/1/versioned", Payload(First, Last));
        foreach (var response in new[] { created, updated, versioned }) Assert.Equal(status, response.StatusCode);
        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.True(await fixture.CacheExistsAsync());
    }

    [Theory]
    [InlineData(" \t\u00a0", "Valid")]
    [InlineData("Valid", " \t\u00a0")]
    public async Task WhitespaceOnlyNames_RemainRejected(string first, string last)
    {
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        client.Timeout = TimeSpan.FromSeconds(90);
        var before = await fixture.SnapshotAsync();
        using var created = await client.PostAsJsonAsync("/customers/", Payload(first, last));
        using var updated = await client.PutAsJsonAsync("/customers/1", Payload(first, last));
        using var versioned = await client.PutAsJsonAsync("/customers/1/versioned", Payload(first, last));
        foreach (var response in new[] { created, updated, versioned }) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await fixture.SnapshotAsync());
    }

    private static object Payload(string first, string last) => new
    {
        FirstName = first,
        LastName = last,
        Email = "literal@example.test",
        Telephone = "020000001",
        Mobile = "0800000001",
        Fax = "020000002",
        DateOfBirth = new DateTime(1980, 1, 1),
        CompanyId = 1,
        BillingAddressId = 1,
        ShippingAddressId = 1,
    };

    private static void AssertNames(JsonElement item, string first, string last)
    {
        Assert.Equal(first, item.GetProperty("FirstName").GetString());
        Assert.Equal(last, item.GetProperty("LastName").GetString());
        Assert.Equal((first + " " + last).Trim(' '), item.GetProperty("FullName").GetString());
        Assert.False(item.TryGetProperty("firstName", out _));
        Assert.False(item.TryGetProperty("InternalRemark", out _));
        foreach (var field in new[] { "PasswordHash", "SecurityStamp", "Revision", "Token" })
            Assert.False(item.TryGetProperty(field, out _));
    }

    private async Task AssertStoredAsync(int id, string first, string last, CancellationToken token)
    {
        await using var db = fixture.Context();
        var stored = await db.Customers.AsNoTracking().SingleAsync(row => row.Id == id, token);
        Assert.Equal(first, stored.FirstName); Assert.Equal(last, stored.LastName);
        Assert.Equal((first + " " + last).Trim(' '), stored.FullName);
        Assert.Equal("literal@example.test", stored.Email);
        Assert.Equal(1, stored.CompanyId); Assert.Equal(1, stored.BillingAddressId); Assert.Equal(1, stored.ShippingAddressId);
        Assert.Equal("020000001", stored.Telephone); Assert.Equal("0800000001", stored.Mobile); Assert.Equal("020000002", stored.Fax);
        Assert.Equal(new DateTime(1980, 1, 1), stored.DateOfBirth);
    }

    private async Task<string> RelationsAsync(CancellationToken token)
    {
        await using var db = fixture.Context();
        return JsonSerializer.Serialize(new
        {
            Companies = await db.Companies.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Addresses = await db.Addresses.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
        });
    }
}
