using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[Collection("Customer profile lifecycle")]
public sealed class CustomerNameBoundaryHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Theory]
    [InlineData("create")]
    [InlineData("keyed-create")]
    [InlineData("update")]
    [InlineData("versioned-update")]
    public async Task LiteralNameBoundary_IsCheckedBeforeAnyWriteWithoutTrimming(string route)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await fixture.ResetAsync();
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "customer-lifecycle");
        await using var db = fixture.Context();
        Assert.Equal("UTF8", await db.Database.SqlQueryRaw<string>("SELECT current_setting('server_encoding') AS \"Value\"").SingleAsync(token));
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM information_schema.columns WHERE table_name = 'Customer' AND column_name IN ('FirstName', 'LastName') AND character_maximum_length = 256").SingleAsync(token));
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var cases = new (string Label, string Literal, int Scalars, bool Accepted)[]
        {
            ("Thai256", new string('ก', 256), 256, true),
            ("Thai257", new string('ก', 257), 257, false),
            ("Supplementary256", string.Concat(Enumerable.Repeat("\U0001f600", 256)), 256, false),
            ("Supplementary257", string.Concat(Enumerable.Repeat("\U0001f600", 257)), 257, false),
            ("Supplementary128", string.Concat(Enumerable.Repeat("\U0001f600", 128)), 128, true),
            ("Supplementary129", string.Concat(Enumerable.Repeat("\U0001f600", 129)), 129, false),
            ("MixedThaiSupplementary256Units", new string('ก', 254) + "\U0001f600", 255, true),
            ("MixedThaiSupplementary257Units", new string('ก', 255) + "\U0001f600", 256, false),
            ("Combining256", string.Concat(Enumerable.Repeat("ก\u0e49", 128)), 256, true),
            ("Combining257", string.Concat(Enumerable.Repeat("ก\u0e49", 128)) + "\u0e49", 257, false),
            ("Padded256", " \t\u00a0" + new string('ก', 250) + "\u00a0\t ", 256, true),
            ("Padded257", " \t\u00a0" + new string('ก', 251) + "\u00a0\t ", 257, false),
            ("TrailingSpace257", new string('ก', 256) + " ", 257, false),
            ("NulNotStorable", "Name\0", 5, false),
        };
        foreach (var field in new[] { "FirstName", "LastName" })
            foreach (var item in cases)
            {
                if (item.Label != "NulNotStorable")
                    Assert.Equal(item.Scalars, await db.Database.SqlQuery<int>($"SELECT char_length({item.Literal}) AS \"Value\"").SingleAsync(token));
                await fixture.SeedOldAsync(host);
                var before = await PhysicalSnapshotAsync(token);
                var cacheBefore = await cache.GetAsync("customer:1", token);
                Assert.NotNull(cacheBefore);
                using var versionRead = await client.GetAsync("/customers/1/versioned", token);
                Assert.Equal(HttpStatusCode.OK, versionRead.StatusCode);
                var etag = versionRead.Headers.ETag!.ToString();
                var first = field == "FirstName" ? item.Literal : "สมชาย";
                var last = field == "LastName" ? item.Literal : "ใจดี";
                var create = route is "create" or "keyed-create";
                var path = create ? "/customers/" : route == "update" ? "/customers/1/" : "/customers/1/versioned";
                var key = Guid.NewGuid();
                using var request = Request(create ? HttpMethod.Post : HttpMethod.Put, path, first, last);
                if (route == "keyed-create") request.Headers.Add("Idempotency-Key", key.ToString());
                if (route == "versioned-update") request.Headers.TryAddWithoutValidation("If-Match", etag);
                using var result = await client.SendAsync(request, token);
                Assert.True(result.StatusCode == (item.Accepted ? create ? HttpStatusCode.Created : HttpStatusCode.NoContent : HttpStatusCode.BadRequest),
                    $"{route}/{field}/{item.Label}: actual {result.StatusCode}");
                if (!item.Accepted)
                {
                    Assert.Equal(before, await PhysicalSnapshotAsync(token));
                    Assert.Equal(cacheBefore, await cache.GetAsync("customer:1", token));
                    Assert.False(await db.CustomerCreateOperations.AnyAsync(row => row.Key == key, token));
                    continue;
                }
                Assert.True(item.Literal.Length <= 256);
                var id = 1;
                if (create)
                {
                    using var body = JsonDocument.Parse(await result.Content.ReadAsStringAsync(token));
                    id = body.RootElement.GetProperty("Id").GetInt32();
                    AssertLiteral(body.RootElement, first, last);
                    Assert.Equal(cacheBefore, await cache.GetAsync("customer:1", token));
                }
                else Assert.Null(await cache.GetAsync("customer:1", token));
                var stored = await db.Customers.AsNoTracking().SingleAsync(row => row.Id == id, token);
                Assert.Equal(first, stored.FirstName); Assert.Equal(last, stored.LastName);
                Assert.Equal((first + " " + last).Trim(' '), stored.FullName);
                using var read = await client.GetAsync($"/customers/{id}", token);
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
                AssertLiteral(readJson.RootElement, first, last);
                if (route == "keyed-create")
                {
                    var after = await PhysicalSnapshotAsync(token);
                    using var replayRequest = Request(HttpMethod.Post, path, first, last);
                    replayRequest.Headers.Add("Idempotency-Key", key.ToString());
                    using var replay = await client.SendAsync(replayRequest, token);
                    Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
                    using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(token));
                    Assert.Equal(id, replayJson.RootElement.GetProperty("Id").GetInt32());
                    AssertLiteral(replayJson.RootElement, first, last);
                    Assert.Equal(after, await PhysicalSnapshotAsync(token));
                }
            }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string first, string last) => new(method, path)
    {
        Content = JsonContent.Create(new
        {
            FirstName = first,
            LastName = last,
            Email = "boundary@example.test",
            CompanyId = 1,
            BillingAddressId = 1,
            ShippingAddressId = 1,
        }),
    };

    private static void AssertLiteral(JsonElement item, string first, string last)
    {
        Assert.Equal(first, item.GetProperty("FirstName").GetString());
        Assert.Equal(last, item.GetProperty("LastName").GetString());
        Assert.Equal((first + " " + last).Trim(' '), item.GetProperty("FullName").GetString());
        Assert.False(item.TryGetProperty("firstName", out _));
        Assert.False(item.TryGetProperty("InternalRemark", out _));
    }

    private async Task<string> PhysicalSnapshotAsync(CancellationToken token)
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
