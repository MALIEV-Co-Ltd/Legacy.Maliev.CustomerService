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

public sealed class CustomerDetailAuthorityFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly IContainer redis = new ContainerBuilder("redis:7.4-alpine").WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    private readonly RSA rsa = RSA.Create(2048);
    private ConnectionMultiplexer admin = null!;
    private string RedisConnection => $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)},allowAdmin=true";
    public CustomerDbContext Context() => new(new DbContextOptionsBuilder<CustomerDbContext>()
        .UseNpgsql(postgres.GetConnectionString()).Options);

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await redis.StartAsync();
        admin = await ConnectionMultiplexer.ConnectAsync(RedisConnection);
        await admin.GetDatabase().ExecuteAsync("ACL", "SETUSER", "detail-fixture", "on", ">test-only", "~*", "+@all");
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    public async Task ResetAsync()
    {
        await admin.GetDatabase().ExecuteAsync("ACL", "SETUSER", "detail-fixture", "+@all");
        await admin.GetServer(redis.Hostname, redis.GetMappedPublicPort(6379)).FlushDatabaseAsync();
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"Customer\", \"Company\", \"Address\" RESTART IDENTITY CASCADE");
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

    public Task DenyRemovalAsync() => admin.GetDatabase().ExecuteAsync("ACL", "SETUSER", "detail-fixture", "-del", "-unlink");
    public Task<bool> CacheExistsAsync() => admin.GetDatabase().KeyExistsAsync("legacy:customer:customer:1");

    public async Task AssertRemovalDeniedAsync(WebApplicationFactory<Program> host)
    {
        var exception = await Assert.ThrowsAsync<RedisServerException>(() => host.Services.GetRequiredService<IDistributedCache>().RemoveAsync("customer:1"));
        Assert.Contains("NOPERM", exception.Message, StringComparison.Ordinal);
    }

    public Task SeedOldAsync(WebApplicationFactory<Program> host)
    {
        // Independent old-writer DTO bytes, not a serialization of a production read.
        const string json = """{"id":1,"firstName":"Before","lastName":"Customer","fullName":"Before Customer","email":"detail@example.test","companyId":1,"billingAddressId":1,"shippingAddressId":1,"company":{"id":1,"name":"Before company","taxNumber":"123"},"billingAddress":{"id":1,"addressLine1":"Before road","city":"Bangkok","countryId":764},"shippingAddress":{"id":1,"addressLine1":"Before road","city":"Bangkok","countryId":764}}""";
        return host.Services.GetRequiredService<IDistributedCache>().SetAsync("customer:1", Encoding.UTF8.GetBytes(json),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
    }

    public WebApplicationFactory<Program> Start(bool restricted = false, CustomerDetailSaveBarrier? barrier = null)
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
        });
        Assert.Equal("Production", host.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
        return host;
    }

    public HttpClient Client(WebApplicationFactory<Program> host, string authority = "full")
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (authority == "anonymous") return client;
        var permissions = authority == "denied" ? Array.Empty<string>() :
            new[] { "legacy-customer.customers.read", "legacy-customer.customers.update", "legacy-customer.companies.update", "legacy-customer.addresses.update" };
        if (authority == "directory") permissions = [.. permissions, "legacy-customer.customers.list"];
        if (authority == "address-lifecycle") permissions = [.. permissions, "legacy-customer.addresses.create", "legacy-customer.addresses.read"];
        using var other = RSA.Create(2048);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken("customer-detail-fixture", "customer-detail-fixture",
            new[] { new Claim("sub", "employee:customer-detail-fixture") }.Concat(permissions.Select(permission => new Claim("permission", permission))),
            now.AddHours(-2), authority == "expired" ? now.AddHours(-1) : now.AddMinutes(5),
            new SigningCredentials(new RsaSecurityKey(authority == "wrong-signature" ? other : rsa), SecurityAlgorithms.RsaSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

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

    public async Task DisposeAsync()
    {
        if (admin is not null) await admin.DisposeAsync();
        rsa.Dispose();
        await redis.DisposeAsync();
        await postgres.DisposeAsync();
    }
}

public sealed class CustomerDetailSaveBarrier : SaveChangesInterceptor
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        finally { Cancelled.TrySetResult(); }
        return result;
    }
}
