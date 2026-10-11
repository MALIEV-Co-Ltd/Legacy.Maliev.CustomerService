using System.Net;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[Collection("Customer company lifecycle")]
public sealed class CustomerCompanyPrimitiveNameHttpTests(CustomerDetailAuthorityFixture fixture, ITestOutputHelper output)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Fact]
    public Task PrimitiveNameMembers_RefuseWithoutChangingPhysicalGraphOrCache() =>
        RefuseMatrixAsync([
            ("BooleanTrue", "{\"Name\":true}"),
            ("BooleanFalse", "{\"Name\":false}"),
            ("Integer", "{\"Name\":42}"),
            ("Decimal", "{\"Name\":1.5}"),
            ("Exponent", "{\"Name\":1e3}"),
            ("NullMember", "{\"Name\":null}"),
            ("MissingMember", "{}"),
            ("ObjectMember", "{\"Name\":{\"privateMarker\":\"synthetic-private-member\"}}"),
            ("ArrayMember", "{\"Name\":[\"synthetic-private-member\"]}"),
        ]);

    [Fact]
    public Task WholeBodyPrimitive_RefusesWithoutChangingPhysicalGraphOrCache() =>
        RefuseMatrixAsync([
            ("BooleanBody", "true"),
            ("FalseBody", "false"),
            ("IntegerBody", "42"),
            ("DecimalBody", "1.5"),
            ("StringBody", "\"synthetic-private-member\""),
            ("NullBody", "null"),
            ("ArrayBody", "[\"synthetic-private-member\"]"),
        ]);

    private async Task RefuseMatrixAsync((string Label, string Json)[] cases)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var route in new[] { "create", "update", "scoped-create", "scoped-update" })
        {
            await SeedAsync(route);
            var scoped = route.StartsWith("scoped-", StringComparison.Ordinal);
            var create = route.EndsWith("create", StringComparison.Ordinal);
            string? metadata = null;
            string? customer = null;
            if (scoped)
            {
                using var read = await client.GetAsync("/customers/1/relations/company/" + (create ? "new" : "1/edit"), token);
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                metadata = read.Headers.ETag!.ToString();
                customer = read.Headers.GetValues("X-Customer-ETag").Single();
            }

            var before = await PhysicalSnapshotAsync(token);
            var cached = new Dictionary<int, byte[]>();
            foreach (var id in new[] { 1, 2, 3 })
            {
                // Independently authored old-writer DTO bytes are never derived from a production read.
                var bytes = Encoding.UTF8.GetBytes($"{{\"id\":{id},\"firstName\":\"Cached\",\"lastName\":\"Synthetic\",\"email\":\"cached-{id}@example.invalid\"}}");
                await cache.SetAsync($"customer:{id}", bytes,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }, token);
                cached.Add(id, bytes);
            }

            foreach (var item in cases)
            {
                var path = route switch
                {
                    "create" => "/customers/companies/",
                    "update" => "/customers/companies/1/",
                    "scoped-create" => "/customers/1/relations/company/new/versioned",
                    _ => "/customers/1/relations/company/1/versioned",
                };
                using var request = new HttpRequestMessage(route == "create" ? HttpMethod.Post : HttpMethod.Put, path)
                {
                    Content = new StringContent(item.Json, Encoding.UTF8, "application/json"),
                };
                if (metadata is not null) request.Headers.TryAddWithoutValidation("If-Match", metadata);
                if (customer is not null) request.Headers.TryAddWithoutValidation("X-Customer-If-Match", customer);
                using var response = await client.SendAsync(request, token);
                Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
                    $"{route}/{item.Label}: expected400, actual{(int)response.StatusCode}");
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
                var raw = await response.Content.ReadAsStringAsync(token);
                using var document = JsonDocument.Parse(raw);
                var problem = document.RootElement;
                Assert.Equal(400, problem.GetProperty("status").GetInt32());
                Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("type").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
                var hasErrors = problem.TryGetProperty("errors", out var errors);
                if (hasErrors)
                {
                    Assert.Equal(JsonValueKind.Object, errors.ValueKind);
                    Assert.NotEmpty(errors.EnumerateObject());
                    foreach (var error in errors.EnumerateObject())
                    {
                        Assert.Equal(JsonValueKind.Array, error.Value.ValueKind);
                        Assert.NotEmpty(error.Value.EnumerateArray());
                        foreach (var message in error.Value.EnumerateArray())
                            Assert.False(string.IsNullOrWhiteSpace(message.GetString()));
                    }
                }
                else Assert.True(!scoped && item.Label is "NullMember" or "MissingMember");

                foreach (var privateValue in new[] { "synthetic-private-member", "staff-only", "Before company", "PasswordHash", "SecurityStamp", "ConnectionStrings", "Npgsql", "SELECT " })
                    Assert.DoesNotContain(privateValue, raw, StringComparison.OrdinalIgnoreCase);
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    Route = route,
                    Case = item.Label,
                    Status = (int)response.StatusCode,
                    ContentType = response.Content.Headers.ContentType.MediaType,
                    ProblemType = problem.GetProperty("type").GetString(),
                    ProblemTitle = problem.GetProperty("title").GetString(),
                    Errors = hasErrors ? errors.GetRawText() : null,
                }));
                Assert.Equal(before, await PhysicalSnapshotAsync(token));
                foreach (var pair in cached)
                    Assert.Equal(pair.Value, await cache.GetAsync($"customer:{pair.Key}", token));
            }
        }
    }

    private async Task SeedAsync(string route)
    {
        await fixture.ResetAsync();
        await using var db = fixture.Context();
        db.Customers.Add(new Customer { FirstName = "Shared", LastName = "Synthetic", Email = "shared@example.invalid", CompanyId = 1, ShippingAddressId = 1 });
        await db.SaveChangesAsync();
        db.Customers.Add(new Customer { FirstName = "Unrelated", LastName = "Synthetic", Email = "unrelated@example.invalid", Company = new Company { Name = "Unrelated company" } });
        if (route == "scoped-create") (await db.Customers.SingleAsync(row => row.Id == 1)).CompanyId = null;
        await db.SaveChangesAsync();
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
