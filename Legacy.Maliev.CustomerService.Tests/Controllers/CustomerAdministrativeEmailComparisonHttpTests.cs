using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[Collection("Customer profile lifecycle")]
public sealed class CustomerAdministrativeEmailComparisonHttpTests(CustomerDetailAuthorityFixture fixture)
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
    public async Task LiteralWriter_LookupAndProvisionSelectSameProfileWithoutMutation(string route, bool unicode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var writer = fixture.Client(host, "customer-lifecycle");
        using var reader = fixture.Client(host, "directory");
        const string core = "MiXeD.o'reilly+tag@Example.test";
        var literal = unicode ? " \t\u00a0" + core + "\u00a0\t " : " " + core + " ";
        var (id, key) = await WriteAsync(writer, route, literal, token);
        using var selected = await writer.GetAsync($"/customers/{id}", token);
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var cacheBefore = await cache.GetAsync($"customer:{id}", token);
        Assert.NotNull(cacheBefore);
        var before = await SnapshotAsync(token);
        foreach (var caller in new[] { core.ToLowerInvariant(), "\u00a0" + core.ToUpperInvariant() + "\u00a0" })
        {
            using var lookup = await reader.GetAsync("/customers/emails/" + Uri.EscapeDataString(caller), token);
            Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
            using var body = JsonDocument.Parse(await lookup.Content.ReadAsStringAsync(token));
            Assert.Equal(id, body.RootElement.GetProperty("Id").GetInt32());
            Assert.Equal(literal, body.RootElement.GetProperty("Email").GetString());
            AssertPrivate(body.RootElement);
            using var provision = await writer.PostAsJsonAsync("/customers/instant-quotation-profile", ProvisionPayload("\t" + caller + "\u00a0"), token);
            await AssertSelectedAsync(provision, id, token);
            Assert.Equal(before, await SnapshotAsync(token));
            Assert.Equal(cacheBefore, await cache.GetAsync($"customer:{id}", token));
        }
        if (route == "keyed-create")
        {
            using var request = WriteRequest(route, literal, key, null);
            using var replay = await writer.SendAsync(request, token);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            using var body = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(token));
            Assert.Equal(id, body.RootElement.GetProperty("Id").GetInt32());
            Assert.Equal(literal, body.RootElement.GetProperty("Email").GetString());
            Assert.Equal(before, await SnapshotAsync(token));
        }
    }

    [Theory]
    [InlineData("create", 2)]
    [InlineData("create", 3)]
    [InlineData("keyed-create", 2)]
    [InlineData("keyed-create", 3)]
    [InlineData("update", 2)]
    [InlineData("update", 3)]
    [InlineData("versioned-update", 2)]
    [InlineData("versioned-update", 3)]
    public async Task PaddedPlainCollision_BothConsumersRefuseWithoutGraphReplayOrCacheMutation(string route, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var writer = fixture.Client(host, "customer-lifecycle");
        using var reader = fixture.Client(host, "directory");
        const string core = "collision@example.test";
        var (id, key) = await WriteAsync(writer, route, "\t\u00a0" + core + "\u00a0\t", token);
        await using (var db = fixture.Context())
        {
            db.Customers.Add(new Customer { FirstName = "Other", LastName = "Profile", Email = core.ToUpperInvariant(), CompanyId = 1, BillingAddressId = 1, ShippingAddressId = 1 });
            if (count == 3)
                db.Customers.Add(new Customer { FirstName = "Third", LastName = "Profile", Email = " \u2003" + core + "\u2003 ", CompanyId = 1, BillingAddressId = 1, ShippingAddressId = 1 });
            await db.SaveChangesAsync(token);
        }
        using var selected = await writer.GetAsync($"/customers/{id}", token);
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var cacheBefore = await cache.GetAsync($"customer:{id}", token);
        Assert.NotNull(cacheBefore);
        var before = await SnapshotAsync(token);
        foreach (var caller in new[] { core, "\u00a0" + core.ToUpperInvariant() + "\u00a0" })
        {
            using var lookup = await reader.GetAsync("/customers/emails/" + Uri.EscapeDataString(caller), token);
            await AssertRefusalAsync(lookup, core, token);
            using var provision = await writer.PostAsJsonAsync("/customers/instant-quotation-profile", ProvisionPayload(caller), token);
            await AssertRefusalAsync(provision, core, token);
            Assert.Equal(before, await SnapshotAsync(token));
            Assert.Equal(cacheBefore, await cache.GetAsync($"customer:{id}", token));
        }
        if (route == "keyed-create")
        {
            using var request = WriteRequest(route, "\t\u00a0" + core + "\u00a0\t", key, null);
            using var replay = await writer.SendAsync(request, token);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(before, await SnapshotAsync(token));
        }
    }

    [Fact]
    public async Task ExplicitWhitespacePolicy_MatchesAllDotNetWhitespaceWithoutChangingLiteralStorage()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = timeout.Token;
        await using var host = fixture.Start();
        using var reader = fixture.Client(host, "directory");
        using var writer = fixture.Client(host, "customer-lifecycle");
        char[] whitespace = ['\u0009', '\u000a', '\u000b', '\u000c', '\u000d', '\u0020', '\u0085', '\u00a0', '\u1680',
            '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a',
            '\u2028', '\u2029', '\u202f', '\u205f', '\u3000'];
        const string core = "comparison@example.test";
        foreach (var padding in whitespace)
        {
            await fixture.ResetAsync();
            var literal = padding + core + padding;
            Assert.Equal(core, literal.Trim());
            await using (var db = fixture.Context())
            {
                var stored = await db.Customers.SingleAsync(token);
                stored.Email = literal;
                await db.SaveChangesAsync(token);
            }
            await fixture.SeedOldAsync(host);
            var before = await SnapshotAsync(token);
            var cache = host.Services.GetRequiredService<IDistributedCache>();
            var cacheBefore = await cache.GetAsync("customer:1", token);
            using var lookup = await reader.GetAsync("/customers/emails/" + core, token);
            Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
            using var body = JsonDocument.Parse(await lookup.Content.ReadAsStringAsync(token));
            Assert.Equal(literal, body.RootElement.GetProperty("Email").GetString());
            using var provision = await writer.PostAsJsonAsync("/customers/instant-quotation-profile", ProvisionPayload(literal), token);
            await AssertSelectedAsync(provision, 1, token);
            Assert.Equal(before, await SnapshotAsync(token));
            Assert.Equal(cacheBefore, await cache.GetAsync("customer:1", token));
        }
    }

    [Theory]
    [InlineData("\u200b", false)]
    [InlineData("\ufeff", false)]
    [InlineData("\u00a0", true)]
    public async Task NonWhitespaceAndInteriorWhitespace_AreNotCollapsedIntoAnotherProfile(string character, bool interior)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using (var db = fixture.Context())
        {
            var row = await db.Customers.SingleAsync(token);
            row.Email = interior ? "comparison" + character + "@example.test" : character + "comparison@example.test" + character;
            await db.SaveChangesAsync(token);
        }
        await using var host = fixture.Start();
        using var reader = fixture.Client(host, "directory");
        var before = await SnapshotAsync(token);
        using var lookup = await reader.GetAsync("/customers/emails/comparison@example.test", token);
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
        Assert.Equal(before, await SnapshotAsync(token));
    }

    [Fact]
    public async Task ConcurrentNormalizedCallers_ReusePaddedProfileWithoutCreatingGraph()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var first = fixture.Client(host, "customer-lifecycle");
        using var second = fixture.Client(host, "customer-lifecycle");
        var (id, _) = await WriteAsync(first, "update", "\t\u00a0concurrent@example.test\u00a0\t", token);
        var before = await SnapshotAsync(token);
        var responses = await Task.WhenAll(
            first.PostAsJsonAsync("/customers/instant-quotation-profile", ProvisionPayload("concurrent@example.test"), token),
            second.PostAsJsonAsync("/customers/instant-quotation-profile", ProvisionPayload("\u00a0CONCURRENT@EXAMPLE.TEST\t"), token));
        try
        {
            foreach (var response in responses) await AssertSelectedAsync(response, id, token);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
        Assert.Equal(before, await SnapshotAsync(token));
    }

    [Fact]
    public async Task AbsentNormalizedEmail_PreservesLookup404AndProvisioningCreationThenSelection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var reader = fixture.Client(host, "directory");
        using var writer = fixture.Client(host, "customer-lifecycle");
        using var missing = await reader.GetAsync("/customers/emails/absent@example.test", token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var created = await writer.PostAsJsonAsync("/customers/instant-quotation-profile", ProvisionPayload("\t\u00a0absent@example.test\u00a0\t"), token);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(token));
        var id = body.RootElement.GetProperty("CustomerId").GetInt32();
        Assert.True(body.RootElement.GetProperty("CustomerCreated").GetBoolean());
        Assert.Equal(2, id);
        await using (var db = fixture.Context())
        {
            Assert.Equal("absent@example.test", (await db.Customers.SingleAsync(row => row.Id == id, token)).Email);
            Assert.Equal(2, await db.Customers.CountAsync(token));
            Assert.Equal(2, await db.Companies.CountAsync(token));
            Assert.Equal(2, await db.Addresses.CountAsync(token));
        }
        var after = await SnapshotAsync(token);
        using var selected = await writer.PostAsJsonAsync("/customers/instant-quotation-profile", ProvisionPayload(" ABSENT@EXAMPLE.TEST "), token);
        await AssertSelectedAsync(selected, id, token);
        Assert.Equal(after, await SnapshotAsync(token));
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    public async Task InvalidAuthority_CannotReachNormalizedLookupOrProvisioning(string authority, HttpStatusCode expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var writer = fixture.Client(host, "customer-lifecycle");
        using var invalid = fixture.Client(host, authority);
        await WriteAsync(writer, "update", "\t\u00a0private@example.test\u00a0\t", token);
        await fixture.SeedOldAsync(host);
        var before = await SnapshotAsync(token);
        using var lookup = await invalid.GetAsync("/customers/emails/private@example.test", token);
        using var provision = await invalid.PostAsJsonAsync("/customers/instant-quotation-profile", ProvisionPayload("private@example.test"), token);
        Assert.Equal(expected, lookup.StatusCode);
        Assert.Equal(expected, provision.StatusCode);
        Assert.Equal(before, await SnapshotAsync(token));
        Assert.True(await fixture.CacheExistsAsync());
    }

    private async Task<(int Id, Guid Key)> WriteAsync(HttpClient writer, string route, string email, CancellationToken token)
    {
        using var old = await writer.GetAsync("/customers/1/versioned", token);
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        var key = Guid.NewGuid();
        using var request = WriteRequest(route, email, key, old.Headers.ETag!.ToString());
        using var response = await writer.SendAsync(request, token);
        var create = route is "create" or "keyed-create";
        Assert.Equal(create ? HttpStatusCode.Created : HttpStatusCode.NoContent, response.StatusCode);
        if (!create) return (1, key);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        Assert.Equal(email, body.RootElement.GetProperty("Email").GetString());
        return (body.RootElement.GetProperty("Id").GetInt32(), key);
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
        FirstName = "Ignored", LastName = "Replacement", Email = email, Company = "Ignored company", TaxNumber = "synthetic",
        Billing = new { AddressLine1 = "Ignored road", CountryId = 764 }, ShipToBillingAddress = true,
    };

    private static async Task AssertSelectedAsync(HttpResponseMessage response, int id, CancellationToken token)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        Assert.Equal(id, body.RootElement.GetProperty("CustomerId").GetInt32());
        Assert.False(body.RootElement.GetProperty("CustomerCreated").GetBoolean());
    }

    private static async Task AssertRefusalAsync(HttpResponseMessage response, string email, CancellationToken token)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(token);
        Assert.DoesNotContain(email, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ambiguous", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Postgres", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CustomerId", body, StringComparison.Ordinal);
    }

    private static void AssertPrivate(JsonElement body)
    {
        foreach (var field in new[] { "InternalRemark", "PasswordHash", "SecurityStamp", "Token", "Revision" })
            Assert.False(body.TryGetProperty(field, out _));
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
