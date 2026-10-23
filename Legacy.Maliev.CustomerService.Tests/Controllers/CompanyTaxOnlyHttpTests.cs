using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Legacy.Maliev.CustomerService.Api.Controllers;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Exercises company validation, tax-only requests, response casing, and permission boundaries with a strict service mock.</summary>
public sealed class CompanyTaxOnlyHttpTests
{
    /// <summary>Verifies that direct company controller calls reject a null name without relying on MVC validation.</summary>
    /// <returns>A task representing completion of the company HTTP validation and service-call assertions.</returns>
    [Fact]
    public async Task DirectController_NullName_DoesNotDependOnMvcValidation()
    {
        var service = new Mock<ICustomerService>(MockBehavior.Strict);
        var controller = new CompaniesController(service.Object);
        var request = new UpsertCompanyRequest(null!, "0100000000000", null);
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestResult>(await controller.CreateCompanyAsync(request, CancellationToken.None));
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestResult>(await controller.UpdateCompanyAsync(23, request, CancellationToken.None));
        service.VerifyNoOtherCalls();
    }

    /// <summary>Verifies accepted company creation preserves PascalCase fields, omitted null values, and the named resource location.</summary>
    /// <param name="name">The literal company name submitted by the scenario.</param>
    /// <param name="tax">The submitted tax number, or null when no tax number is supplied.</param>
    /// <returns>A task representing completion of the company HTTP validation and service-call assertions.</returns>
    [Theory]
    [InlineData("", "0100000000000")]
    [InlineData("  ", "0100000000000")]
    [InlineData("บริษัทตัวอย่าง", null)]
    public async Task Create_ValidCompany_PreservesPascalCaseAndNamedLocation(string name, string? tax)
    {
        var service = new Mock<ICustomerService>(MockBehavior.Strict);
        service.Setup(value => value.CreateCompanyAsync(new(name, tax, null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompanyResponse(23, name, tax, null, null, null));
        await using var app = await StartAsync(service.Object);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", "allowed");
        using var response = await client.PostAsync("/customers/companies", Body(name, tax));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("http://localhost/customers/Companies/23", response.Headers.Location?.AbsoluteUri);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(23, json.RootElement.GetProperty("Id").GetInt32());
        Assert.Equal(name, json.RootElement.GetProperty("Name").GetString());
        Assert.False(json.RootElement.TryGetProperty("name", out _));
        Assert.False(json.RootElement.TryGetProperty("Registrar", out _));
        if (tax is null) Assert.False(json.RootElement.TryGetProperty("TaxNumber", out _));
        else Assert.Equal(tax, json.RootElement.GetProperty("TaxNumber").GetString());
        service.VerifyAll();
    }

    /// <summary>Verifies tax-only company updates retain the service outcome as either no-content or not-found.</summary>
    /// <param name="found">Whether the service reports the company update target as present.</param>
    /// <param name="expected">The expected no-content or not-found response for the service outcome.</param>
    /// <returns>A task representing completion of the company HTTP validation and service-call assertions.</returns>
    [Theory]
    [InlineData(true, HttpStatusCode.NoContent)]
    [InlineData(false, HttpStatusCode.NotFound)]
    public async Task Update_TaxOnlyCompany_PreservesStatus(bool found, HttpStatusCode expected)
    {
        var service = new Mock<ICustomerService>(MockBehavior.Strict);
        service.Setup(value => value.UpdateCompanyAsync(23, new("", "0100000000000", null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(found);
        await using var app = await StartAsync(service.Object);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", "allowed");
        using var response = await client.PutAsync("/customers/companies/23", Body("", "0100000000000"));
        Assert.Equal(expected, response.StatusCode);
        service.VerifyAll();
    }

    /// <summary>Verifies invalid company bodies are refused on create and update before calling the service.</summary>
    /// <param name="json">The invalid company body submitted to both create and update.</param>
    /// <returns>A task representing completion of the company HTTP validation and service-call assertions.</returns>
    [Theory]
    [InlineData("{\"Name\":\"\",\"TaxNumber\":null}")]
    [InlineData("{\"Name\":\"  \",\"TaxNumber\":\"  \"}")]
    [InlineData("{\"Name\":\"\",\"Registrar\":\"Registrar only\"}")]
    [InlineData("{\"Name\":null,\"TaxNumber\":\"0100000000000\"}")]
    [InlineData("{\"TaxNumber\":\"0100000000000\"}")]
    public async Task CreateAndUpdate_InvalidCompany_RejectBeforeService(string json)
    {
        var service = new Mock<ICustomerService>(MockBehavior.Strict);
        await using var app = await StartAsync(service.Object);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", "allowed");
        using var created = await client.PostAsync("/customers/companies", new StringContent(json, Encoding.UTF8, "application/json"));
        using var updated = await client.PutAsync("/customers/companies/23", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);
        service.VerifyNoOtherCalls();
    }

    /// <summary>Verifies unauthorized company create and update requests never call the service.</summary>
    /// <param name="identity">The test identity header value; null omits the header when supported by the scenario.</param>
    /// <param name="expected">The expected HTTP status for the supplied scenario.</param>
    /// <returns>A task representing completion of the company HTTP validation and service-call assertions.</returns>
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    public async Task CreateAndUpdate_WithoutPermission_DoNotCallService(string? identity, HttpStatusCode expected)
    {
        var service = new Mock<ICustomerService>(MockBehavior.Strict);
        await using var app = await StartAsync(service.Object);
        using var client = app.GetTestClient();
        if (identity is not null) client.DefaultRequestHeaders.Add("Test-Identity", identity);
        using var created = await client.PostAsync("/customers/companies", Body("", "0100000000000"));
        using var updated = await client.PutAsync("/customers/companies/23", Body("", "0100000000000"));
        Assert.Equal(expected, created.StatusCode);
        Assert.Equal(expected, updated.StatusCode);
        service.VerifyNoOtherCalls();
    }

    private static StringContent Body(string name, string? tax) =>
        new(JsonSerializer.Serialize(new { Name = name, TaxNumber = tax }), Encoding.UTF8, "application/json");

    internal static async Task<WebApplication> StartAsync(ICustomerService? service = null, string? customerConnectionString = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (service is not null)
        {
            builder.Services.AddSingleton(service);
        }
        else if (customerConnectionString is not null)
        {
            builder.Services.AddDbContext<Legacy.Maliev.CustomerService.Data.CustomerDbContext>(options => options.UseNpgsql(customerConnectionString));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped<Legacy.Maliev.CustomerService.Application.Interfaces.ICustomerRepository, Legacy.Maliev.CustomerService.Data.CustomerRepository>();
            builder.Services.AddScoped<Legacy.Maliev.CustomerService.Application.Interfaces.ICustomerCache, NoOpCustomerCache>();
            builder.Services.AddScoped<ICustomerService, Legacy.Maliev.CustomerService.Application.Services.CustomerApplicationService>();
            builder.Services.AddScoped<Legacy.Maliev.CustomerService.Api.CustomerCreateReplayService>();
        }
        builder.Services.AddControllers().AddApplicationPart(typeof(CompaniesController).Assembly).AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = null;
            options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            foreach (var permission in new[] { typeof(CompaniesController), typeof(CustomersController), typeof(AddressesController), typeof(EmailsController) }
                .SelectMany(type => type.GetMethods()).SelectMany(method => method.GetCustomAttributes<RequirePermissionAttribute>()))
                options.AddPolicy(permission.Policy!, policy => policy.RequireAuthenticatedUser().RequireClaim("test-access", "allowed"));
        });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = Request.Headers["Test-Identity"].ToString();
            return Task.FromResult(string.IsNullOrEmpty(identity) ? AuthenticateResult.NoResult() :
                AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("test-access", identity), new Claim(ClaimTypes.NameIdentifier, "test-employee")], Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class NoOpCustomerCache : ICustomerCache
    {
        public Task<CustomerResponse?> GetAsync(int id, CancellationToken cancellationToken) => Task.FromResult<CustomerResponse?>(null);
        public Task SetAsync(CustomerResponse customer, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(int id, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
