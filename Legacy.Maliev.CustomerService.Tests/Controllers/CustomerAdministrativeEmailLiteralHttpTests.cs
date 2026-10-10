using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[Collection("Customer profile lifecycle")]
public sealed class CustomerAdministrativeEmailLiteralHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("keyed-create", false)]
    [InlineData("keyed-create", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    [InlineData("versioned-update", false)]
    [InlineData("versioned-update", true)]
    public async Task AdministrativeWrite_PreservesLiteralEmailAndUnrelatedGraph(string route, bool unicode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        await fixture.SeedOldAsync(host);
        var email = unicode ? " \t\u00a0literal@example.test\u00a0\t " : " literal@example.test ";
        var before = await UnselectedSnapshotAsync(route is "create" or "keyed-create" ? 2 : 1, token);
        using var old = await client.GetAsync("/customers/1/versioned", token);
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        var etag = old.Headers.ETag!.ToString();
        var key = Guid.NewGuid();
        using var request = Request(route, email, key, etag);
        using var response = await client.SendAsync(request, token);
        Assert.Equal(IsCreate(route) ? HttpStatusCode.Created : HttpStatusCode.NoContent, response.StatusCode);
        var id = 1;
        if (IsCreate(route))
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            id = body.RootElement.GetProperty("Id").GetInt32();
            Assert.Equal(2, id);
            AssertEmail(body.RootElement, email);
            Assert.EndsWith($"/customers/{id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        else Assert.False(await fixture.CacheExistsAsync());
        await AssertStoredAsync(id, email, token);
        Assert.Equal(before, await UnselectedSnapshotAsync(id, token));
        using var read = await client.GetAsync($"/customers/{id}", token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var wire = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
        AssertEmail(wire.RootElement, email);
        if (route == "keyed-create")
        {
            var after = await SnapshotAsync(token);
            using var replayRequest = Request(route, email, key, etag);
            using var replay = await client.SendAsync(replayRequest, token);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            using var body = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(token));
            Assert.Equal(id, body.RootElement.GetProperty("Id").GetInt32());
            AssertEmail(body.RootElement, email);
            Assert.Equal(after, await SnapshotAsync(token));
            using var changedRequest = Request(route, email.Trim(), key, etag);
            using var changed = await client.SendAsync(changedRequest, token);
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.Equal(after, await SnapshotAsync(token));
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("keyed-create")]
    [InlineData("update")]
    [InlineData("versioned-update")]
    public async Task PaddedEmail_UsesExistingStorageLimitAndRollsBackOverlongValue(string route)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        foreach (var length in new[] { 256, 257 })
        {
            await fixture.ResetAsync();
            await using var host = fixture.Start();
            using var client = fixture.Client(host, "customer-lifecycle");
            await fixture.SeedOldAsync(host);
            var email = " " + new string('a', length - 15) + "@example.test ";
            Assert.Equal(length, email.Length);
            var cache = host.Services.GetRequiredService<IDistributedCache>();
            var cacheBefore = await cache.GetAsync("customer:1", token);
            var before = await SnapshotAsync(token);
            using var old = await client.GetAsync("/customers/1/versioned", token);
            var key = Guid.NewGuid();
            using var request = Request(route, email, key, old.Headers.ETag!.ToString());
            using var response = await client.SendAsync(request, token);
            if (length == 256)
            {
                Assert.Equal(IsCreate(route) ? HttpStatusCode.Created : HttpStatusCode.NoContent, response.StatusCode);
                await AssertStoredAsync(IsCreate(route) ? 2 : 1, email, token);
            }
            else
            {
                Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
                var error = await response.Content.ReadAsStringAsync(token);
                Assert.DoesNotContain(email, error, StringComparison.Ordinal);
                Assert.DoesNotContain("Postgres", error, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("22001", error, StringComparison.Ordinal);
                Assert.Equal(before, await SnapshotAsync(token));
                Assert.Equal(cacheBefore, await cache.GetAsync("customer:1", token));
                await using var db = fixture.Context();
                Assert.False(await db.CustomerCreateOperations.AnyAsync(row => row.Key == key, token));
            }
        }
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    public async Task LiteralEmail_DoesNotBypassWriteAuthority(string authority, HttpStatusCode expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, authority);
        await fixture.SeedOldAsync(host);
        var before = await SnapshotAsync(token);
        foreach (var route in new[] { "create", "keyed-create", "update", "versioned-update" })
        {
            using var request = Request(route, " \tmetadata@example.test\u00a0 ", Guid.NewGuid(), "\"customer-1-1\"");
            using var response = await client.SendAsync(request, token);
            Assert.Equal(expected, response.StatusCode);
        }
        Assert.Equal(before, await SnapshotAsync(token));
        Assert.True(await fixture.CacheExistsAsync());
    }

    [Theory]
    [InlineData(" \t\u00a0 ")]
    [InlineData(" padded-without-at ")]
    public async Task InvalidEmail_RemainsRejectedWithoutGraphOrCacheMutation(string email)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        await fixture.SeedOldAsync(host);
        var before = await SnapshotAsync(token);
        using var old = await client.GetAsync("/customers/1/versioned", token);
        foreach (var route in new[] { "create", "keyed-create", "update", "versioned-update" })
        {
            using var request = Request(route, email, Guid.NewGuid(), old.Headers.ETag!.ToString());
            using var response = await client.SendAsync(request, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(before, await SnapshotAsync(token));
        Assert.True(await fixture.CacheExistsAsync());
    }

    [Fact]
    public async Task StaleVersion_RefusesLiteralEmailWithoutInvalidatingCommittedCache()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        using var old = await client.GetAsync("/customers/1/versioned", token);
        var etag = old.Headers.ETag!.ToString();
        using var firstRequest = Request("versioned-update", " winner@example.test ", Guid.NewGuid(), etag);
        using var first = await client.SendAsync(firstRequest, token);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        using var fresh = await client.GetAsync("/customers/1", token);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.True(await fixture.CacheExistsAsync());
        var before = await SnapshotAsync(token);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var cacheBefore = await cache.GetAsync("customer:1", token);
        using var staleRequest = Request("versioned-update", " stale@example.test ", Guid.NewGuid(), etag);
        using var stale = await client.SendAsync(staleRequest, token);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(before, await SnapshotAsync(token));
        Assert.Equal(cacheBefore, await cache.GetAsync("customer:1", token));
        await AssertStoredAsync(1, " winner@example.test ", token);
    }

    [Fact]
    public async Task ExistingLookup_StillNormalizesCallerAndReturnsUnchangedPlainStoredEmail()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "directory");
        var before = await SnapshotAsync(token);
        using var response = await client.GetAsync("/customers/emails/" + Uri.EscapeDataString(" DETAIL@EXAMPLE.TEST "), token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        AssertEmail(body.RootElement, "detail@example.test");
        Assert.Equal(before, await SnapshotAsync(token));
    }

    private static bool IsCreate(string route) => route is "create" or "keyed-create";

    private static HttpRequestMessage Request(string route, string email, Guid key, string etag)
    {
        var request = new HttpRequestMessage(IsCreate(route) ? HttpMethod.Post : HttpMethod.Put,
            IsCreate(route) ? "/customers/" : route == "update" ? "/customers/1/" : "/customers/1/versioned")
        {
            Content = JsonContent.Create(new
            {
                FirstName = "Before", LastName = "Customer", Email = email,
                CompanyId = 1, BillingAddressId = 1, ShippingAddressId = 1,
            }),
        };
        if (route == "keyed-create") request.Headers.Add("Idempotency-Key", key.ToString());
        if (route == "versioned-update") request.Headers.TryAddWithoutValidation("If-Match", etag);
        return request;
    }

    private static void AssertEmail(JsonElement item, string email)
    {
        Assert.Equal(email, item.GetProperty("Email").GetString());
        Assert.False(item.TryGetProperty("email", out _));
        foreach (var field in new[] { "InternalRemark", "PasswordHash", "SecurityStamp", "Token", "Revision" })
            Assert.False(item.TryGetProperty(field, out _));
    }

    private async Task AssertStoredAsync(int id, string email, CancellationToken token)
    {
        await using var db = fixture.Context();
        var stored = await db.Customers.AsNoTracking().SingleAsync(row => row.Id == id, token);
        Assert.Equal(email, stored.Email);
        Assert.Equal(1, stored.CompanyId); Assert.Equal(1, stored.BillingAddressId); Assert.Equal(1, stored.ShippingAddressId);
    }

    private async Task<string> SnapshotAsync(CancellationToken token)
    {
        await using var db = fixture.Context();
        return await db.Database.SqlQueryRaw<string>("""
            SELECT jsonb_build_object(
              'customers', (SELECT jsonb_agg(to_jsonb(c) || jsonb_build_object('xmin', c.xmin::text) ORDER BY c."ID") FROM "Customer" c),
              'companies', (SELECT jsonb_agg(to_jsonb(c) || jsonb_build_object('xmin', c.xmin::text) ORDER BY c."ID") FROM "Company" c),
              'addresses', (SELECT jsonb_agg(to_jsonb(a) || jsonb_build_object('xmin', a.xmin::text) ORDER BY a."ID") FROM "Address" a),
              'replay', (SELECT jsonb_agg(to_jsonb(r) || jsonb_build_object('xmin', r.xmin::text) ORDER BY r."Key") FROM "CustomerCreateOperation" r)
            )::text AS "Value"
            """).SingleAsync(token);
    }

    private async Task<string> UnselectedSnapshotAsync(int id, CancellationToken token)
    {
        await using var db = fixture.Context();
        return await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object(
              'customers', (SELECT jsonb_agg(to_jsonb(c) || jsonb_build_object('xmin', c.xmin::text) ORDER BY c."ID") FROM "Customer" c WHERE c."ID" <> {id}),
              'companies', (SELECT jsonb_agg(to_jsonb(c) || jsonb_build_object('xmin', c.xmin::text) ORDER BY c."ID") FROM "Company" c),
              'addresses', (SELECT jsonb_agg(to_jsonb(a) || jsonb_build_object('xmin', a.xmin::text) ORDER BY a."ID") FROM "Address" a)
            )::text AS "Value"
            """).SingleAsync(token);
    }
}
