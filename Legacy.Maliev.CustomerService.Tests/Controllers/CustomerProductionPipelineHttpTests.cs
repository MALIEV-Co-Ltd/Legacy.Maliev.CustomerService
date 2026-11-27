using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Serializes production pipeline scenarios that share host configuration and fixture state.</summary>
[CollectionDefinition("Customer production pipeline", DisableParallelization = true)]
public sealed class CustomerProductionPipelineHttpTestsCollection;

/// <summary>Exercises production CORS, authentication, and health response behavior through the actual application host.</summary>
/// <param name="fixture">The shared fixture providing customer hosts and disposable persistence for these scenarios.</param>
[Collection("Customer production pipeline")]
public sealed class CustomerProductionPipelineHttpTests(CustomerDetailAuthorityFixture fixture)
    : IClassFixture<CustomerDetailAuthorityFixture>
{
    private const string AllowedOrigin = "https://example.test";

    /// <summary>Verifies configured-origin CORS preflight responses before protected mutation authorization.</summary>
    /// <param name="allowed">Whether the preflight Origin matches the configured allowed origin.</param>
    /// <returns>A task representing completion of the production pipeline HTTP response assertions.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProductionPreflight_ContainsConfiguredOriginBeforeProtectedMutationAuthorization(bool allowed)
    {
        await using var host = NewHost();
        Assert.Equal("Production", host.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Options, "/customers/1/");
        request.Headers.Add("Origin", allowed ? AllowedOrigin : "https://blocked.example.invalid");
        request.Headers.Add("Access-Control-Request-Method", "PUT");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        if (allowed)
        {
            Assert.Equal(AllowedOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
            Assert.Contains("PUT", response.Headers.GetValues("Access-Control-Allow-Methods"));
            var headers = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
            Assert.Contains("authorization", headers, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("content-type", headers, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        }
    }

    /// <summary>Verifies that anonymous mutations return unauthorized with the configured CORS headers needed by browser callers.</summary>
    /// <returns>A task representing completion of the production pipeline HTTP response assertions.</returns>
    [Fact]
    public async Task ProductionAnonymousMutation_ReturnsUnauthorizedWithConfiguredOriginForBrowserErrorHandling()
    {
        await using var host = NewHost();
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Put, "/customers/1/")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Origin", AllowedOrigin);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(AllowedOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
    }

    /// <summary>Verifies that healthy production probes remain anonymous and expose only the approved response shape.</summary>
    /// <param name="probe">The liveness or readiness probe whose anonymous response is checked.</param>
    /// <returns>A task representing completion of the production pipeline HTTP response assertions.</returns>
    [Theory]
    [InlineData("liveness")]
    [InlineData("readiness")]
    public async Task ProductionHealthyProbe_RemainsAnonymousAndUsesApprovedSanitizedWireShape(string probe)
    {
        await using var host = NewHost();
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync($"/customer/{probe}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (probe == "liveness")
        {
            Assert.Equal("Healthy", body);
            return;
        }
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("Healthy", json.RootElement.GetProperty("status").GetString());
        Assert.True(json.RootElement.GetProperty("totalDuration").GetDouble() >= 0);
        Assert.NotEmpty(json.RootElement.GetProperty("checks").EnumerateObject());
        foreach (var check in json.RootElement.GetProperty("checks").EnumerateObject())
        {
            Assert.Equal("Healthy", check.Value.GetProperty("status").GetString());
            Assert.True(check.Value.GetProperty("duration").GetDouble() >= 0);
            foreach (var privateField in new[] { "description", "data", "exception" })
                Assert.False(check.Value.TryGetProperty(privateField, out _));
        }
    }

    private WebApplicationFactory<Program> NewHost() => fixture.Start();
}
