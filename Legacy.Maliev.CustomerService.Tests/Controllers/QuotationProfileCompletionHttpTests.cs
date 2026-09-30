using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Api.Controllers;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Data;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Testcontainers.PostgreSql;
using Maliev.Aspire.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

public sealed class QuotationProfileCompletionHttpTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly Mock<ICustomerCache> cache = new();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();
    private CustomerDbContext Db() => new(new DbContextOptionsBuilder<CustomerDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    [Theory]
    [InlineData("employee", HttpStatusCode.Forbidden)]
    [InlineData("service", HttpStatusCode.Forbidden)]
    [InlineData("other", HttpStatusCode.Forbidden)]
    [InlineData("duplicate", HttpStatusCode.Forbidden)]
    public async Task Read_DeniesNonOwnerBeforeGraphAccess(string identity, HttpStatusCode expected)
    {
        await using var app = await Start();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", identity);
        using var result = await client.GetAsync("/customers/1/instant-quotation-profile-completion");
        Assert.Equal(expected, result.StatusCode);
    }

    [Fact]
    public async Task Complete_PreservesPopulatedGraphAndReplaysAfterRestart()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        var customer = new Customer
        {
            FirstName = "Stored",
            LastName = "Owner",
            Email = "stored@example.test",
            Company = new Company { Name = "Stored company", TaxNumber = "0100000000000" },
            BillingAddress = new Address { AddressLine1 = "Stored billing", CountryId = 764 },
            ShippingAddress = new Address { AddressLine1 = "Distinct shipping", CountryId = 764 },
        };
        db.Add(customer); await db.SaveChangesAsync();
        await using var app = await Start();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", customer.Id.ToString());
        var route = $"/customers/{customer.Id}/instant-quotation-profile-completion";
        using var read = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var etag = read.Headers.ETag!.ToString();
        Assert.Equal("no-store, private", read.Headers.CacheControl!.ToString());
        var key = Guid.NewGuid().ToString();
        const string body = """
            {"FirstName":"Forged","LastName":"Forged","Telephone":"021234567","Company":"Forged","TaxNumber":"9999999999999","Billing":{"AddressLine1":"Forged","City":"Bangkok","CountryId":999},"Shipping":{"AddressLine1":"Forged","CountryId":999},"ShipToBillingAddress":true}
            """;
        using var completed = await Send(client, route, key, etag, body);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var receipt = await completed.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(receipt);
        Assert.Equal(customer.Id, json.RootElement.GetProperty("CustomerId").GetInt32());
        db.ChangeTracker.Clear();
        var saved = await db.Customers.Include(x => x.Company).Include(x => x.BillingAddress).Include(x => x.ShippingAddress).SingleAsync(x => x.Id == customer.Id);
        Assert.Equal("Stored", saved.FirstName);
        Assert.Equal("021234567", saved.Telephone);
        Assert.Equal("Stored company", saved.Company!.Name);
        Assert.Equal("0100000000000", saved.Company.TaxNumber);
        Assert.Equal("Stored billing", saved.BillingAddress!.AddressLine1);
        Assert.Equal("Bangkok", saved.BillingAddress.City);
        Assert.Equal(764, saved.BillingAddress.CountryId);
        Assert.Equal("Distinct shipping", saved.ShippingAddress!.AddressLine1);
        Assert.NotEqual(saved.BillingAddressId, saved.ShippingAddressId);
        await using var restarted = await Start();
        using var retry = restarted.GetTestClient();
        retry.DefaultRequestHeaders.Add("Test-Identity", customer.Id.ToString());
        using var replay = await Send(retry, route, key, etag, body);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt, await replay.Content.ReadAsStringAsync());
        using var conflict = await Send(retry, route, key, etag, body.Replace("021234567", "029999999", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        cache.Verify(x => x.RemoveAsync(customer.Id, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string route, string key, string etag, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("If-Match", etag); request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    [Theory]
    [InlineData(null, null, "0100000000000")]
    [InlineData("head-office", null, "0100000000000 (สำนักงานใหญ่)")]
    [InlineData("branch", "00001", "0100000000000 (สาขาที่ 00001)")]
    [InlineData("branch", "99999", "0100000000000 (สาขาที่ 99999)")]
    public async Task Complete_TypedTaxBranchPersistsCanonicalValueAndNormalizedReplay(string? branch, string? code, string expected)
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var owner = new Customer { Email = "owner@test.example", BillingAddress = new Address { AddressLine1 = "Street", CountryId = 764 } };
        db.Add(owner); await db.SaveChangesAsync();
        await using var app = await Start(); using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", owner.Id.ToString());
        var route = $"/customers/{owner.Id}/instant-quotation-profile-completion";
        using var read = await client.GetAsync(route); var key = Guid.NewGuid().ToString(); var etag = read.Headers.ETag!.ToString();
        var body = JsonSerializer.Serialize(new { TaxNumber = "0100000000000", TaxBranch = branch, TaxBranchCode = code, ShipToBillingAddress = true });
        using var result = await Send(client, route, key, etag, body);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var receipt = await result.Content.ReadAsStringAsync();
        db.ChangeTracker.Clear(); var saved = await db.Customers.Include(x => x.Company).SingleAsync();
        Assert.Equal(expected, saved.Company!.TaxNumber);
        var equivalent = JsonSerializer.Serialize(new { TaxNumber = "0100000000000", TaxBranch = branch is null ? null : $" {branch.ToUpperInvariant()} ", TaxBranchCode = code, ShipToBillingAddress = true });
        using var replay = await Send(client, route, key, etag, equivalent);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal(receipt, await replay.Content.ReadAsStringAsync());
        Assert.Single(await db.QuotationProfileCompletionOperations.ToListAsync());
    }

    [Fact]
    public async Task Complete_RejectsMalformedTaxCombinationsWithoutMutationOrReceipt()
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var owner = new Customer { Email = "owner@test.example", BillingAddress = new Address { AddressLine1 = "Street", CountryId = 764 } };
        db.Add(owner); await db.SaveChangesAsync();
        await using var app = await Start(); using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Identity", owner.Id.ToString());
        var route = $"/customers/{owner.Id}/instant-quotation-profile-completion"; using var read = await client.GetAsync(route);
        foreach (var candidate in new[]
        {
            new { TaxNumber = (string?)"0100000000000 (สำนักงานใหญ่)", TaxBranch = (string?)null, TaxBranchCode = (string?)null },
            new { TaxNumber = (string?)"0100000000000", TaxBranch = (string?)"สำนักงานใหญ่", TaxBranchCode = (string?)null },
            new { TaxNumber = (string?)"0100000000000", TaxBranch = (string?)"head-office", TaxBranchCode = (string?)"00001" },
            new { TaxNumber = (string?)null, TaxBranch = (string?)"head-office", TaxBranchCode = (string?)null },
            new { TaxNumber = (string?)"0100000000000", TaxBranch = (string?)null, TaxBranchCode = (string?)"00001" },
            new { TaxNumber = (string?)"0100000000000", TaxBranch = (string?)"branch", TaxBranchCode = (string?)null },
            new { TaxNumber = (string?)"0100000000000", TaxBranch = (string?)"branch", TaxBranchCode = (string?)"1" },
            new { TaxNumber = (string?)"0100000000000", TaxBranch = (string?)"branch", TaxBranchCode = (string?)"000001" },
            new { TaxNumber = (string?)"0100000000000", TaxBranch = (string?)"branch", TaxBranchCode = (string?)"๐๐๐๐๑" },
            new { TaxNumber = (string?)"0100000000000", TaxBranch = (string?)"branch", TaxBranchCode = (string?)"00a01" },
            new { TaxNumber = (string?)"0100000000000 (Branch 1)", TaxBranch = (string?)null, TaxBranchCode = (string?)null },
            new { TaxNumber = (string?)new string('9', 257), TaxBranch = (string?)null, TaxBranchCode = (string?)null },
            new { TaxNumber = (string?)"0100000000000", TaxBranch = (string?)new string('x', 257), TaxBranchCode = (string?)null },
        })
        {
            var body = JsonSerializer.SerializeToNode(candidate)!.AsObject(); body["ShipToBillingAddress"] = true;
            using var rejected = await Send(client, route, Guid.NewGuid().ToString(), read.Headers.ETag!.ToString(), body.ToJsonString());
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        db.ChangeTracker.Clear(); Assert.Null((await db.Customers.SingleAsync()).CompanyId);
        Assert.Empty(await db.Companies.ToListAsync()); Assert.Empty(await db.QuotationProfileCompletionOperations.ToListAsync());
        cache.Verify(x => x.RemoveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("0100000000000")]
    [InlineData("0100000000000 (สำนักงานใหญ่)")]
    [InlineData("0100000000000 (สาขาที่ 1)")]
    public async Task Complete_StoredTaxDesignationRemainsAuthoritative(string stored)
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var owner = new Customer { Email = "owner@test.example", Company = new Company { Name = "Stored", TaxNumber = stored }, BillingAddress = new Address { AddressLine1 = "Street", CountryId = 764 } };
        db.Add(owner); await db.SaveChangesAsync();
        await using var app = await Start(); using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Identity", owner.Id.ToString());
        var route = $"/customers/{owner.Id}/instant-quotation-profile-completion"; using var read = await client.GetAsync(route);
        using var result = await Send(client, route, Guid.NewGuid().ToString(), read.Headers.ETag!.ToString(),
            """{"TaxNumber":"9999999999999","TaxBranch":"branch","TaxBranchCode":"99999","Email":"forged@test.example","CustomerId":999,"ShipToBillingAddress":true}""");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        db.ChangeTracker.Clear(); Assert.Equal(stored, (await db.Companies.SingleAsync()).TaxNumber);
    }

    [Fact]
    public async Task Complete_FillsBlankEmailFromRealOwnerJwtOnlyAndBindsNewReplayMeaning()
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var owner = new Customer { Email = "", BillingAddress = new Address { AddressLine1 = "Street", CountryId = 764 } };
        db.Add(owner); await db.SaveChangesAsync(); using var rsa = RSA.Create(2048);
        await using var app = await Start(rsa); using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SignedToken(rsa, owner.Id, emailClaims: ["trusted@example.test"]));
        var route = $"/customers/{owner.Id}/instant-quotation-profile-completion"; using var read = await client.GetAsync(route);
        Assert.Equal("2", Assert.Single(read.Headers.GetValues("X-Quotation-Profile-Completion-Contract")));
        var key = Guid.NewGuid().ToString(); var etag = read.Headers.ETag!.ToString();
        const string body = """{"Email":"forged@example.test","CustomerId":999,"ShipToBillingAddress":true}""";
        using var result = await Send(client, route, key, etag, body); Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var receipt = await result.Content.ReadAsStringAsync();
        db.ChangeTracker.Clear(); Assert.Equal("trusted@example.test", (await db.Customers.SingleAsync()).Email);
        using var replay = await Send(client, route, key, etag, body); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt, await replay.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SignedToken(rsa, owner.Id, emailClaims: ["changed@example.test"]));
        using var conflict = await Send(client, route, key, etag, body); Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var latest = await client.GetAsync(route);
        using var otherKey = await Send(client, route, Guid.NewGuid().ToString(), latest.Headers.ETag!.ToString(), body);
        Assert.Equal(HttpStatusCode.OK, otherKey.StatusCode);
        db.ChangeTracker.Clear(); Assert.Equal("trusted@example.test", (await db.Customers.SingleAsync()).Email);
    }

    [Fact]
    public async Task Complete_RejectsAmbiguousOrMalformedSignedEmailBeforeDatabase()
    {
        using var rsa = RSA.Create(2048); await using var app = await Start(rsa); using var client = app.GetTestClient();
        foreach (var emails in new[]
        {
            new[] { "one@example.test", "one@example.test" }, new[] { "one@example.test", "two@example.test" },
            new[] { "" }, new[] { " " }, new[] { "not-an-email" }, new[] { "Display <one@example.test>" },
            new[] { "one@example.test\r\nother@example.test" }, new[] { new string('a', 250) + "@example.test" },
        })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SignedToken(rsa, 1, emailClaims: emails));
            using var read = await client.GetAsync("/customers/1/instant-quotation-profile-completion"); Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
            using var result = await Send(client, "/customers/1/instant-quotation-profile-completion", Guid.NewGuid().ToString(), "\"" + new string('A', 64) + "\"", "{}");
            Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        }
        // There is deliberately no schema: any persistence probe would fail instead of 403.
        cache.Verify(x => x.RemoveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Complete_LegacyReceiptReplayReturnsOriginalWithoutNewEmailMutation()
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var address = new Address { AddressLine1 = "Street", CountryId = 764 };
        var owner = new Customer { Email = "", Mobile = "0812345678", BillingAddress = address, ShippingAddress = address };
        db.Add(owner); await db.SaveChangesAsync();
        // Frozen PR #30 normalized wire serialization; no newly appended null fields.
        const string legacyProjection = """{"FirstName":null,"LastName":null,"Telephone":null,"Mobile":"0812345678","Company":null,"TaxNumber":null,"Billing":null,"Shipping":null,"ShipToBillingAddress":true}""";
        var key = Guid.NewGuid(); var receiptId = Guid.NewGuid();
        db.Add(new QuotationProfileCompletionOperation
        {
            CustomerId = owner.Id,
            Key = key,
            CompletionId = receiptId,
            Changed = true,
            ActorHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("customer-subject"))),
            RequestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyProjection))),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        using var rsa = RSA.Create(2048); await using var app = await Start(rsa); using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SignedToken(rsa, owner.Id, emailClaims: ["new-fallback@example.test"]));
        var route = $"/customers/{owner.Id}/instant-quotation-profile-completion"; using var read = await client.GetAsync(route);
        using var result = await Send(client, route, key.ToString(), "\"" + new string('A', 64) + "\"",
            """{"Mobile":" 0812345678 ","Email":"forged@example.test","ShipToBillingAddress":true}""");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        using var receipt = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.Equal(receiptId, receipt.RootElement.GetProperty("CompletionId").GetGuid()); Assert.True(receipt.RootElement.GetProperty("Changed").GetBoolean());
        db.ChangeTracker.Clear(); Assert.Equal("", (await db.Customers.SingleAsync()).Email);
        Assert.Single(await db.QuotationProfileCompletionOperations.ToListAsync());
        using var after = await client.GetAsync(route); Assert.Equal(read.Headers.ETag, after.Headers.ETag);
    }

    [Fact]
    public async Task Complete_MissingEmailClaimDoesNotInventFallbackFromPostedEmail()
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var owner = new Customer { Email = " ", BillingAddress = new Address { AddressLine1 = "Street", CountryId = 764 } };
        db.Add(owner); await db.SaveChangesAsync(); using var rsa = RSA.Create(2048);
        await using var app = await Start(rsa); using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SignedToken(rsa, owner.Id));
        var route = $"/customers/{owner.Id}/instant-quotation-profile-completion"; using var read = await client.GetAsync(route);
        using var result = await Send(client, route, Guid.NewGuid().ToString(), read.Headers.ETag!.ToString(), """{"Email":"forged@example.test","ShipToBillingAddress":true}""");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        db.ChangeTracker.Clear(); Assert.Equal(" ", (await db.Customers.SingleAsync()).Email);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("column")]
    [InlineData("type")]
    [InlineData("key")]
    [InlineData("privilege")]
    [InlineData("extra-required")]
    [InlineData("deferrable-key")]
    [InlineData("generated")]
    [InlineData("identity")]
    [InlineData("rls")]
    [InlineData("check")]
    [InlineData("trigger")]
    [InlineData("unique-index")]
    [InlineData("rule")]
    [InlineData("foreign-key")]
    public async Task CompletionCapability_FailsClosedWhenReceiptPrerequisitesAreAbsent(string drift)
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var owner = new Customer { Email = "owner@example.test", BillingAddress = new Address { AddressLine1 = "Street", CountryId = 764 } };
        db.Add(owner); await db.SaveChangesAsync();
        string? connection = null;
        switch (drift)
        {
            case "missing": await db.Database.ExecuteSqlRawAsync("DROP TABLE \"QuotationProfileCompletionOperation\""); break;
            case "column": await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"QuotationProfileCompletionOperation\" RENAME COLUMN \"Changed\" TO \"Unexpected\""); break;
            case "type": await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"QuotationProfileCompletionOperation\" ALTER COLUMN \"RequestHash\" TYPE varchar(63)"); break;
            case "key": await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"QuotationProfileCompletionOperation\" DROP CONSTRAINT \"PK_QuotationProfileCompletionOperation\""); break;
            case "extra-required": await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"QuotationProfileCompletionOperation\" ADD COLUMN \"Unexpected\" integer NOT NULL"); break;
            case "deferrable-key": await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"QuotationProfileCompletionOperation\" DROP CONSTRAINT \"PK_QuotationProfileCompletionOperation\"; ALTER TABLE \"QuotationProfileCompletionOperation\" ADD PRIMARY KEY (\"CustomerId\", \"Key\") DEFERRABLE INITIALLY IMMEDIATE"); break;
            case "generated": await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"QuotationProfileCompletionOperation\" DROP COLUMN \"Changed\"; ALTER TABLE \"QuotationProfileCompletionOperation\" ADD COLUMN \"Changed\" boolean GENERATED ALWAYS AS (true) STORED NOT NULL"); break;
            case "identity": await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"QuotationProfileCompletionOperation\" ALTER COLUMN \"CustomerId\" ADD GENERATED ALWAYS AS IDENTITY"); break;
            case "rls": await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"QuotationProfileCompletionOperation\" ENABLE ROW LEVEL SECURITY"); break;
            case "check": await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"QuotationProfileCompletionOperation\" ADD CONSTRAINT \"RejectInitialReceipt\" CHECK (\"Changed\")"); break;
            case "unique-index": await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX \"UnexpectedReceiptUniqueness\" ON \"QuotationProfileCompletionOperation\" (\"CompletionId\")"); break;
            case "foreign-key": await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"QuotationProfileCompletionOperation\" ADD CONSTRAINT \"UnexpectedReceiptLifetime\" FOREIGN KEY (\"CustomerId\") REFERENCES \"Customer\" (\"ID\")"); break;
            case "rule": await db.Database.ExecuteSqlRawAsync("CREATE RULE \"RejectReceiptInsert\" AS ON INSERT TO \"QuotationProfileCompletionOperation\" DO INSTEAD NOTHING"); break;
            case "trigger": await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_completion() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Disposable rejecting trigger'; END; $$;
                CREATE TRIGGER reject_completion BEFORE INSERT ON "QuotationProfileCompletionOperation" FOR EACH ROW EXECUTE FUNCTION reject_completion();
                """); break;
            case "privilege":
                await db.Database.ExecuteSqlRawAsync("CREATE ROLE completion_probe; GRANT USAGE ON SCHEMA public TO completion_probe; GRANT SELECT, INSERT ON TABLE \"QuotationProfileCompletionOperation\" TO completion_probe");
                connection = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Options = "-c role=completion_probe" }.ConnectionString;
                break;
        }
        await using var app = await Start(connectionString: connection); using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", owner.Id.ToString()); var route = $"/customers/{owner.Id}/instant-quotation-profile-completion";
        using var read = await client.GetAsync(route); Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);
        Assert.False(read.Headers.Contains("X-Quotation-Profile-Completion-Contract"));
        using var result = await Send(client, route, Guid.NewGuid().ToString(), "\"" + new string('A', 64) + "\"", """{"Telephone":"021234567"}""");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.False(result.Headers.Contains("X-Quotation-Profile-Completion-Contract"));
        db.ChangeTracker.Clear(); Assert.Null((await db.Customers.SingleAsync()).Telephone);
        if (drift != "missing") Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM \"QuotationProfileCompletionOperation\"").SingleAsync());
        cache.Verify(x => x.RemoveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompletionCapability_AllowsOrdinaryNonuniquePerformanceIndex()
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var address = new Address { AddressLine1 = "Street", CountryId = 764 };
        var owner = new Customer { Email = "owner@example.test", BillingAddress = address, ShippingAddress = address };
        db.Add(owner); await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX \"ReceiptAgeLookup\" ON \"QuotationProfileCompletionOperation\" (\"CreatedAt\")");
        await using var app = await Start(); using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Identity", owner.Id.ToString());
        var route = $"/customers/{owner.Id}/instant-quotation-profile-completion"; using var read = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.Equal("2", Assert.Single(read.Headers.GetValues("X-Quotation-Profile-Completion-Contract")));
        using var result = await Send(client, route, Guid.NewGuid().ToString(), read.Headers.ETag!.ToString(), "{}"); Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task CompletionCapability_RequiresRealOwnerBeforeAnySchemaProbe()
    {
        using var rsa = RSA.Create(2048); await using var app = await Start(rsa); using var client = app.GetTestClient();
        var route = "/customers/1/instant-quotation-profile-completion";
        using var anonymous = await client.GetAsync(route); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        foreach (var token in new[] { SignedToken(rsa, 1, kind: "employee"), SignedToken(rsa, 2), SignedToken(rsa, 1, duplicate: true) })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var read = await client.GetAsync(route); Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
            Assert.False(read.Headers.Contains("X-Quotation-Profile-Completion-Contract"));
            using var result = await Send(client, route, Guid.NewGuid().ToString(), "\"" + new string('A', 64) + "\"", "{}");
            Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        }
        // No migrations were applied: probing customer/journal storage would throw.
        cache.Verify(x => x.RemoveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Complete_SharedRecordsCopyOnWritePreservesOtherOwnerAndAddressAlias()
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var company = new Company { Name = "Shared", Registrar = "Stored registrar" };
        var address = new Address { AddressLine1 = "Shared street", CountryId = 764 };
        var owner = new Customer { FirstName = "Owner", Email = "owner@test.example", Company = company, BillingAddress = address, ShippingAddress = address };
        var other = new Customer { FirstName = "Other", Email = "other@test.example", Company = company, BillingAddress = address, ShippingAddress = address };
        db.AddRange(owner, other); await db.SaveChangesAsync();
        await using var app = await Start(); using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", owner.Id.ToString());
        var route = $"/customers/{owner.Id}/instant-quotation-profile-completion";
        using var read = await client.GetAsync(route);
        using var completion = await Send(client, route, Guid.NewGuid().ToString(), read.Headers.ETag!.ToString(),
            """{"TaxNumber":"0100000000000","Billing":{"City":"Bangkok","CountryId":764},"ShipToBillingAddress":true}""");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        db.ChangeTracker.Clear();
        var saved = await db.Customers.Include(x => x.Company).Include(x => x.BillingAddress).SingleAsync(x => x.Id == owner.Id);
        var unchanged = await db.Customers.Include(x => x.Company).Include(x => x.BillingAddress).SingleAsync(x => x.Id == other.Id);
        Assert.NotEqual(saved.CompanyId, unchanged.CompanyId); Assert.NotEqual(saved.BillingAddressId, unchanged.BillingAddressId);
        Assert.Equal(saved.BillingAddressId, saved.ShippingAddressId);
        Assert.Equal("Shared", saved.Company!.Name); Assert.Equal("Stored registrar", saved.Company.Registrar);
        Assert.Equal("0100000000000", saved.Company.TaxNumber); Assert.Equal("Bangkok", saved.BillingAddress!.City);
        Assert.Null(unchanged.Company!.TaxNumber); Assert.Null(unchanged.BillingAddress!.City);
        Assert.Equal(company.Id, unchanged.CompanyId); Assert.Equal(address.Id, unchanged.ShippingAddressId);
        cache.Verify(x => x.RemoveAsync(other.Id, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Complete_CreatesOnlyMissingGraphIgnoresPostedIdentityAndNoOpKeepsRevision()
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var owner = new Customer { Email = "trusted@test.example", InternalRemark = "Employee only" };
        db.Add(owner); await db.SaveChangesAsync();
        await using var app = await Start(); using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", owner.Id.ToString());
        var route = $"/customers/{owner.Id}/instant-quotation-profile-completion";
        using var read = await client.GetAsync(route);
        var readJson = await read.Content.ReadAsStringAsync();
        Assert.Contains("\"Email\":\"trusted@test.example\"", readJson, StringComparison.Ordinal);
        Assert.DoesNotContain("InternalRemark", readJson, StringComparison.Ordinal);
        Assert.DoesNotContain("BillingAddress", readJson, StringComparison.Ordinal);
        using var completion = await Send(client, route, Guid.NewGuid().ToString(), read.Headers.ETag!.ToString(),
            """{"FirstName":" New ","LastName":" Owner ","Email":"forged@test.example","CustomerId":999,"Company":"Company","TaxNumber":"0100000000000","Billing":{"AddressLine1":"Bill","City":"Bangkok","State":"Bangkok","PostalCode":"10100","CountryId":764},"Shipping":{"AddressLine1":"Ship","City":"Rayong","State":"Rayong","PostalCode":"21100","CountryId":764}}""");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        db.ChangeTracker.Clear(); var saved = await db.Customers.Include(x => x.Company).Include(x => x.BillingAddress).Include(x => x.ShippingAddress).SingleAsync();
        Assert.Equal("New", saved.FirstName); Assert.Equal("Owner", saved.LastName); Assert.Equal("trusted@test.example", saved.Email);
        Assert.Equal("Company", saved.Company!.Name); Assert.Equal("Bill", saved.BillingAddress!.AddressLine1); Assert.Equal("Ship", saved.ShippingAddress!.AddressLine1);
        Assert.NotEqual(saved.BillingAddressId, saved.ShippingAddressId); Assert.NotNull(saved.Company.CreatedDate); Assert.NotNull(saved.BillingAddress.CreatedDate);
        using var filledRead = await client.GetAsync(route); var filledRevision = filledRead.Headers.ETag!.ToString();
        using var noOp = await Send(client, route, Guid.NewGuid().ToString(), filledRevision, "{}");
        Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
        using var receipt = JsonDocument.Parse(await noOp.Content.ReadAsStringAsync()); Assert.False(receipt.RootElement.GetProperty("Changed").GetBoolean());
        using var afterNoOp = await client.GetAsync(route); Assert.Equal(filledRevision, afterNoOp.Headers.ETag!.ToString());
        cache.Verify(x => x.RemoveAsync(owner.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Complete_StaleLinkedVersionRollsBackReceiptAndMissingAddressRejectsAtomically()
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var owner = new Customer { Email = "owner@test.example", Company = new Company { Name = "Old" } };
        db.Add(owner); await db.SaveChangesAsync();
        await using var app = await Start(); using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", owner.Id.ToString());
        var route = $"/customers/{owner.Id}/instant-quotation-profile-completion";
        using var oldRead = await client.GetAsync(route);
        owner.Company!.Name = "New"; await db.SaveChangesAsync();
        using var stale = await Send(client, route, Guid.NewGuid().ToString(), oldRead.Headers.ETag!.ToString(), "{}");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var newRead = await client.GetAsync(route);
        using var rejected = await Send(client, route, Guid.NewGuid().ToString(), newRead.Headers.ETag!.ToString(),
            """{"Telephone":"021234567","Billing":{"AddressLine1":"Street","City":"Bangkok","State":"Bangkok","PostalCode":"10100","CountryId":764}}""");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        db.ChangeTracker.Clear(); Assert.Empty(await db.QuotationProfileCompletionOperations.ToListAsync());
        Assert.Empty(await db.Addresses.ToListAsync());
        Assert.Null((await db.Customers.SingleAsync()).Telephone);
        cache.Verify(x => x.RemoveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Complete_ConcurrentExactReplayCommitsOnceAndCacheFailureRemainsRetryable()
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var owner = new Customer { Email = "owner@test.example", BillingAddress = new Address { AddressLine1 = "Street", CountryId = 764 } };
        db.Add(owner); await db.SaveChangesAsync();
        await using var app = await Start(); using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", owner.Id.ToString());
        var route = $"/customers/{owner.Id}/instant-quotation-profile-completion";
        using var read = await client.GetAsync(route); var key = Guid.NewGuid().ToString(); var etag = read.Headers.ETag!.ToString();
        const string body = """{"Telephone":"021234567","ShipToBillingAddress":true}""";
        var cacheCalls = 0;
        cache.Setup(x => x.RemoveAsync(owner.Id, It.IsAny<CancellationToken>())).Returns(() =>
            Interlocked.Increment(ref cacheCalls) == 1 ? Task.FromException(new IOException("Disposable cache failure")) : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => Send(client, route, key, etag, body));
        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Send(client, route, key, etag, body)));
        var receipts = new List<string>();
        foreach (var response in responses) { using (response) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); receipts.Add(await response.Content.ReadAsStringAsync()); } }
        Assert.Single(receipts.Distinct()); Assert.Single(await db.QuotationProfileCompletionOperations.ToListAsync());
        db.ChangeTracker.Clear(); var saved = await db.Customers.SingleAsync();
        Assert.Equal("021234567", saved.Telephone); Assert.Equal(saved.BillingAddressId, saved.ShippingAddressId);
        using var nextRead = await client.GetAsync(route);
        var nextKey = Guid.NewGuid().ToString();
        var firstWrites = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Send(client, route, nextKey, nextRead.Headers.ETag!.ToString(),
            """{"Mobile":"0812345678"}""")));
        receipts.Clear();
        foreach (var response in firstWrites) { using (response) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); receipts.Add(await response.Content.ReadAsStringAsync()); } }
        Assert.Single(receipts.Distinct()); Assert.Equal(2, await db.QuotationProfileCompletionOperations.CountAsync());
    }

    [Fact]
    public async Task Routes_RequireRealRs256CustomerOwnerAndRejectWrongExpiredOrWrongIssuerTokens()
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var owner = new Customer { Email = "owner@test.example", BillingAddress = new Address { AddressLine1 = "Street", CountryId = 764 } };
        db.Add(owner); await db.SaveChangesAsync();
        using var rsa = RSA.Create(2048); using var wrong = RSA.Create(2048);
        await using var app = await Start(rsa); using var client = app.GetTestClient();
        var route = $"/customers/{owner.Id}/instant-quotation-profile-completion";
        foreach (var token in new[] { SignedToken(wrong, owner.Id), SignedToken(rsa, owner.Id, expired: true), SignedToken(rsa, owner.Id, issuer: "https://wrong.example") })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var rejected = await client.GetAsync(route); Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        foreach (var token in new[] { SignedToken(rsa, owner.Id, kind: "employee"), SignedToken(rsa, owner.Id + 1), SignedToken(rsa, owner.Id, duplicate: true) })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var rejected = await client.GetAsync(route); Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            using var posted = await Send(client, route, Guid.NewGuid().ToString(), "\"" + new string('A', 64) + "\"", "{}");
            Assert.Equal(HttpStatusCode.Forbidden, posted.StatusCode);
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SignedToken(rsa, owner.Id));
        using var read = await client.GetAsync(route); Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var completed = await Send(client, route, Guid.NewGuid().ToString(), read.Headers.ETag!.ToString(), """{"Mobile":"0812345678","ShipToBillingAddress":true}""");
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
    }

    private static string SignedToken(RSA key, int id, string kind = "customer", bool expired = false, string issuer = "https://iam.maliev.com", bool duplicate = false, string[]? emailClaims = null)
    {
        var claims = new List<Claim> { new("sub", "customer-subject"), new("identity_kind", kind), new("legacy_database_id", id.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        if (duplicate) claims.Add(new("legacy_database_id", "999"));
        foreach (var email in emailClaims ?? []) claims.Add(new("email", email));
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, "maliev-services", claims,
            now.AddHours(-2), expired ? now.AddHours(-1) : now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)));
    }

    [Theory]
    [InlineData(null, "00000000-0000-0000-0000-000000000001", HttpStatusCode.PreconditionRequired)]
    [InlineData("bad", "00000000-0000-0000-0000-000000000001", HttpStatusCode.BadRequest)]
    [InlineData("\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"", "bad", HttpStatusCode.BadRequest)]
    public async Task Complete_RejectsMissingOrMalformedConcurrencyHeadersBeforeDatabase(string? etag, string key, HttpStatusCode expected)
    {
        await using var app = await Start(); using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Test-Identity", "1");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/customers/1/instant-quotation-profile-completion") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Headers.Add("Idempotency-Key", key);
        using var result = await client.SendAsync(request); Assert.Equal(expected, result.StatusCode);
    }

    private async Task<WebApplication> Start(RSA? rsa = null, string? connectionString = null)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<CustomerDbContext>(x => x.UseNpgsql(connectionString ?? postgres.GetConnectionString()));
        builder.Services.AddSingleton(cache.Object);
        builder.Services.AddControllers().AddApplicationPart(typeof(CustomersController).Assembly).AddJsonOptions(x =>
        {
            x.JsonSerializerOptions.PropertyNamingPolicy = null;
            x.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });
        if (rsa is null) builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, IdentityHandler>("Test", _ => { });
        else
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem())),
                ["Jwt:Issuer"] = "https://iam.maliev.com",
                ["Jwt:Audience"] = "maliev-services"
            });
            builder.AddJwtAuthentication();
        }
        builder.Services.AddAuthorization();
        var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); await app.StartAsync(); return app;
    }

    private sealed class IdentityHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = Request.Headers["Test-Identity"].ToString();
            if (identity.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new("sub", "customer-subject"), new("identity_kind", identity is "employee" or "service" ? identity : "customer"), new("legacy_database_id", int.TryParse(identity, out _) ? identity : identity == "other" ? "2" : "1") };
            if (identity == "duplicate") claims.Add(new("legacy_database_id", "2"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
