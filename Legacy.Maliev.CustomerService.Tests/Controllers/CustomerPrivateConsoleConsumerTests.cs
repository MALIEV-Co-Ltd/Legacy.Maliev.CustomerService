using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Checks Customer's production-selected private console formatter without changing source data or existing request ownership.</summary>
/// <param name="fixture">The existing production fixture supplying disposable PostgreSQL, Redis, and the unchanged Customer host settings.</param>
[Collection("Customer production pipeline")]
public sealed class CustomerPrivateConsoleConsumerTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    private const string Canary = "customer-private-console-payload-sentinel";
    private const string ProbeName = "CustomerPrivateConsoleProbe";

    /// <summary>Checks actual Program provider selection and refuses arbitrary messages, state, scopes, and successful severities.</summary>
    /// <param name="level">The actual ILogger severity delivered despite the provider-specific Trace rule.</param>
    /// <param name="severity">The expected private severity, or null when the selected formatter must remain quiet.</param>
    /// <returns>A task completing after production DI, retained telemetry services, and formatted-output assertions.</returns>
    [Theory]
    [InlineData(LogLevel.Trace, null)]
    [InlineData(LogLevel.Debug, null)]
    [InlineData(LogLevel.Information, null)]
    [InlineData(LogLevel.Warning, "WARNING")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Critical, "CRITICAL")]
    public async Task ActualProgram_PrivateConsoleRejectsArbitraryTextAndSuccessLevels(LogLevel level, string? severity)
    {
        await using var host = new ConsoleHost(fixture);
        using var client = fixture.Client(host.Factory, "anonymous");
        var services = host.Factory.Services;
        var providers = services.GetServices<ILoggerProvider>().Where(provider => provider is not FormattedRecorder).ToArray();
        Assert.IsType<ConsoleLoggerProvider>(Assert.Single(providers));
        Assert.Equal(PrivateFailureConsoleFormatter.FormatterName,
            services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName);
        Assert.True(services.GetRequiredService<IHostEnvironment>().IsProduction());
        Assert.NotNull(services.GetRequiredService<MeterProvider>());
        Assert.NotNull(services.GetRequiredService<TracerProvider>());
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(ProbeName);
        Assert.True(logger.IsEnabled(LogLevel.Trace));
        var formattedMessages = 0;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["Payload"] = Canary,
            ["Authorization"] = Canary,
            ["service"] = Canary,
            ["traceId"] = Canary,
        });
        var state = new Dictionary<string, object?>
        {
            ["EventName"] = ProbeName,
            ["Operation"] = "PrivateConsoleRegression",
            ["StatusCode"] = 503,
            ["Payload"] = Canary,
            ["Email"] = Canary,
            ["Path"] = "/" + Canary,
            ["Authorization"] = Canary,
            ["service"] = Canary,
            ["deploymentVersion"] = Canary,
            ["traceId"] = Canary,
        };
        logger.Log(level, new EventId(5251), state, new InvalidOperationException(Canary), (_, _) =>
        {
            Interlocked.Increment(ref formattedMessages);
            return Canary;
        });
        Assert.Equal(0, formattedMessages);
        Assert.Equal(1, host.Records.ProbeInputs);
        var lines = host.Records.Lines(ProbeName);
        if (severity is null) Assert.Empty(lines);
        else
        {
            var line = Assert.Single(lines);
            Assert.DoesNotContain(Canary, line, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(line);
            Assert.Equal(severity, json.RootElement.GetProperty("severity").GetString());
            Assert.Equal(ProbeName, json.RootElement.GetProperty("EventName").GetString());
            Assert.Equal("PrivateConsoleRegression", json.RootElement.GetProperty("Operation").GetString());
            Assert.Equal(503, json.RootElement.GetProperty("StatusCode").GetInt32());
            Assert.Equal(typeof(InvalidOperationException).FullName, json.RootElement.GetProperty("exceptionType").GetString());
            foreach (var field in new[] { "Payload", "Email", "Path", "Authorization" })
                Assert.False(json.RootElement.TryGetProperty(field, out _));
            foreach (var field in new[] { "service", "deploymentVersion", "traceId" })
                if (json.RootElement.TryGetProperty(field, out var metadata)) Assert.NotEqual(Canary, metadata.GetString());
        }
        Assert.False(host.Records.Overflowed);
    }

    /// <summary>Checks existing health, authorization, error responses, and single-owner request observations through the selected formatter.</summary>
    /// <returns>A task completing after real HTTP responses, unchanged physical state, and bounded private event assertions.</returns>
    [Fact]
    public async Task ActualProgram_ExistingResponsesAndFailureEventsRemainPrivate()
    {
        await fixture.ResetAsync();
        await using var host = new ConsoleHost(fixture);
        using var client = fixture.Client(host.Factory, "anonymous");
        await fixture.SeedOldAsync(host.Factory);
        var before = await fixture.SnapshotAsync();
        var cache = host.Factory.Services.GetRequiredService<IDistributedCache>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var cacheBefore = await cache.GetAsync("customer:1", deadline.Token);
        Assert.NotNull(cacheBefore);

        using var health = await client.GetAsync("/customer/liveness", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Healthy", await health.Content.ReadAsStringAsync(deadline.Token));
        Assert.True(Guid.TryParseExact(Assert.Single(health.Headers.GetValues("X-Maliev-Health-Instance")), "N", out _));
        using var unauthorized = await client.GetAsync("/customers/1", deadline.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var returned = await client.GetAsync("/__fixture/customer-observation/returned", deadline.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, returned.StatusCode);
        using var returnedJson = JsonDocument.Parse(await returned.Content.ReadAsStringAsync(deadline.Token));
        Assert.Equal("fixture-returned-failure", returnedJson.RootElement.GetProperty("error").GetString());
        using var thrown = await client.GetAsync("/__fixture/customer-observation/throw?payload=" + Canary, deadline.Token);
        Assert.Equal(HttpStatusCode.InternalServerError, thrown.StatusCode);
        var body = await thrown.Content.ReadAsStringAsync(deadline.Token);
        using var thrownJson = JsonDocument.Parse(body);
        Assert.Equal("An internal server error occurred", thrownJson.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, thrownJson.RootElement.GetProperty("details").ValueKind);
        Assert.DoesNotContain("fixture-private-error-sentinel", body, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, body, StringComparison.Ordinal);
        foreach (var response in new[] { unauthorized, returned, thrown })
        {
            Assert.False(response.Headers.Contains("X-Maliev-Health-Instance"));
            Assert.False(response.Headers.Contains("X-Maliev-Diagnostic-Id"));
        }

        Assert.Single(host.Records.Lines("HandledOperationFailure"));
        Assert.Single(host.Records.Lines("UnhandledRequestFailure"));
        Assert.Empty(host.Records.Lines("HealthProbeFailure"));
        foreach (var line in host.Records.AllLines)
        {
            Assert.DoesNotContain(Canary, line, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-private-error-sentinel", line, StringComparison.Ordinal);
        }
        Assert.False(host.Records.Overflowed);
        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.Equal(cacheBefore, await cache.GetAsync("customer:1", deadline.Token));
    }

    private sealed class ConsoleHost : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> parent;
        internal WebApplicationFactory<Program> Factory { get; }
        internal FormattedRecorder Records => Factory.Services.GetServices<ILoggerProvider>().OfType<FormattedRecorder>().Single();

        internal ConsoleHost(CustomerDetailAuthorityFixture fixture)
        {
            parent = fixture.Start();
            try
            {
                Factory = parent.WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("Observability:TracingEnabled", "true");
                    builder.UseSetting("Observability:RuntimeMetricsEnabled", "false");
                    builder.UseSetting("Logging:Console:LogLevel:" + ProbeName, "Trace");
                    builder.ConfigureTestServices(services =>
                    {
                        services.AddLogging(logging => logging.AddFilter<FormattedRecorder>(ProbeName, LogLevel.Trace));
                        services.AddSingleton<ILoggerProvider>(provider => new FormattedRecorder(
                            provider.GetServices<ConsoleFormatter>().Single(formatter => formatter.Name ==
                                provider.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName)));
                        services.Configure<HostOptions>(options =>
                        {
                            options.StartupTimeout = TimeSpan.FromSeconds(20);
                            options.ShutdownTimeout = TimeSpan.FromSeconds(10);
                        });
                        services.AddControllers().AddApplicationPart(typeof(CustomerObservationFailureController).Assembly);
                    });
                });
            }
            catch (Exception primary)
            {
                try { parent.Dispose(); }
                catch (Exception cleanup) { primary.Data["PrivateConsoleHostCleanupFailureType"] = cleanup.GetType().Name; }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await Factory.DisposeAsync(); }
            finally { await parent.DisposeAsync(); }
        }
    }

    private sealed class FormattedRecorder(ConsoleFormatter formatter) : ILoggerProvider
    {
        private readonly ConsoleFormatter selected = formatter;
        private readonly ConcurrentQueue<(string Event, string Line)> records = new();
        private int count;
        private int probeInputs;
        internal int ProbeInputs => Volatile.Read(ref probeInputs);
        internal bool Overflowed => Volatile.Read(ref count) > 128;
        internal IReadOnlyList<string> AllLines => records.Select(record => record.Line).ToArray();
        internal IReadOnlyList<string> Lines(string name) => records.Where(record => record.Event == name).Select(record => record.Line).ToArray();
        public ILogger CreateLogger(string category) => new Recorder(this, category);
        public void Dispose() { }

        private sealed class Recorder(FormattedRecorder owner, string category) : ILogger
        {
            private readonly LoggerExternalScopeProvider scopes = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopes.Push(state);
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> message)
            {
                var name = (state as IEnumerable<KeyValuePair<string, object?>>)?
                    .Take(64).FirstOrDefault(pair => pair.Key == "EventName").Value as string;
                if (name is not ("CustomerPrivateConsoleProbe" or "HandledOperationFailure" or "UnhandledRequestFailure" or "HealthProbeFailure")) return;
                using var writer = new StringWriter();
                var entry = new LogEntry<TState>(level, category, id, state, exception, message);
                if (name == ProbeName) Interlocked.Increment(ref owner.probeInputs);
                owner.selected.Write(in entry, scopes, writer);
                var line = writer.ToString().Trim();
                if (line.Length == 0) return;
                if (Interlocked.Increment(ref owner.count) <= 128) owner.records.Enqueue((name, line));
            }
        }
    }
}
