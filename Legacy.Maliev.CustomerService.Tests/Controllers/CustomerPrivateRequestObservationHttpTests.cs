using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Verifies production health identity and private request observations without changing customer or cache state.</summary>
/// <param name="fixture">The shared Customer authority fixture supplying disposable PostgreSQL and Redis, production hosts, and signed request clients.</param>
[Collection("Customer production pipeline")]
public sealed class CustomerPrivateRequestObservationHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    private const string InstanceHeader = "X-Maliev-Health-Instance";
    private const string NonceHeader = "X-Maliev-Diagnostic-Id";
    private const string DiagnosticPath = "/internal/diagnostics/observability";

    /// <summary>Checks quiet stable per-host health identifiers only on registered GET endpoints with sanitized readiness output.</summary>
    /// <returns>A task that completes after verifying quiet stable per-host health identifiers only on registered GET endpoints with sanitized readiness output.</returns>
    [Fact]
    public async Task ProductionHealth_UsesStablePrivateIdentityOnlyForRegisteredGetEndpoints()
    {
        await fixture.ResetAsync();
        await using var first = new ProbeHost(fixture);
        using var client = first.Client(fixture);
        Assert.True(first.Factory.Services.GetRequiredService<IHostEnvironment>().IsProduction());
        Assert.IsNotType<TestServer>(first.Factory.Services.GetRequiredService<IServer>());
        await fixture.SeedOldAsync(first.Factory);
        var cacheBefore = await CacheAsync(first.Factory);
        Assert.NotNull(cacheBefore);
        var before = await SnapshotAsync();
        string? identity = null;
        foreach (var path in new[] { "/customer/liveness", "/customer/readiness", "/customer/liveness" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var marker = Assert.Single(response.Headers.GetValues(InstanceHeader));
            Assert.True(Guid.TryParseExact(marker, "N", out _));
            identity ??= marker;
            Assert.Equal(identity, marker);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.False(response.Headers.Contains(NonceHeader));
            if (path.EndsWith("/readiness", StringComparison.Ordinal))
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("Healthy", json.RootElement.GetProperty("status").GetString());
                Assert.NotEmpty(json.RootElement.GetProperty("checks").EnumerateObject());
                foreach (var check in json.RootElement.GetProperty("checks").EnumerateObject())
                    foreach (var privateField in new[] { "description", "data", "exception" })
                        Assert.False(check.Value.TryGetProperty(privateField, out _));
            }
            else Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        }
        foreach (var entry in new[]
        {
            (Method: "POST", Path: "/customer/liveness"),
            (Method: "HEAD", Path: "/customer/liveness"),
            (Method: "POST", Path: "/customer/readiness"),
            (Method: "GET", Path: "/customer/aspire-liveness"),
            (Method: "GET", Path: "/customer/liveness/extra"),
            (Method: "GET", Path: "/liveness"),
            (Method: "GET", Path: "/not-a-health-endpoint"),
        })
        {
            using var request = new HttpRequestMessage(new HttpMethod(entry.Method), entry.Path);
            using var response = await client.SendAsync(request);
            Assert.False(response.Headers.Contains(InstanceHeader));
            Assert.False(response.Headers.Contains(NonceHeader));
        }
        await using var other = new ProbeHost(fixture);
        using var otherClient = other.Client(fixture);
        using var independent = await otherClient.GetAsync("/customer/liveness");
        Assert.Equal(HttpStatusCode.OK, independent.StatusCode);
        var otherIdentity = Assert.Single(independent.Headers.GetValues(InstanceHeader));
        Assert.True(Guid.TryParseExact(otherIdentity, "N", out _));
        Assert.NotEqual(identity, otherIdentity);
        Assert.Empty(first.Records.Snapshot);
        Assert.Empty(other.Records.Snapshot);
        Assert.False(first.Records.Overflowed);
        Assert.False(other.Records.Overflowed);
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(cacheBefore, await CacheAsync(first.Factory));
    }

    /// <summary>Checks private probe denial, nonce canonicalization, and exact one-minute throttling without consuming budget on rejected requests.</summary>
    /// <returns>A task that completes after verifying private probe denial, nonce canonicalization, and exact one-minute throttling without consuming budget on rejected requests.</returns>
    [Fact]
    public async Task ProductionDiagnostic_RejectsSpoofedPeersAndInvalidNoncesWithoutConsumingTheMinute()
    {
        await fixture.ResetAsync();
        await using var host = new ProbeHost(fixture);
        using var client = host.Client(fixture);
        await fixture.SeedOldAsync(host.Factory);
        var cacheBefore = await CacheAsync(host.Factory);
        Assert.NotNull(cacheBefore);
        var before = await SnapshotAsync();
        using var health = await client.GetAsync("/customer/liveness");
        var identity = Assert.Single(health.Headers.GetValues(InstanceHeader));
        foreach (var nonce in new string?[] { null, "invalid", Guid.NewGuid().ToString("D") })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, DiagnosticPath);
            if (nonce is not null) request.Headers.Add(NonceHeader, nonce);
            using var response = await client.SendAsync(request);
            AssertDenied(response);
        }
        using (var duplicate = new HttpRequestMessage(HttpMethod.Get, DiagnosticPath))
        {
            duplicate.Headers.Add(NonceHeader, new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") });
            using var response = await client.SendAsync(duplicate);
            AssertDenied(response);
        }
        using (var post = new HttpRequestMessage(HttpMethod.Post, DiagnosticPath))
        {
            post.Headers.Add(NonceHeader, Guid.NewGuid().ToString("N"));
            using var response = await client.SendAsync(post);
            AssertDenied(response);
        }
        Assert.Empty(host.Records.Snapshot);
        var nonceValue = Guid.NewGuid().ToString("N");
        using (var request = new HttpRequestMessage(HttpMethod.Get, DiagnosticPath))
        {
            request.Headers.Add(NonceHeader, nonceValue.ToUpperInvariant());
            using var response = await client.SendAsync(request);
            AssertAdmitted(response, identity, nonceValue);
        }
        foreach (var elapsed in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(59999) })
        {
            host.Clock.Advance(elapsed);
            using var request = new HttpRequestMessage(HttpMethod.Get, DiagnosticPath);
            request.Headers.Add(NonceHeader, Guid.NewGuid().ToString("N"));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.False(response.Headers.Contains(InstanceHeader));
            Assert.False(response.Headers.Contains(NonceHeader));
        }
        host.Clock.Advance(TimeSpan.FromMilliseconds(1));
        using (var request = new HttpRequestMessage(HttpMethod.Get, DiagnosticPath))
        {
            request.Headers.Add(NonceHeader, Guid.Empty.ToString("N"));
            using var response = await client.SendAsync(request);
            AssertAdmitted(response, identity, Guid.Empty.ToString("N"));
        }
        Assert.Equal(new[]
        {
            ProbeEvent.Probe, ProbeEvent.FrameworkError, ProbeEvent.Unhandled,
            ProbeEvent.Probe, ProbeEvent.FrameworkError, ProbeEvent.Unhandled,
        }, host.Records.Snapshot);
        Assert.False(host.Records.Overflowed);

        await using var spoof = new ProbeHost(fixture, kestrel: false);
        using var spoofClient = spoof.Client(fixture);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var peer in new[] { IPAddress.Parse("203.0.113.7"), null })
        {
            var denied = await spoof.Factory.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = peer;
                context.Request.Method = "GET";
                context.Request.Path = DiagnosticPath;
                context.Request.Headers[NonceHeader] = Guid.NewGuid().ToString("N");
                context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
            }, deadline.Token);
            // Consume the exact response to EOF before inspecting terminal logging.
            using (denied.Response.Body)
                Assert.Equal(0, await denied.Response.Body.ReadAsync(new byte[1], deadline.Token));
            Assert.Equal(StatusCodes.Status404NotFound, denied.Response.StatusCode);
            Assert.False(denied.Response.Headers.ContainsKey(InstanceHeader));
            Assert.False(denied.Response.Headers.ContainsKey(NonceHeader));
            Assert.Equal("no-store", denied.Response.Headers.CacheControl.ToString());
            Assert.Equal("noindex", denied.Response.Headers["X-Robots-Tag"].ToString());
        }
        Assert.Empty(spoof.Records.Snapshot);
        Assert.False(spoof.Records.Overflowed);
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(cacheBefore, await CacheAsync(host.Factory));
    }

    /// <summary>Checks authorization and private returned or thrown failure observations without probe headers or physical state changes.</summary>
    /// <returns>A task that completes after verifying authorization and private returned or thrown failure observations without probe headers or physical state changes.</returns>
    [Fact]
    public async Task ProductionOrdinaryFailures_PreserveAuthorizationPrivateErrorsAndPhysicalState()
    {
        await fixture.ResetAsync();
        await using var host = new ProbeHost(fixture);
        using var client = host.Client(fixture);
        await fixture.SeedOldAsync(host.Factory);
        var cacheBefore = await CacheAsync(host.Factory);
        Assert.NotNull(cacheBefore);
        var before = await SnapshotAsync();
        using var unauthorized = await client.GetAsync("/customers/1");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.False(unauthorized.Headers.Contains(InstanceHeader));
        Assert.False(unauthorized.Headers.Contains(NonceHeader));
        using var returned = await client.GetAsync("/__fixture/customer-observation/returned");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, returned.StatusCode);
        using var returnedJson = JsonDocument.Parse(await returned.Content.ReadAsStringAsync());
        Assert.Equal("fixture-returned-failure", returnedJson.RootElement.GetProperty("error").GetString());
        Assert.False(returned.Headers.Contains(InstanceHeader));
        Assert.False(returned.Headers.Contains(NonceHeader));
        using var thrown = await client.GetAsync("/__fixture/customer-observation/throw");
        Assert.Equal(HttpStatusCode.InternalServerError, thrown.StatusCode);
        var body = await thrown.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("An internal server error occurred", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("details").ValueKind);
        Assert.DoesNotContain("fixture-private-error-sentinel", body, StringComparison.Ordinal);
        Assert.False(thrown.Headers.Contains(InstanceHeader));
        Assert.False(thrown.Headers.Contains(NonceHeader));
        Assert.Single(host.Records.Snapshot, value => value == ProbeEvent.Handled);
        Assert.Single(host.Records.Snapshot, value => value == ProbeEvent.Unhandled);
        Assert.DoesNotContain(ProbeEvent.Probe, host.Records.Snapshot);
        Assert.False(host.Records.Overflowed);
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(cacheBefore, await CacheAsync(host.Factory));
    }

    private static void AssertDenied(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains(InstanceHeader));
        Assert.False(response.Headers.Contains(NonceHeader));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
    }

    private static void AssertAdmitted(HttpResponseMessage response, string identity, string nonce)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(identity, Assert.Single(response.Headers.GetValues(InstanceHeader)));
        Assert.Equal(nonce, Assert.Single(response.Headers.GetValues(NonceHeader)));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
    }

    private static async Task<byte[]?> CacheAsync(WebApplicationFactory<Program> host)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await host.Services.GetRequiredService<IDistributedCache>().GetAsync("customer:1", deadline.Token);
    }

    private async Task<string> SnapshotAsync()
    {
        await using var db = fixture.Context();
        var connection = db.Database.GetDbConnection();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 15;
        command.CommandText = """
            SELECT jsonb_build_object(
                'Customer', (SELECT coalesce(jsonb_agg(payload ORDER BY payload::text), '[]'::jsonb) FROM
                    (SELECT to_jsonb(t) || jsonb_build_object('_xmin', t.xmin::text) AS payload FROM "Customer" t) s),
                'Company', (SELECT coalesce(jsonb_agg(payload ORDER BY payload::text), '[]'::jsonb) FROM
                    (SELECT to_jsonb(t) || jsonb_build_object('_xmin', t.xmin::text) AS payload FROM "Company" t) s),
                'Address', (SELECT coalesce(jsonb_agg(payload ORDER BY payload::text), '[]'::jsonb) FROM
                    (SELECT to_jsonb(t) || jsonb_build_object('_xmin', t.xmin::text) AS payload FROM "Address" t) s),
                'CustomerCreateOperation', (SELECT coalesce(jsonb_agg(payload ORDER BY payload::text), '[]'::jsonb) FROM
                    (SELECT to_jsonb(t) || jsonb_build_object('_xmin', t.xmin::text) AS payload FROM "CustomerCreateOperation" t) s),
                'QuotationProfileCompletionOperation', (SELECT coalesce(jsonb_agg(payload ORDER BY payload::text), '[]'::jsonb) FROM
                    (SELECT to_jsonb(t) || jsonb_build_object('_xmin', t.xmin::text) AS payload FROM "QuotationProfileCompletionOperation" t) s)
            )::text
            """;
        return Assert.IsType<string>(await command.ExecuteScalarAsync(deadline.Token));
    }

    private sealed class ProbeHost : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> parent;
        public WebApplicationFactory<Program> Factory { get; }
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        public ProbeRecorder Records { get; } = new();

        public ProbeHost(CustomerDetailAuthorityFixture fixture, bool kestrel = true)
        {
            parent = fixture.Start();
            try
            {
                Factory = parent.WithWebHostBuilder(builder =>
                {
                    builder.ConfigureLogging(logging => logging.AddProvider(Records));
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<TimeProvider>();
                        services.AddSingleton<TimeProvider>(Clock);
                        services.Configure<HostOptions>(options =>
                        {
                            options.StartupTimeout = TimeSpan.FromSeconds(20);
                            options.ShutdownTimeout = TimeSpan.FromSeconds(10);
                        });
                        services.AddControllers().AddApplicationPart(typeof(CustomerObservationFailureController).Assembly);
                    });
                });
                if (kestrel) Factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            }
            catch (Exception primary)
            {
                try { parent.Dispose(); }
                catch (Exception cleanup) { primary.Data["ProbeHostCleanupFailureType"] = cleanup.GetType().Name; }
                throw;
            }
        }

        public HttpClient Client(CustomerDetailAuthorityFixture fixture)
        {
            Factory.StartServer();
            var client = fixture.Client(Factory, "anonymous");
            try
            {
                var server = Factory.Services.GetRequiredService<IServer>();
                if (server is not TestServer)
                {
                    var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
                        ?? throw new InvalidOperationException("Kestrel did not publish a bound address.");
                    var address = new Uri(Assert.Single(addresses), UriKind.Absolute);
                    Assert.Equal(Uri.UriSchemeHttp, address.Scheme);
                    Assert.True(IPAddress.TryParse(address.Host, out var peer) && peer is not null && IPAddress.IsLoopback(peer));
                    Assert.InRange(address.Port, 1, 65535);
                    Assert.Equal("/", address.AbsolutePath);
                    Assert.Empty(address.UserInfo);
                    Assert.Empty(address.Query);
                    Assert.Empty(address.Fragment);
                    client.BaseAddress = address;
                }
                client.Timeout = TimeSpan.FromSeconds(15);
                return client;
            }
            catch { client.Dispose(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            try { await Factory.DisposeAsync(); }
            finally { await parent.DisposeAsync(); }
        }
    }

    private enum ProbeEvent { Probe, FrameworkError, Handled, Unhandled, HealthFailure }

    private sealed class ProbeRecorder : ILoggerProvider
    {
        private readonly ConcurrentQueue<ProbeEvent> values = new();
        private int count;
        public ProbeEvent[] Snapshot => values.ToArray();
        public bool Overflowed => Volatile.Read(ref count) > 16;
        public ILogger CreateLogger(string categoryName) => new ProbeLogger(this,
            categoryName == "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware");
        public void Dispose() { }
        private void Record<TState>(TState state, bool frameworkError)
        {
            if (frameworkError)
            {
                if (Interlocked.Increment(ref count) <= 16) values.Enqueue(ProbeEvent.FrameworkError);
                return;
            }
            if (state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
            var name = fields.FirstOrDefault(field => field.Key == "EventName").Value as string;
            ProbeEvent? value = name switch
            {
                "ObservabilityPipelineProbe" => ProbeEvent.Probe,
                "HandledOperationFailure" => ProbeEvent.Handled,
                "UnhandledRequestFailure" => ProbeEvent.Unhandled,
                "HealthProbeFailure" => ProbeEvent.HealthFailure,
                _ => null
            };
            if (value is not null && Interlocked.Increment(ref count) <= 16) values.Enqueue(value.Value);
        }
        private sealed class ProbeLogger(ProbeRecorder owner, bool framework) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Record(state, framework && logLevel == LogLevel.Error);
        }
    }
}

/// <summary>Exposes controlled returned and thrown failures for production observation tests.</summary>
[ApiController]
[AllowAnonymous]
[Route("/__fixture/customer-observation")]
public sealed class CustomerObservationFailureController : ControllerBase
{
    /// <summary>Returns a controlled service-unavailable response for completed-request observation tests.</summary>
    /// <returns>A 503 response containing the fixed fixture-returned-failure error marker.</returns>
    [HttpGet("returned")]
    public IActionResult Returned() => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "fixture-returned-failure" });
    /// <summary>Throws a controlled private exception for production unhandled-failure observation tests.</summary>
    /// <returns>No action result; the method throws the controlled fixture exception.</returns>
    [HttpGet("throw")]
    public IActionResult Throw() => throw new Exception("fixture-private-error-sentinel");
}
