using System.Net;
using System.Net.Http.Json;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Data;
using Legacy.Maliev.CustomerService.Tests.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.CustomerService.Tests.Data;

public sealed class CustomerRevisionPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var db = CreateDb();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task NativeXminMapping_MigratesWithoutCreatingAUserColumn()
    {
        await using var db = CreateDb();
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        await using var connection = new Npgsql.NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'Customer' AND column_name = 'xmin'",
            connection);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task VersionedHttpWrite_RejectsStaleAndMalformedWithoutMutation()
    {
        await using var app = await CompanyTaxOnlyHttpTests.StartAsync(customerConnectionString: postgres.GetConnectionString());
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", "allowed");
        using var created = await client.PostAsJsonAsync("/customers", Request());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<CustomerResponse>())!.Id;

        using var firstRead = await client.GetAsync($"/customers/{id}/versioned");
        using var secondRead = await client.GetAsync($"/customers/{id}/versioned");
        using var legacyRead = await client.GetAsync($"/customers/{id}");
        Assert.Equal(HttpStatusCode.OK, firstRead.StatusCode);
        Assert.Equal(firstRead.Headers.ETag, secondRead.Headers.ETag);
        Assert.Equal("no-store", firstRead.Headers.CacheControl?.ToString());
        Assert.Null(legacyRead.Headers.ETag);
        Assert.Equal(await legacyRead.Content.ReadAsStringAsync(), await firstRead.Content.ReadAsStringAsync());
        var original = await firstRead.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.Equal("First", original?.FirstName);
        Assert.DoesNotContain("Revision", await firstRead.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var missing = await PutAsync(client, id, Request() with { FirstName = "Missing" }, null);
        using var malformed = await PutAsync(client, id, Request() with { FirstName = "Malformed" }, "W/\"00000001\"");
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        var revision = firstRead.Headers.ETag!.ToString();
        using var firstWrite = await PutAsync(client, id, Request() with { FirstName = "Winner" }, revision);
        using var staleWrite = await PutAsync(client, id, Request() with { FirstName = "Loser" }, revision);
        using var identicalRetry = await PutAsync(client, id, Request() with { FirstName = "Winner" }, revision);
        Assert.Equal(HttpStatusCode.NoContent, firstWrite.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleWrite.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, identicalRetry.StatusCode);

        using var freshRead = await client.GetAsync($"/customers/{id}/versioned");
        Assert.NotEqual(revision, freshRead.Headers.ETag!.ToString());
        Assert.Equal("Winner", (await freshRead.Content.ReadFromJsonAsync<CustomerResponse>())!.FirstName);
        using var freshWrite = await PutAsync(client, id, Request() with { FirstName = "Fresh" }, freshRead.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NoContent, freshWrite.StatusCode);
        using var finalRead = await client.GetAsync($"/customers/{id}/versioned");
        Assert.Equal("Fresh", (await finalRead.Content.ReadFromJsonAsync<CustomerResponse>())!.FirstName);
    }

    [Fact]
    public async Task VersionedHttpWrite_RequiresResourcePermissionBeforePreconditionParsing()
    {
        await using var app = await CompanyTaxOnlyHttpTests.StartAsync(customerConnectionString: postgres.GetConnectionString());
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", "allowed");
        using var created = await client.PostAsJsonAsync("/customers", Request());
        var id = (await created.Content.ReadFromJsonAsync<CustomerResponse>())!.Id;
        client.DefaultRequestHeaders.Remove("Test-Identity");
        using var anonymousRead = await client.GetAsync($"/customers/{id}/versioned");
        using var anonymousWrite = await PutAsync(client, id, Request(), null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRead.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousWrite.StatusCode);
        client.DefaultRequestHeaders.Add("Test-Identity", "denied");
        using var forbiddenRead = await client.GetAsync($"/customers/{id}/versioned");
        using var forbiddenWrite = await PutAsync(client, id, Request(), null);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenWrite.StatusCode);
    }

    [Fact]
    public async Task SameRevisionAcrossContexts_CommitsOneWriteDespiteFrozenTimestamp()
    {
        var frozenClock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        await using var seedDb = CreateDb();
        var repository = new CustomerRepository(seedDb, frozenClock);
        var customer = await repository.CreateCustomerAsync(Request(), CancellationToken.None);
        await seedDb.Customers.Where(value => value.Id == customer.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.InternalRemark, "staff only"));
        var initial = (await repository.GetCustomerVersionedAsync(customer.Id, CancellationToken.None))!;
        var revision = initial.Revision;
        await using var firstDb = CreateDb();
        await using var secondDb = CreateDb();
        var results = await Task.WhenAll(
            new CustomerRepository(firstDb, frozenClock).UpdateCustomerIfRevisionAsync(
                customer.Id, Request() with { FirstName = "One" }, revision, CancellationToken.None),
            new CustomerRepository(secondDb, frozenClock).UpdateCustomerIfRevisionAsync(
                customer.Id, Request() with { FirstName = "Two" }, revision, CancellationToken.None));
        Assert.Equal(1, results.Count(result => result == CustomerRevisionUpdateResult.Updated));
        Assert.Equal(1, results.Count(result => result == CustomerRevisionUpdateResult.Stale));
        await using var verifyDb = CreateDb();
        var persisted = await new CustomerRepository(verifyDb, frozenClock)
            .GetCustomerVersionedAsync(customer.Id, CancellationToken.None);
        Assert.Contains(persisted!.Customer.FirstName, new[] { "One", "Two" });
        Assert.NotEqual(revision, persisted.Revision);
        Assert.Equal(initial.Customer.ModifiedDate, persisted.Customer.ModifiedDate);
        Assert.Equal("staff only", await verifyDb.Customers.Where(value => value.Id == customer.Id)
            .Select(value => value.InternalRemark).SingleAsync());
    }

    private static async Task<HttpResponseMessage> PutAsync(
        HttpClient client, int id, UpsertCustomerRequest request, string? etag)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"/customers/{id}/versioned")
        {
            Content = JsonContent.Create(request),
        };
        if (etag is not null) message.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(message);
    }

    private CustomerDbContext CreateDb() => new(new DbContextOptionsBuilder<CustomerDbContext>()
        .UseNpgsql(postgres.GetConnectionString()).Options);

    private static UpsertCustomerRequest Request() => new(
        "First", "Last", null, null, null, "revision@example.test", null, null, null, null);
}
