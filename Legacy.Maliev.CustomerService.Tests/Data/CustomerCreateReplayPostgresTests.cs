using Legacy.Maliev.CustomerService.Api;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Application.Services;
using Legacy.Maliev.CustomerService.Data;
using Legacy.Maliev.CustomerService.Tests.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Moq;
using Testcontainers.PostgreSql;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Legacy.Maliev.CustomerService.Tests.Data;

public sealed class CustomerCreateReplayPostgresTests : IAsyncLifetime
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
    public async Task SameKeyAndPayload_ReplaysOriginalResponseWithoutCreatingAnotherRow()
    {
        var key = Guid.NewGuid();
        await using var firstDb = CreateDb();
        var first = await CreateService(firstDb).CreateAsync(key, "employee-1", Request(), CancellationToken.None);
        await using var replayDb = CreateDb();
        var replay = await CreateService(replayDb).CreateAsync(key, "employee-1", Request(), CancellationToken.None);

        Assert.False(first.ConflictingKey);
        Assert.Equal(first.Customer, replay.Customer);
        Assert.Single(await replayDb.Customers.ToListAsync());
        Assert.Single(await replayDb.CustomerCreateOperations.ToListAsync());
    }

    [Fact]
    public async Task FailedCreate_RollsBackKeyAndAllowsSafeRetry()
    {
        var key = Guid.NewGuid();
        await using var failedDb = CreateDb();
        var failingCustomers = new Mock<ICustomerService>(MockBehavior.Strict);
        failingCustomers.Setup(value => value.CreateCustomerAsync(It.IsAny<UpsertCustomerRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated persistence failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CustomerCreateReplayService(failedDb, failingCustomers.Object)
                .CreateAsync(key, "employee-1", Request(), CancellationToken.None));

        await using var retryDb = CreateDb();
        var retry = await CreateService(retryDb).CreateAsync(key, "employee-1", Request(), CancellationToken.None);
        Assert.NotNull(retry.Customer);
        Assert.Single(await retryDb.Customers.ToListAsync());
        Assert.Single(await retryDb.CustomerCreateOperations.ToListAsync());
    }

    [Fact]
    public async Task SameKeyWithDifferentPayloadOrActor_IsConflictWithoutAnotherRow()
    {
        var key = Guid.NewGuid();
        await using var db = CreateDb();
        await CreateService(db).CreateAsync(key, "employee-1", Request(), CancellationToken.None);
        await using var replayDb = CreateDb();
        var changed = await CreateService(replayDb).CreateAsync(key, "employee-1", Request() with { FirstName = "Changed" }, CancellationToken.None);
        var otherActor = await CreateService(replayDb).CreateAsync(key, "employee-2", Request(), CancellationToken.None);

        Assert.True(changed.ConflictingKey);
        Assert.Null(changed.Customer);
        Assert.True(otherActor.ConflictingKey);
        Assert.Null(otherActor.Customer);
        Assert.Single(await replayDb.Customers.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentSameKeyAcrossContexts_CommitsExactlyOneCustomer()
    {
        var key = Guid.NewGuid();
        await using var firstDb = CreateDb();
        await using var secondDb = CreateDb();
        var results = await Task.WhenAll(
            CreateService(firstDb).CreateAsync(key, "employee-1", Request(), CancellationToken.None),
            CreateService(secondDb).CreateAsync(key, "employee-1", Request(), CancellationToken.None));

        Assert.Equal(results[0].Customer, results[1].Customer);
        await using var verifyDb = CreateDb();
        Assert.Single(await verifyDb.Customers.ToListAsync());
        Assert.Single(await verifyDb.CustomerCreateOperations.ToListAsync());
    }

    [Fact]
    public async Task KeyedPost_PreservesLegacyJsonAndPermissionBoundary()
    {
        await using var app = await CompanyTaxOnlyHttpTests.StartAsync(customerConnectionString: postgres.GetConnectionString());
        using var client = app.GetTestClient();
        var key = Guid.NewGuid();

        using var anonymous = await PostAsync(client, key, Request());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("Test-Identity", "denied");
        using var restricted = await PostAsync(client, key, Request());
        Assert.Equal(HttpStatusCode.Forbidden, restricted.StatusCode);
        client.DefaultRequestHeaders.Remove("Test-Identity");
        client.DefaultRequestHeaders.Add("Test-Identity", "allowed");

        using var first = await PostAsync(client, key, Request());
        using var replay = await PostAsync(client, key, Request());
        using var changed = await PostAsync(client, key, Request() with { FirstName = "Changed" });
        using var malformedMessage = new HttpRequestMessage(HttpMethod.Post, "/customers") { Content = JsonContent.Create(Request()) };
        malformedMessage.Headers.Add("Idempotency-Key", "not-a-guid");
        using var malformed = await client.SendAsync(malformedMessage);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal("First", json.RootElement.GetProperty("FirstName").GetString());
        Assert.False(json.RootElement.TryGetProperty("firstName", out _));
        Assert.False(json.RootElement.TryGetProperty("Company", out _));
        await using var db = CreateDb();
        Assert.Single(await db.Customers.ToListAsync());
    }

    [Fact]
    public async Task CommittedKeyReplay_StillRequiresAuthenticationAndCreatePermission()
    {
        await using var app = await CompanyTaxOnlyHttpTests.StartAsync(customerConnectionString: postgres.GetConnectionString());
        using var client = app.GetTestClient();
        var key = Guid.NewGuid();

        client.DefaultRequestHeaders.Add("Test-Identity", "allowed");
        using var created = await PostAsync(client, key, Request());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var originalBody = await created.Content.ReadAsStringAsync();
        var originalLocation = created.Headers.Location;

        client.DefaultRequestHeaders.Remove("Test-Identity");
        using var anonymousReplay = await PostAsync(client, key, Request());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousReplay.StatusCode);

        client.DefaultRequestHeaders.Add("Test-Identity", "denied");
        using var restrictedReplay = await PostAsync(client, key, Request());
        Assert.Equal(HttpStatusCode.Forbidden, restrictedReplay.StatusCode);

        client.DefaultRequestHeaders.Remove("Test-Identity");
        client.DefaultRequestHeaders.Add("Test-Identity", "allowed");
        using var authorizedReplay = await PostAsync(client, key, Request());
        Assert.Equal(HttpStatusCode.Created, authorizedReplay.StatusCode);
        Assert.Equal(originalLocation, authorizedReplay.Headers.Location);
        Assert.Equal(originalBody, await authorizedReplay.Content.ReadAsStringAsync());

        await using var db = CreateDb();
        Assert.Single(await db.Customers.ToListAsync());
        Assert.Single(await db.CustomerCreateOperations.ToListAsync());
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid key, UpsertCustomerRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/customers")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await client.SendAsync(message);
    }

    private CustomerDbContext CreateDb() => new(new DbContextOptionsBuilder<CustomerDbContext>()
        .UseNpgsql(postgres.GetConnectionString()).Options);

    private static CustomerCreateReplayService CreateService(CustomerDbContext db)
    {
        var cache = new Mock<ICustomerCache>();
        var customers = new CustomerApplicationService(new CustomerRepository(db, TimeProvider.System), cache.Object);
        return new CustomerCreateReplayService(db, customers);
    }

    private static UpsertCustomerRequest Request() => new(
        "First", "Last", "02-000-0000", null, null, "replay@example.test", null, null, null, null);
}
