using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.CustomerService.Data;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Owns disposable PostgreSQL, Redis, signing keys, and production hosts for authoritative customer detail tests.</summary>
public sealed class CustomerDetailAuthorityFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly IContainer redis = new ContainerBuilder("redis:7.4-alpine").WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    private readonly RSA rsa = RSA.Create(2048);
    private ConnectionMultiplexer admin = null!;
    private string RedisConnection => $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)},allowAdmin=true";
    /// <summary>Creates a fresh database context connected to the fixture-owned PostgreSQL instance.</summary>
    /// <returns>A new CustomerDbContext that the caller must dispose.</returns>
    public CustomerDbContext Context() => new(new DbContextOptionsBuilder<CustomerDbContext>()
        .UseNpgsql(postgres.GetConnectionString()).Options);

    /// <summary>Starts owned database and cache containers, configures the test cache user, and applies the customer schema.</summary>
    /// <returns>A task that completes when the fixture database, Redis connection, cache authority, and migrated schema are ready.</returns>
    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await redis.StartAsync();
        admin = await ConnectionMultiplexer.ConnectAsync(RedisConnection);
        await admin.GetDatabase().ExecuteAsync("ACL", "SETUSER", "detail-fixture", "on", ">test-only", "~*", "+@all");
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    /// <summary>Resets cache authority and seeded customer, company, address, and replay-operation state.</summary>
    /// <returns>A task that completes when caches are cleared and the original customer relationship graph is restored.</returns>
    public async Task ResetAsync()
    {
        await admin.GetDatabase().ExecuteAsync("ACL", "SETUSER", "detail-fixture", "+@all");
        await admin.GetServer(redis.Hostname, redis.GetMappedPublicPort(6379)).FlushDatabaseAsync();
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"CustomerCreateOperation\", \"Customer\", \"Company\", \"Address\" RESTART IDENTITY CASCADE");
        var company = new Company { Name = "Before company", TaxNumber = "123" };
        var address = new Address { AddressLine1 = "Before road", City = "Bangkok", CountryId = 764 };
        db.Customers.Add(new Customer
        {
            FirstName = "Before",
            LastName = "Customer",
            Email = "detail@example.test",
            Company = company,
            BillingAddress = address,
            ShippingAddress = address,
            InternalRemark = "staff-only"
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Revokes DEL and UNLINK from the fixture cache user to exercise failed cache cleanup.</summary>
    /// <returns>A task that completes when the fixture cache user can no longer remove keys.</returns>
    public Task DenyRemovalAsync() => admin.GetDatabase().ExecuteAsync("ACL", "SETUSER", "detail-fixture", "-del", "-unlink");
    /// <summary>Checks whether the seeded customer detail key remains in Redis.</summary>
    /// <returns>A task returning true when the seeded customer cache key exists; otherwise, false.</returns>
    public Task<bool> CacheExistsAsync() => admin.GetDatabase().KeyExistsAsync("legacy:customer:customer:1");

    /// <summary>Asserts that the host cache connection cannot remove the seeded customer key.</summary>
    /// <param name="host">The production host whose distributed cache uses the restricted fixture Redis account.</param>
    /// <returns>A task that completes when a Redis NOPERM removal failure has been verified.</returns>
    public async Task AssertRemovalDeniedAsync(WebApplicationFactory<Program> host)
    {
        var exception = await Assert.ThrowsAsync<RedisServerException>(() => host.Services.GetRequiredService<IDistributedCache>().RemoveAsync("customer:1"));
        Assert.Contains("NOPERM", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Seeds independent old-writer customer projection bytes into the host cache.</summary>
    /// <param name="host">The production host whose distributed cache receives the ten-minute historical projection.</param>
    /// <returns>A task that completes when the old-writer projection has been cached.</returns>
    public Task SeedOldAsync(WebApplicationFactory<Program> host)
    {
        // Independent old-writer DTO bytes, not a serialization of a production read.
        const string json = """{"id":1,"firstName":"Before","lastName":"Customer","fullName":"Before Customer","email":"detail@example.test","companyId":1,"billingAddressId":1,"shippingAddressId":1,"company":{"id":1,"name":"Before company","taxNumber":"123"},"billingAddress":{"id":1,"addressLine1":"Before road","city":"Bangkok","countryId":764},"shippingAddress":{"id":1,"addressLine1":"Before road","city":"Bangkok","countryId":764}}""";
        return host.Services.GetRequiredService<IDistributedCache>().SetAsync("customer:1", Encoding.UTF8.GetBytes(json),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
    }

    /// <summary>Creates a production application host bound to fixture services with optional cache restrictions and EF interceptors.</summary>
    /// <param name="restricted">Whether the host connects as the fixture Redis user whose removal permissions can be revoked.</param>
    /// <param name="barrier">The optional save interceptor that blocks persistence until request cancellation is observed.</param>
    /// <param name="interceptor">An additional optional EF interceptor installed on the host database context.</param>
    /// <returns>The configured production WebApplicationFactory, owned and disposed by the caller.</returns>
    public WebApplicationFactory<Program> Start(bool restricted = false, CustomerDetailSaveBarrier? barrier = null, IInterceptor? interceptor = null)
    {
        var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            foreach (var setting in new Dictionary<string, string>
            {
                ["ConnectionStrings:CustomerDbContext"] = postgres.GetConnectionString(),
                ["ConnectionStrings:redis"] = RedisConnection + (restricted ? ",user=detail-fixture,password=test-only" : ""),
                ["Cache:RedisEnabled"] = "true",
                ["CORS:AllowedOrigins:0"] = "https://example.test",
                ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem())),
                ["Jwt:Issuer"] = "customer-detail-fixture",
                ["Jwt:Audience"] = "customer-detail-fixture",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            }) builder.UseSetting(setting.Key, setting.Value);
            if (barrier is not null) builder.ConfigureServices(services =>
                services.ConfigureDbContext<CustomerDbContext>(options => options.AddInterceptors(barrier)));
            if (interceptor is not null) builder.ConfigureServices(services =>
                services.ConfigureDbContext<CustomerDbContext>(options => options.AddInterceptors(interceptor)));
        });
        Assert.Equal("Production", host.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
        return host;
    }

    /// <summary>Creates a host client with the selected fixture JWT authority or no authentication.</summary>
    /// <param name="host">The application factory from which the HTTP client is created.</param>
    /// <param name="authority">The fixture authority selector controlling anonymous access, permissions, lifecycle or relation scopes, expiry, and signature validity; full is the default.</param>
    /// <returns>An HTTP client with redirects disabled and the selected request authority; the caller owns its disposal.</returns>
    public HttpClient Client(WebApplicationFactory<Program> host, string authority = "full")
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (authority == "anonymous") return client;
        var permissions = authority == "denied" ? Array.Empty<string>() :
            new[] { "legacy-customer.customers.read", "legacy-customer.customers.update", "legacy-customer.companies.update", "legacy-customer.addresses.update" };
        if (authority == "directory") permissions = [.. permissions, "legacy-customer.customers.list"];
        if (authority == "address-lifecycle") permissions = [.. permissions, "legacy-customer.addresses.create", "legacy-customer.addresses.read"];
        if (authority == "company-lifecycle") permissions = [.. permissions, "legacy-customer.companies.create", "legacy-customer.companies.read", "legacy-customer.companies.delete"];
        if (authority == "customer-lifecycle") permissions = [.. permissions, "legacy-customer.customers.create", "legacy-customer.customers.delete"];
        if (authority == "shared-relationship") permissions = [.. permissions, "legacy-customer.companies.delete", "legacy-customer.addresses.delete"];
        if (authority.StartsWith("relation-", StringComparison.Ordinal))
        {
            permissions = [.. permissions, "legacy-customer.companies.create", "legacy-customer.companies.read", "legacy-customer.addresses.create", "legacy-customer.addresses.read"];
            if (authority == "relation-no-customer-update") permissions = permissions.Where(value => value != "legacy-customer.customers.update").ToArray();
            if (authority == "relation-no-metadata-update") permissions = permissions.Where(value => value is not ("legacy-customer.companies.update" or "legacy-customer.addresses.update")).ToArray();
        }
        using var other = RSA.Create(2048);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken("customer-detail-fixture", "customer-detail-fixture",
            new[] { new Claim("sub", "employee:customer-detail-fixture") }.Concat(permissions.Select(permission => new Claim("permission", permission))),
            now.AddHours(-2), authority == "expired" ? now.AddHours(-1) : now.AddMinutes(5),
            new SigningCredentials(new RsaSecurityKey(authority == "wrong-signature" ? other : rsa), SecurityAlgorithms.RsaSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    /// <summary>Reads the distinguishing persisted scalar from the seeded customer, company, or address.</summary>
    /// <param name="kind">The customer, company, or address entity selecting FirstName, Name, or AddressLine1.</param>
    /// <returns>A task returning the selected stored scalar value.</returns>
    public async Task<string> StoredAsync(string kind)
    {
        await using var db = Context();
        return kind switch
        {
            "customer" => await db.Customers.Select(row => row.FirstName).SingleAsync(),
            "company" => await db.Companies.Select(row => row.Name).SingleAsync(),
            "address" => await db.Addresses.Select(row => row.AddressLine1).SingleAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    /// <summary>Captures a stable serialized snapshot of the seeded customer relationship graph.</summary>
    /// <returns>A task returning JSON for the seeded customer, company, and address fields, relationship identifiers, and modification dates.</returns>
    public async Task<string> SnapshotAsync()
    {
        await using var db = Context();
        return JsonSerializer.Serialize(new
        {
            Customer = await db.Customers.AsNoTracking().Select(row => new { row.Id, row.FirstName, row.LastName, row.Email, row.CompanyId, row.BillingAddressId, row.ShippingAddressId, row.ModifiedDate, row.InternalRemark }).SingleAsync(),
            Company = await db.Companies.AsNoTracking().Select(row => new { row.Id, row.Name, row.TaxNumber, row.ModifiedDate }).SingleAsync(),
            Address = await db.Addresses.AsNoTracking().Select(row => new { row.Id, row.AddressLine1, row.City, row.CountryId, row.ModifiedDate }).SingleAsync()
        });
    }

    /// <summary>Disposes the fixture Redis connection, signing key, and owned cache and database containers.</summary>
    /// <returns>A task that completes when the fixture-owned resources have been disposed.</returns>
    public async Task DisposeAsync()
    {
        if (admin is not null) await admin.DisposeAsync();
        rsa.Dispose();
        await redis.DisposeAsync();
        await postgres.DisposeAsync();
    }
}

/// <summary>Blocks an EF save so tests can observe request cancellation before persistence.</summary>
public sealed class CustomerDetailSaveBarrier : SaveChangesInterceptor
{
    /// <summary>Signals that an intercepted save has entered the cancellation barrier.</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Signals that the intercepted save has left its blocking delay after cancellation.</summary>
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Signals save entry and blocks until the save cancellation token interrupts the delay.</summary>
    /// <param name="eventData">The EF context event data supplied for the intercepted save.</param>
    /// <param name="result">The incoming EF interception result retained by the override.</param>
    /// <param name="cancellationToken">The request save token that cancels the infinite barrier delay.</param>
    /// <returns>An asynchronous interception that remains blocked until cancellation and then propagates the cancellation exception.</returns>
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        finally { Cancelled.TrySetResult(); }
        return result;
    }
}
