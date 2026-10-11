using System.Text.Json;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit.Abstractions;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

[Collection("Customer company lifecycle")]
public sealed class CompanyTaxRegistrarPrimitiveStorageTests(CustomerDetailAuthorityFixture fixture, ITestOutputHelper output)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    [Fact]
    public async Task PairedFields_SeparateCurrentDeserializeControllerAndDisposablePostgreSqlStorageOutcomes()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        await using var host = fixture.Start();
        var options = host.Services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        foreach (var field in new[] { "TaxNumber", "Registrar" })
            foreach (var item in Cases(field))
            {
                await fixture.ResetAsync();
                await fixture.SeedOldAsync(host);
                var before = await SnapshotAsync(token);
                var cached = await cache.GetAsync("customer:1", token);
                Assert.NotNull(cached);
                UpsertCompanyRequest? request = null;
                string? readError = null;
                try { request = JsonSerializer.Deserialize<UpsertCompanyRequest>("{\"Name\":\"ok\"," + item.Members + "}", options); }
                catch (JsonException error) { readError = error.GetType().FullName; }
                if (item.ReadRejects)
                {
                    Assert.Equal(typeof(JsonException).FullName, readError);
                    Assert.Null(request);
                    Assert.Equal(before, await SnapshotAsync(token));
                    Assert.Equal(cached, await cache.GetAsync("customer:1", token));
                    output.WriteLine(JsonSerializer.Serialize(new
                    {
                        Kind = "storage-stage",
                        Field = field,
                        Case = item.Label,
                        Deserialize = readError,
                        Controller = "NOT RUN",
                        PostgreSqlStorage = "NOT RUN",
                        GraphCacheUnchanged = true,
                    }));
                    continue;
                }
                Assert.Null(readError);
                var value = Assert.IsType<UpsertCompanyRequest>(request);
                Assert.True(CompanyTaxRegistrarPrimitiveOracleTests.Details(value));
                Assert.Equal(item.Expected, field == "TaxNumber" ? value.TaxNumber : value.Registrar);
                string? storageError = null;
                string? sqlState = null;
                var id = 0;
                await using (var db = fixture.Context())
                {
                    var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
                    var company = new Company
                    {
                        Name = value.Name,
                        TaxNumber = value.TaxNumber,
                        Registrar = value.Registrar,
                        CreatedDate = now,
                        ModifiedDate = now,
                    };
                    db.Companies.Add(company);
                    try { await db.SaveChangesAsync(token); id = company.Id; }
                    catch (DbUpdateException error)
                    {
                        storageError = error.GetType().FullName;
                        sqlState = Assert.IsType<PostgresException>(error.InnerException).SqlState;
                    }
                    catch (ArgumentException error) when (item.Label == "literal-nul")
                    {
                        // A client encoding refusal is a different stage from a PostgreSQL SQLSTATE.
                        storageError = error.GetType().FullName;
                    }
                }
                if (item.StorageRejects)
                {
                    if (storageError == typeof(ArgumentException).FullName)
                    {
                        Assert.Equal("literal-nul", item.Label);
                        Assert.Null(sqlState);
                    }
                    else
                    {
                        Assert.Equal(typeof(DbUpdateException).FullName, storageError);
                        Assert.Equal(item.Label == "literal-nul" ? "22021" : "22001", sqlState);
                    }
                    Assert.Equal(before, await SnapshotAsync(token));
                }
                else
                {
                    Assert.Null(storageError);
                    Assert.True(id > 1);
                    await using var db = fixture.Context();
                    var stored = await db.Companies.AsNoTracking().SingleAsync(company => company.Id == id, token);
                    Assert.Equal(value.Name, stored.Name);
                    Assert.Equal(value.TaxNumber, stored.TaxNumber);
                    Assert.Equal(value.Registrar, stored.Registrar);
                    using var original = JsonDocument.Parse(before);
                    using var after = JsonDocument.Parse(await SnapshotAsync(token));
                    foreach (var table in new[] { "customers", "addresses", "replay" })
                        Assert.Equal(original.RootElement.GetProperty(table).GetRawText(), after.RootElement.GetProperty(table).GetRawText());
                    var companies = after.RootElement.GetProperty("companies").EnumerateArray().ToArray();
                    Assert.Equal(original.RootElement.GetProperty("companies").GetArrayLength() + 1, companies.Length);
                    foreach (var previous in original.RootElement.GetProperty("companies").EnumerateArray())
                        Assert.Equal(previous.GetRawText(), companies.Single(company => company.GetProperty("ID").GetInt32() == previous.GetProperty("ID").GetInt32()).GetRawText());
                }
                Assert.Equal(cached, await cache.GetAsync("customer:1", token));
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    Kind = "storage-stage",
                    Field = field,
                    Case = item.Label,
                    Deserialize = "Succeeded",
                    Controller = "Existing HasCompanyDetails returned true; no HTTP action invoked",
                    PostgreSqlStorage = storageError ?? "Direct disposable context insert/readback succeeded",
                    SqlState = sqlState,
                    ExistingGraphCachePreserved = true,
                    HistoricalSqlServer = "UNKNOWN",
                }));
            }
    }

    private static IEnumerable<StorageCase> Cases(string field)
    {
        yield return new("null-only", $"\"{field}\":null", null);
        foreach (var length in new[] { 255, 256, 257 })
            yield return new("literal-length/" + length, $"\"{field}\":" + JsonSerializer.Serialize(new string('x', length)), new string('x', length), StorageRejects: length == 257);
        yield return new("literal-nul", $"\"{field}\":\"\\u0000\"", "\0", StorageRejects: true);
        var emoji = string.Concat(Enumerable.Repeat("😀", 129));
        yield return new("literal-129-pairs", $"\"{field}\":" + JsonSerializer.Serialize(emoji), emoji);
        yield return new("long-literal-bounded-null", $"\"{field}\":" + JsonSerializer.Serialize(new string('x', 257)) + $",\"{field}\":\"bounded\",\"{field}\":null", "bounded");
        yield return new("integer-token", $"\"{field}\":42", null, ReadRejects: true);
        yield return new("long-numeric-bounded-null", $"\"{field}\":" + new string('1', 257) + $",\"{field}\":\"bounded\",\"{field}\":null", null, ReadRejects: true);
    }

    private async Task<string> SnapshotAsync(CancellationToken token)
    {
        await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync(token);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT jsonb_build_object(
              'customers', (SELECT jsonb_agg(to_jsonb(c) || jsonb_build_object('xmin', c.xmin::text) ORDER BY c."ID") FROM "Customer" c),
              'companies', (SELECT jsonb_agg(to_jsonb(c) || jsonb_build_object('xmin', c.xmin::text) ORDER BY c."ID") FROM "Company" c),
              'addresses', (SELECT jsonb_agg(to_jsonb(a) || jsonb_build_object('xmin', a.xmin::text) ORDER BY a."ID") FROM "Address" a),
              'replay', (SELECT jsonb_agg(to_jsonb(r) || jsonb_build_object('xmin', r.xmin::text) ORDER BY r."Key") FROM "CustomerCreateOperation" r)
            )::text
            """;
        return (string)(await command.ExecuteScalarAsync(token))!;
    }

    private sealed record StorageCase(string Label, string Members, string? Expected, bool StorageRejects = false, bool ReadRejects = false);
}
