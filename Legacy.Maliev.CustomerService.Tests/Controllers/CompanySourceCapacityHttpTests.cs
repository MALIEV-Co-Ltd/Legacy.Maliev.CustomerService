using System.Net;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Data;
using Legacy.Maliev.CustomerService.Tests.OriginalInput;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Checks qualified Company capacity without claiming an original deployed SQL Server execution.</summary>
/// <param name="fixture">The existing owner of disposable customer persistence, cache, and authenticated production hosts.</param>
[Collection("Customer company lifecycle")]
public sealed class CompanySourceCapacityHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    private static readonly Guid RetainedOperation = Guid.Parse("cb6a22cb-77cb-40ec-881d-78f9323143e2");
    /// <summary>Separates literal tracked POST values from stored GET values across ordinary and protected relation writes.</summary>
    /// <param name="route">The ordinary or scoped create/update route.</param>
    /// <param name="field">The original Company field whose capacity is exercised.</param>
    /// <returns>The actual HTTP, storage, projection, cache, and stale-version assertions.</returns>
    [Theory]
    [InlineData("create", "Name")]
    [InlineData("update", "Name")]
    [InlineData("scoped-create", "Name")]
    [InlineData("scoped-update", "Name")]
    [InlineData("create", "TaxNumber")]
    [InlineData("update", "TaxNumber")]
    [InlineData("scoped-create", "TaxNumber")]
    [InlineData("scoped-update", "TaxNumber")]
    [InlineData("create", "Registrar")]
    [InlineData("update", "Registrar")]
    [InlineData("scoped-create", "Registrar")]
    [InlineData("scoped-update", "Registrar")]
    public async Task QualifiedCapacity_PreservesImmediateLiteralStoredPrefixAndExistingAuthority(string route, string field)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        await using var host = fixture.Start();
        using var client = fixture.Client(host, "relation-editor");
        client.Timeout = TimeSpan.FromSeconds(15);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        var scoped = route.StartsWith("scoped-", StringComparison.Ordinal);
        var create = route.EndsWith("create", StringComparison.Ordinal);
        foreach (var vector in Vectors())
        {
            await SeedAsync(route);
            var values = Values(field, vector.Literal);
            // The frozen original body reader establishes literal assignment, not provider storage behavior.
            var source = await CustomerOriginalInputOracle.ReadAsync("company", JsonSerializer.Serialize(values));
            Assert.Equal("success", source.Kind);
            var sourceCompany = Assert.IsType<SourceCompany>(source.Model);
            Assert.Equal(values["Name"], sourceCompany.Name);
            Assert.Equal(values["TaxNumber"], sourceCompany.TaxNumber);
            Assert.Equal(values["Registrar"], sourceCompany.Registrar);
            var before = await SnapshotAsync(token);
            var cached = await PrimeAsync(cache, token);
            var versions = scoped ? await VersionsAsync(client, create ? "new" : "1", token) : default;
            using var request = Request(route, values, versions);
            using var result = await client.SendAsync(request, token);
            var blankName = field == "Name" && string.IsNullOrWhiteSpace(vector.Literal);
            var accepted = vector.Accepted && !(scoped && blankName);
            var expected = accepted ? route == "create" ? HttpStatusCode.Created : HttpStatusCode.NoContent
                : scoped || field == "Name" ? HttpStatusCode.BadRequest : HttpStatusCode.InternalServerError;
            Assert.True(result.StatusCode == expected,
                $"{route}/{field}/{vector.Label}: expected {expected}, actual {result.StatusCode}");
            if (!accepted)
            {
                Assert.Equal(before, await SnapshotAsync(token));
                await AssertCacheAsync(cache, cached, [], token);
                continue;
            }

            var stored = Values(field, vector.Stored);
            var id = 1;
            if (scoped)
            {
                id = int.Parse(result.Headers.GetValues("X-Relation-Id").Single(), System.Globalization.CultureInfo.InvariantCulture);
                using var fresh = await client.GetAsync($"/customers/1/relations/company/{id}/edit", token);
                Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
                Assert.Equal(result.Headers.ETag, fresh.Headers.ETag);
                Assert.Equal(result.Headers.GetValues("X-Customer-ETag").Single(), fresh.Headers.GetValues("X-Customer-ETag").Single());
                using var relation = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync(token));
                AssertFields(relation.RootElement.GetProperty("Company"), stored);
            }
            else if (create)
            {
                using var immediate = JsonDocument.Parse(await result.Content.ReadAsStringAsync(token));
                id = immediate.RootElement.GetProperty("Id").GetInt32();
                AssertFields(immediate.RootElement, values);
                Assert.EndsWith($"/customers/Companies/{id}", result.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            else Assert.Empty(await result.Content.ReadAsByteArrayAsync(token));

            await using (var db = fixture.Context())
            {
                var saved = await db.Companies.AsNoTracking().SingleAsync(row => row.Id == id, token);
                Assert.Equal(stored["Name"], saved.Name);
                Assert.Equal(stored["TaxNumber"], saved.TaxNumber);
                Assert.Equal(stored["Registrar"], saved.Registrar);
                Assert.True(saved.ModifiedDate > new DateTime(2020, 1, 1));
                if (!create) Assert.Equal(new DateTime(2020, 1, 1), saved.CreatedDate);
            }
            AssertPreservedGraph(before, await SnapshotAsync(token), id, scoped, create);
            await AssertCacheAsync(cache, cached, create ? scoped ? [1] : [] : [1, 2], token);
            using var read = await client.GetAsync($"/customers/companies/{id}", token);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
            AssertFields(body.RootElement, stored);
            foreach (var customerId in scoped && create ? new[] { 1 } : !create ? new[] { 1, 2 } : Array.Empty<int>())
            {
                using var customer = await client.GetAsync($"/customers/{customerId}", token);
                Assert.Equal(HttpStatusCode.OK, customer.StatusCode);
                using var projection = JsonDocument.Parse(await customer.Content.ReadAsStringAsync(token));
                AssertFields(projection.RootElement.GetProperty("Company"), stored);
                Assert.False(projection.RootElement.TryGetProperty("InternalRemark", out _));
            }
            if (scoped)
            {
                var committed = await SnapshotAsync(token);
                var replayCache = await PrimeAsync(cache, token);
                using var staleRequest = Request(route, values, versions);
                using var stale = await client.SendAsync(staleRequest, token);
                Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
                Assert.Equal(committed, await SnapshotAsync(token));
                await AssertCacheAsync(cache, replayCache, [], token);
            }
        }
    }

    /// <summary>Requires physical enforcement for writers that do not pass through HTTP or new DTO validation.</summary>
    /// <param name="field">The Company column receiving a typed text parameter.</param>
    /// <param name="update">Whether the physical writer updates an existing row rather than inserting a new one.</param>
    /// <returns>The actual stored-prefix and precise constraint-refusal assertions.</returns>
    [Theory]
    [InlineData("Name", false)]
    [InlineData("Name", true)]
    [InlineData("TaxNumber", false)]
    [InlineData("TaxNumber", true)]
    [InlineData("Registrar", false)]
    [InlineData("Registrar", true)]
    public async Task EveryWriter_StoresOnlyQualifiedUtf16PrefixAndRefusesOtherOverflow(string field, bool update)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        await SeedAsync("update");
        await using var db = fixture.Context();
        var prefix = string.Concat(Enumerable.Repeat("😀", 128));
        var literal = prefix + " ";
        var insertId = 49;
        await WriteAsync(literal);
        var id = update ? 1 : 50;
        var saved = await db.Companies.AsNoTracking().SingleAsync(row => row.Id == id, token);
        Assert.Equal(prefix, Field(saved));
        var before = await SnapshotAsync(token);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => WriteAsync(string.Concat(Enumerable.Repeat("😀", 129))));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal($"CK_Company_{field}_SourceLength", failure.ConstraintName);
        Assert.Equal(before, await SnapshotAsync(token));

        Task WriteAsync(string value)
        {
            var column = field switch { "Name" => "Name", "TaxNumber" => "TaxNumber", "Registrar" => "Registrar", _ => throw new ArgumentOutOfRangeException(nameof(field)) };
            var targetId = update ? 1 : ++insertId;
            var sql = update ? $"UPDATE \"Company\" SET \"{column}\"=@value WHERE \"ID\"=1"
                : column == "Name" ? $"INSERT INTO \"Company\" (\"ID\",\"Name\") VALUES ({targetId},@value)"
                : $"INSERT INTO \"Company\" (\"ID\",\"Name\",\"{column}\") VALUES ({targetId},'Other field',@value)";
            return db.Database.ExecuteSqlRawAsync(sql, [new NpgsqlParameter("value", NpgsqlDbType.Text) { Value = value, Size = -1 }], token);
        }
        string? Field(Company company) => field switch { "Name" => company.Name, "TaxNumber" => company.TaxNumber, "Registrar" => company.Registrar, _ => throw new ArgumentOutOfRangeException(nameof(field)) };
    }

    /// <summary>Prevents generated clients from unconditionally refusing qualified overflow accepted by the actual route.</summary>
    /// <returns>The served request-schema assertions.</returns>
    [Fact]
    public async Task ServedRequestSchemas_DoNotDeclareAnUnconditional256CharacterLimit()
    {
        await using var host = fixture.Start().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = fixture.Client(host, "relation-editor");
        using var response = await client.GetAsync("/customer/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var type in new[] { "UpsertCompanyRequest", "CustomerCompanyRelationRequest" })
        {
            var properties = schemas.GetProperty(type).GetProperty("properties");
            foreach (var field in new[] { "Name", "TaxNumber", "Registrar" })
            {
                var property = properties.GetProperty(field);
                Assert.False(property.TryGetProperty("maxLength", out _));
                Assert.False(properties.TryGetProperty(char.ToLowerInvariant(field[0]) + field[1..], out _));
            }
        }
    }

    private sealed record Vector(string Label, string Literal, string Stored, bool Accepted = true);

    private static IEnumerable<Vector> Vectors()
    {
        var ascii = new string('A', 256);
        var supplementary = string.Concat(Enumerable.Repeat("😀", 128));
        var mixed = "ก" + string.Concat(Enumerable.Repeat("ก😀", 85));
        yield return new("in-bound-padding", " \tบริษัท\u00a0 ", " \tบริษัท\u00a0 ");
        yield return new("ascii256", ascii, ascii);
        yield return new("ascii257-space", ascii + " ", ascii);
        yield return new("supplementary257-space", supplementary + " ", supplementary);
        yield return new("mixed-overflow-spaces", mixed + "   ", mixed);
        yield return new("all-spaces", new string(' ', 257), new string(' ', 256));
        yield return new("ascii257-nonspace", ascii + "A", "", false);
        yield return new("ascii257-nbsp", ascii + "\u00a0", "", false);
        yield return new("ascii257-tab", ascii + "\t", "", false);
        yield return new("supplementary258-nonspace", string.Concat(Enumerable.Repeat("😀", 129)), "", false);
        yield return new("scalar-crosses-capacity", new string('A', 255) + "😀 ", "", false);
    }

    private static Dictionary<string, string?> Values(string field, string? value) => new(StringComparer.Ordinal)
    {
        ["Name"] = field == "Name" ? value : "  Synthetic company  ",
        ["TaxNumber"] = field == "TaxNumber" ? value : " 0100000000000 ",
        ["Registrar"] = field == "Registrar" ? value : "\tSynthetic registrar\t",
    };

    private static HttpRequestMessage Request(string route, Dictionary<string, string?> values, (string? Metadata, string? Customer) versions)
    {
        var path = route switch
        {
            "create" => "/customers/companies/",
            "update" => "/customers/companies/1/",
            "scoped-create" => "/customers/1/relations/company/new/versioned",
            "scoped-update" => "/customers/1/relations/company/1/versioned",
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };
        var request = new HttpRequestMessage(route == "create" ? HttpMethod.Post : HttpMethod.Put, path)
        { Content = new StringContent(JsonSerializer.Serialize(values), Encoding.UTF8, "application/json") };
        if (versions.Metadata is not null) request.Headers.TryAddWithoutValidation("If-Match", versions.Metadata);
        if (versions.Customer is not null) request.Headers.TryAddWithoutValidation("X-Customer-If-Match", versions.Customer);
        return request;
    }

    private static async Task<(string? Metadata, string? Customer)> VersionsAsync(HttpClient client, string binding, CancellationToken token)
    {
        using var read = await client.GetAsync($"/customers/1/relations/company/{binding}" + (binding == "new" ? "" : "/edit"), token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        return (read.Headers.ETag!.ToString(), read.Headers.GetValues("X-Customer-ETag").Single());
    }

    private static void AssertFields(JsonElement item, Dictionary<string, string?> expected)
    {
        foreach (var field in expected) Assert.Equal(field.Value, item.GetProperty(field.Key).GetString());
        foreach (var key in new[] { "name", "taxNumber", "registrar", "InternalRemark", "PasswordHash", "SecurityStamp", "Token" })
            Assert.False(item.TryGetProperty(key, out _));
    }

    private static void AssertPreservedGraph(string before, string after, int changedCompany, bool scoped, bool create)
    {
        using var old = JsonDocument.Parse(before);
        using var current = JsonDocument.Parse(after);
        foreach (var name in new[] { "addresses", "replay", "completionReplay" })
            Assert.Equal(old.RootElement.GetProperty(name).GetRawText(), current.RootElement.GetProperty(name).GetRawText());
        var companies = current.RootElement.GetProperty("companies").EnumerateArray().ToArray();
        Assert.Equal(create ? 3 : 2, companies.Length);
        foreach (var row in old.RootElement.GetProperty("companies").EnumerateArray())
            if (row.GetProperty("ID").GetInt32() != changedCompany)
                Assert.Equal(row.GetRawText(), companies.Single(value => value.GetProperty("ID").GetInt32() == row.GetProperty("ID").GetInt32()).GetRawText());
        var customers = current.RootElement.GetProperty("customers").EnumerateArray().ToArray();
        Assert.Equal(3, customers.Length);
        foreach (var row in old.RootElement.GetProperty("customers").EnumerateArray())
        {
            var id = row.GetProperty("ID").GetInt32();
            var saved = customers.Single(value => value.GetProperty("ID").GetInt32() == id);
            if (!scoped || id != 1) Assert.Equal(row.GetRawText(), saved.GetRawText());
            else
            {
                foreach (var property in row.EnumerateObject())
                    if (property.Name is not ("CompanyID" or "ModifiedDate" or "xmin"))
                        Assert.Equal(property.Value.GetRawText(), saved.GetProperty(property.Name).GetRawText());
                Assert.Equal(changedCompany, saved.GetProperty("CompanyID").GetInt32());
                Assert.NotEqual(row.GetProperty("xmin").GetString(), saved.GetProperty("xmin").GetString());
            }
        }
    }

    private static async Task<Dictionary<int, byte[]>> PrimeAsync(IDistributedCache cache, CancellationToken token)
    {
        var entries = new Dictionary<int, byte[]>();
        foreach (var id in new[] { 1, 2, 3 })
        {
            var bytes = Encoding.UTF8.GetBytes($"{{\"id\":{id},\"firstName\":\"Cached\",\"lastName\":\"Synthetic\",\"email\":\"cached-{id}@example.invalid\"}}");
            await cache.SetAsync($"customer:{id}", bytes, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }, token);
            entries.Add(id, bytes);
        }
        return entries;
    }

    private static async Task AssertCacheAsync(IDistributedCache cache, Dictionary<int, byte[]> before, int[] evicted, CancellationToken token)
    {
        foreach (var pair in before)
            if (evicted.Contains(pair.Key)) Assert.Null(await cache.GetAsync($"customer:{pair.Key}", token));
            else Assert.Equal(pair.Value, await cache.GetAsync($"customer:{pair.Key}", token));
    }

    private async Task SeedAsync(string route)
    {
        await fixture.ResetAsync();
        await using var db = fixture.Context();
        await db.QuotationProfileCompletionOperations.Where(value => value.Key == RetainedOperation).ExecuteDeleteAsync();
        db.CustomerCreateOperations.Add(new CustomerCreateOperation
        {
            Key = RetainedOperation,
            ActorHash = new string('a', 64),
            RequestHash = new string('b', 64),
            CustomerId = 1,
            ResponseJson = "{\"Id\":1,\"Name\":\"retained synthetic receipt\"}",
            CreatedAt = DateTime.UtcNow,
        });
        db.QuotationProfileCompletionOperations.Add(new QuotationProfileCompletionOperation
        {
            CustomerId = 1,
            Key = RetainedOperation,
            ActorHash = new string('a', 64),
            RequestHash = new string('b', 64),
            CompletionId = RetainedOperation,
            Changed = true,
            CreatedAt = DateTime.UtcNow,
        });
        var original = await db.Companies.SingleAsync(row => row.Id == 1);
        original.CreatedDate = new DateTime(2020, 1, 1);
        db.Customers.Add(new Customer { FirstName = "Shared", LastName = "Synthetic", Email = "shared@example.invalid", CompanyId = 1, ShippingAddressId = 1 });
        await db.SaveChangesAsync();
        db.Customers.Add(new Customer { FirstName = "Unrelated", LastName = "Synthetic", Email = "unrelated@example.invalid", Company = new Company { Name = "Unrelated company" } });
        if (route == "scoped-create") (await db.Customers.SingleAsync(row => row.Id == 1)).CompanyId = null;
        await db.SaveChangesAsync();
    }

    private async Task<string> SnapshotAsync(CancellationToken token)
    {
        await using var db = fixture.Context();
        return await db.Database.SqlQueryRaw<string>("""
            SELECT jsonb_build_object(
              'customers', (SELECT jsonb_agg(to_jsonb(c) || jsonb_build_object('xmin', c.xmin::text) ORDER BY c."ID") FROM "Customer" c),
              'companies', (SELECT jsonb_agg(to_jsonb(c) || jsonb_build_object('xmin', c.xmin::text) ORDER BY c."ID") FROM "Company" c),
              'addresses', (SELECT jsonb_agg(to_jsonb(a) || jsonb_build_object('xmin', a.xmin::text) ORDER BY a."ID") FROM "Address" a),
              'replay', (SELECT jsonb_agg(to_jsonb(r) || jsonb_build_object('xmin', r.xmin::text) ORDER BY r."Key") FROM "CustomerCreateOperation" r),
              'completionReplay', (SELECT jsonb_agg(to_jsonb(r) || jsonb_build_object('xmin', r.xmin::text) ORDER BY r."CustomerId",r."Key") FROM "QuotationProfileCompletionOperation" r)
            )::text AS "Value"
            """).SingleAsync(token);
    }
}
