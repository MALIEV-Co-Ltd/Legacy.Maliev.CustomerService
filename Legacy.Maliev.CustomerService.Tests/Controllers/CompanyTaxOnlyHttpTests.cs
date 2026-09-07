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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

public sealed class CompanyTaxOnlyHttpTests
{
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

    internal static async Task<WebApplication> StartAsync(ICustomerService service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(service);
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
                    [new Claim("test-access", identity)], Scheme.Name)), Scheme.Name)));
        }
    }
}
