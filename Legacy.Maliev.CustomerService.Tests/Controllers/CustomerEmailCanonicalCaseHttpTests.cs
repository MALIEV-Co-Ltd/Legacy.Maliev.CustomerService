using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[Collection("Customer profile lifecycle")]
public sealed class CustomerEmailCanonicalCaseHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Theory]
    [InlineData("create", "École@EXAMPLE.TEST", "éCOLE@example.test")]
    [InlineData("keyed-create", "École@EXAMPLE.TEST", "éCOLE@example.test")]
    [InlineData("update", "École@EXAMPLE.TEST", "éCOLE@example.test")]
    [InlineData("versioned-update", "École@EXAMPLE.TEST", "éCOLE@example.test")]
    [InlineData("create", "Σ@EXAMPLE.TEST", "ς@example.test")]
    [InlineData("keyed-create", "Σ@EXAMPLE.TEST", "ς@example.test")]
    [InlineData("update", "Σ@EXAMPLE.TEST", "ς@example.test")]
    [InlineData("versioned-update", "Σ@EXAMPLE.TEST", "ς@example.test")]
    [InlineData("create", "İ@EXAMPLE.TEST", "İ@example.test")]
    [InlineData("keyed-create", "İ@EXAMPLE.TEST", "İ@example.test")]
    [InlineData("update", "İ@EXAMPLE.TEST", "İ@example.test")]
    [InlineData("versioned-update", "İ@EXAMPLE.TEST", "İ@example.test")]
    [InlineData("create", "\U00010400@EXAMPLE.TEST", "\U00010428@example.test")]
    [InlineData("keyed-create", "\U00010400@EXAMPLE.TEST", "\U00010428@example.test")]
    [InlineData("update", "\U00010400@EXAMPLE.TEST", "\U00010428@example.test")]
    [InlineData("versioned-update", "\U00010400@EXAMPLE.TEST", "\U00010428@example.test")]
    public async Task NonAsciiLiteralWriter_RoundTripsThroughBothCanonicalConsumers(string route, string supplied, string equivalent)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var writer = fixture.Client(host, "customer-lifecycle");
        using var reader = fixture.Client(host, "directory");
        var literal = "\t\u00a0" + supplied + "\u00a0\t";
        using var old = await writer.GetAsync("/customers/1/versioned", token);
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        var key = Guid.NewGuid();
        using var request = WriteRequest(route, literal, key, old.Headers.ETag!.ToString());
        using var written = await writer.SendAsync(request, token);
        var create = route is "create" or "keyed-create";
        Assert.Equal(create ? HttpStatusCode.Created : HttpStatusCode.NoContent, written.StatusCode);
        var id = create ? 2 : 1;
        using var selected = await writer.GetAsync($"/customers/{id}", token);
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var cacheBefore = await cache.GetAsync($"customer:{id}", token);
        Assert.NotNull(cacheBefore);
        var before = await SnapshotAsync(token);
        foreach (var caller in new[] { supplied, equivalent })
        {
            using var lookup = await reader.GetAsync("/customers/emails/" + Uri.EscapeDataString(caller), token);
            Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
            using var body = JsonDocument.Parse(await lookup.Content.ReadAsStringAsync(token));
            Assert.Equal(id, body.RootElement.GetProperty("Id").GetInt32());
            Assert.Equal(literal, body.RootElement.GetProperty("Email").GetString());
            Assert.False(body.RootElement.TryGetProperty("InternalRemark", out _));
            Assert.False(body.RootElement.TryGetProperty("PasswordHash", out _));
            using var provision = await writer.PostAsJsonAsync("/customers/instant-quotation-profile", ProvisionPayload("\t\u00a0" + caller + "\u00a0\t"), token);
            var result = await ReadProvisionAsync(provision, token);
            Assert.Equal(id, result.Id);
            Assert.False(result.Created);
            Assert.Equal(before, await SnapshotAsync(token));
            Assert.Equal(cacheBefore, await cache.GetAsync($"customer:{id}", token));
        }
        if (route == "keyed-create")
        {
            using var replayRequest = WriteRequest(route, literal, key, null);
            using var replay = await writer.SendAsync(replayRequest, token);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            using var body = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(token));
            Assert.Equal(id, body.RootElement.GetProperty("Id").GetInt32());
            Assert.Equal(literal, body.RootElement.GetProperty("Email").GetString());
            Assert.Equal(before, await SnapshotAsync(token));
        }
    }

    [Theory]
    [InlineData("École@example.test", "école@EXAMPLE.TEST")]
    [InlineData("Σ@example.test", "ς@EXAMPLE.TEST")]
    [InlineData("\U00010400@example.test", "\U00010428@EXAMPLE.TEST")]
    [InlineData("\u13a0@example.test", "\uab70@EXAMPLE.TEST")]
    [InlineData("ẞ@example.test", "ß@EXAMPLE.TEST")]
    public async Task NonAsciiCanonicalCollision_RefusesBothConsumersAndKeepsAllStoredBytes(string first, string second)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var writer = fixture.Client(host, "customer-lifecycle");
        using var reader = fixture.Client(host, "directory");
        using var update = WriteRequest("update", "\u00a0" + first + "\t", Guid.NewGuid(), null);
        using var response = await writer.SendAsync(update, token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using (var db = fixture.Context())
        {
            db.Customers.Add(new Customer { FirstName = "Other", LastName = "Profile", Email = "\t" + second + "\u00a0", CompanyId = 1, BillingAddressId = 1, ShippingAddressId = 1 });
            await db.SaveChangesAsync(token);
        }
        using var selected = await writer.GetAsync("/customers/1", token);
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var cacheBefore = await cache.GetAsync("customer:1", token);
        var before = await SnapshotAsync(token);
        foreach (var caller in new[] { first, second })
        {
            using var lookup = await reader.GetAsync("/customers/emails/" + Uri.EscapeDataString(caller), token);
            using var provision = await writer.PostAsJsonAsync("/customers/instant-quotation-profile", ProvisionPayload(caller), token);
            foreach (var failure in new[] { lookup, provision })
            {
                Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
                var body = await failure.Content.ReadAsStringAsync(token);
                Assert.DoesNotContain(first, body, StringComparison.Ordinal);
                Assert.DoesNotContain(second, body, StringComparison.Ordinal);
                Assert.DoesNotContain("ambiguous", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("CustomerId", body, StringComparison.Ordinal);
            }
            Assert.Equal(before, await SnapshotAsync(token));
            Assert.Equal(cacheBefore, await cache.GetAsync("customer:1", token));
        }
    }

    [Theory]
    [InlineData("İ@example.test", "i@example.test")]
    [InlineData("ı@example.test", "i@example.test")]
    [InlineData("ß@example.test", "ss@example.test")]
    [InlineData("É@example.test", "E\u0301@example.test")]
    public async Task ExcludedTurkicExpansionAndNormalizationPolicies_DoNotSelectAnotherProfile(string stored, string distinct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var writer = fixture.Client(host, "customer-lifecycle");
        using var reader = fixture.Client(host, "directory");
        using var update = WriteRequest("update", stored, Guid.NewGuid(), null);
        using var written = await writer.SendAsync(update, token);
        Assert.Equal(HttpStatusCode.NoContent, written.StatusCode);
        var before = await SnapshotAsync(token);
        using var lookup = await reader.GetAsync("/customers/emails/" + Uri.EscapeDataString(distinct), token);
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
        Assert.Equal(before, await SnapshotAsync(token));
        using var self = await reader.GetAsync("/customers/emails/" + Uri.EscapeDataString(stored), token);
        Assert.Equal(HttpStatusCode.OK, self.StatusCode);
        using var body = JsonDocument.Parse(await self.Content.ReadAsStringAsync(token));
        Assert.Equal(stored, body.RootElement.GetProperty("Email").GetString());
        Assert.Equal(before, await SnapshotAsync(token));
    }

    [Theory]
    [InlineData("Σ@EXAMPLE.TEST", "ς@example.test", "σ@example.test")]
    [InlineData("École@EXAMPLE.TEST", "éCOLE@example.test", "école@example.test")]
    public async Task ConcurrentAbsentCanonicalEquivalents_BothWaitForExactLockThenCreateOneGraph(string first, string second, string canonical)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var firstClient = fixture.Client(host, "customer-lifecycle");
        using var secondClient = fixture.Client(host, "customer-lifecycle");
        await fixture.SeedOldAsync(host);
        var before = await SnapshotAsync(token);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var cacheBefore = await cache.GetAsync("customer:1", token);
        await using var blocker = fixture.Context();
        Assert.Equal("UTF8", await blocker.Database.SqlQueryRaw<string>("SELECT current_setting('server_encoding') AS \"Value\"").SingleAsync(token));
        Assert.Equal("read committed", await blocker.Database.SqlQueryRaw<string>("SELECT current_setting('transaction_isolation') AS \"Value\"").SingleAsync(token));
        // The expected canonical key is a separate fixed Unicode oracle, not a
        // call into the repository implementation or its generated mapping table.
        await using var transaction = await blocker.Database.BeginTransactionAsync(token);
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({canonical} COLLATE \"C\", 0))", token);
        Task<HttpResponseMessage>[] requests = [];
        var released = false;
        try
        {
            requests = [
                firstClient.PostAsJsonAsync("/customers/instant-quotation-profile", ProvisionPayload("\t\u00a0" + first + "\u00a0\t"), token),
                secondClient.PostAsJsonAsync("/customers/instant-quotation-profile", ProvisionPayload(" " + second + " "), token),
            ];
            var waiting = 0;
            using var waitDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            waitDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            await using (var observer = fixture.Context())
            {
                while (waiting != 2)
                {
                    Assert.All(requests, request => Assert.False(request.IsCompleted,
                        "A provisioning request completed before the independent canonical advisory lock was released."));
                    waiting = await observer.Database.SqlQueryRaw<int>("""
                        SELECT count(*)::integer AS "Value" FROM pg_stat_activity
                        WHERE datname = current_database() AND wait_event_type = 'Lock'
                          AND wait_event = 'advisory' AND pid <> pg_backend_pid()
                        """).SingleAsync(waitDeadline.Token);
                    if (waiting != 2) await Task.Delay(50, waitDeadline.Token);
                }
            }
            Assert.Equal(2, waiting);
            Assert.Equal(before, await SnapshotAsync(token));
            Assert.Equal(cacheBefore, await cache.GetAsync("customer:1", token));
            await transaction.RollbackAsync(token);
            released = true;
            var responses = await Task.WhenAll(requests);
            var results = new List<(int Id, bool Created)>();
            foreach (var response in responses) results.Add(await ReadProvisionAsync(response, token));
            Assert.All(results, result => Assert.Equal(2, result.Id));
            Assert.Single(results, result => result.Created);
            Assert.Single(results, result => !result.Created);
            await using var db = fixture.Context();
            Assert.Equal(2, await db.Customers.CountAsync(token));
            Assert.Equal(2, await db.Companies.CountAsync(token));
            Assert.Equal(2, await db.Addresses.CountAsync(token));
            Assert.False(await db.CustomerCreateOperations.AnyAsync(token));
            var created = await db.Customers.AsNoTracking().SingleAsync(row => row.Id == 2, token);
            Assert.Contains(created.Email, new[] { first, second });
            Assert.Equal(cacheBefore, await cache.GetAsync("customer:1", token));
        }
        finally
        {
            if (!released) await transaction.RollbackAsync(CancellationToken.None);
            try { await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch
            {
                timeout.Cancel();
                try { await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { /* Exact request tokens and host/client disposal release test-owned transports. */ }
            }
            foreach (var request in requests)
                if (request.IsCompletedSuccessfully) (await request).Dispose();
            Assert.All(requests, request => Assert.True(request.IsCompleted, "Owned provisioning request did not settle during cleanup."));
        }
    }

    private static HttpRequestMessage WriteRequest(string route, string email, Guid key, string? etag)
    {
        var create = route is "create" or "keyed-create";
        var request = new HttpRequestMessage(create ? HttpMethod.Post : HttpMethod.Put,
            create ? "/customers/" : route == "update" ? "/customers/1/" : "/customers/1/versioned")
        {
            Content = JsonContent.Create(new { FirstName = "Before", LastName = "Customer", Email = email, CompanyId = 1, BillingAddressId = 1, ShippingAddressId = 1 }),
        };
        if (route == "keyed-create") request.Headers.Add("Idempotency-Key", key.ToString());
        if (route == "versioned-update") request.Headers.TryAddWithoutValidation("If-Match", etag!);
        return request;
    }

    private static object ProvisionPayload(string email) => new
    {
        FirstName = "Controlled", LastName = "Provisioned", Email = email, Company = "Controlled new company", TaxNumber = "synthetic",
        Billing = new { AddressLine1 = "Controlled new road", CountryId = 764 }, ShipToBillingAddress = true,
    };

    private static async Task<(int Id, bool Created)> ReadProvisionAsync(HttpResponseMessage response, CancellationToken token)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return (body.RootElement.GetProperty("CustomerId").GetInt32(), body.RootElement.GetProperty("CustomerCreated").GetBoolean());
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
}
