using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Serializes unhealthy readiness scenarios that share production host and fixture state.</summary>
[CollectionDefinition("Customer unhealthy readiness", DisableParallelization = true)]
public sealed class CustomerUnhealthyReadinessHttpTestsCollection;

/// <summary>Exercises production readiness failure sanitization independently from liveness.</summary>
/// <param name="fixture">The shared fixture providing customer hosts and disposable persistence for these scenarios.</param>
[Collection("Customer unhealthy readiness")]
public sealed class CustomerUnhealthyReadinessHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    /// <summary>Verifies anonymous sanitized readiness failures return 503 while liveness remains healthy.</summary>
    /// <param name="throwFailure">Whether the readiness check throws; false returns an unhealthy result carrying private diagnostics.</param>
    /// <returns>A task representing completion of the readiness sanitization and liveness assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionFailedDependency_ReturnsAnonymousSanitized503AndKeepsLivenessHealthy(bool throwFailure)
    {
        const string descriptionMarker = "private-description-marker";
        const string exceptionMarker = "private-exception-marker";
        const string dataMarker = "private-data-marker";
        await using var baseHost = fixture.Start();
        await using var host = baseHost.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["CORS:AllowedOrigins:0"] = "https://example.test" }));
            builder.ConfigureTestServices(services => services.AddHealthChecks().AddCheck("controlled-readiness", () =>
            {
                if (throwFailure) throw new InvalidOperationException(exceptionMarker);
                return HealthCheckResult.Unhealthy(descriptionMarker, new InvalidOperationException(exceptionMarker),
                    new Dictionary<string, object> { ["private-diagnostic"] = dataMarker });
            }));
        });
        Assert.Equal("Production", host.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        using var response = await client.GetAsync("/customer/readiness");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        foreach (var marker in new[] { descriptionMarker, exceptionMarker, dataMarker, "private-diagnostic" })
            Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(new[] { "checks", "status", "totalDuration" },
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("Unhealthy", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("totalDuration").GetDouble() >= 0);
        var failedCheck = root.GetProperty("checks").GetProperty("controlled-readiness");
        Assert.Equal("Unhealthy", failedCheck.GetProperty("status").GetString());
        foreach (var check in root.GetProperty("checks").EnumerateObject())
        {
            Assert.Equal(new[] { "duration", "status" },
                check.Value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
            Assert.True(check.Value.GetProperty("duration").GetDouble() >= 0);
        }
        using var liveness = await client.GetAsync("/customer/liveness");
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal("Healthy", await liveness.Content.ReadAsStringAsync());
    }
}
