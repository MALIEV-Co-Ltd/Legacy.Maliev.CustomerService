using System.Reflection;
using Legacy.Maliev.CustomerService.Api.Controllers;
using Legacy.Maliev.CustomerService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

public sealed class CustomerControllerContractTests
{
    [Theory]
    [InlineData(typeof(CustomersController), "customers")]
    [InlineData(typeof(AddressesController), "customers/[controller]")]
    [InlineData(typeof(CompaniesController), "customers/[controller]")]
    [InlineData(typeof(EmailsController), "customers/[controller]")]
    public void Controllers_PreserveLegacyBaseRoutesAndRequireAuthentication(Type controller, string route)
    {
        Assert.Equal(route, controller.GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.NotNull(controller.GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public void CustomerActions_PreserveAllLegacyTemplates()
    {
        AssertAction<CustomersController>(nameof(CustomersController.ValidateUserCredentialsAsync), "v1/validate", typeof(HttpPostAttribute));
        AssertAction<CustomersController>(nameof(CustomersController.CreateCustomerAsync), null, typeof(HttpPostAttribute));
        AssertAction<CustomersController>(nameof(CustomersController.DeleteCustomerAsync), "{id:int}", typeof(HttpDeleteAttribute));
        AssertAction<CustomersController>(nameof(CustomersController.GetCustomerAsync), "{id:int}", typeof(HttpGetAttribute));
        AssertAction<CustomersController>(nameof(CustomersController.GetPaginatedAsync), null, typeof(HttpGetAttribute));
        AssertAction<CustomersController>(nameof(CustomersController.UpdateCustomerAsync), "{id:int}", typeof(HttpPutAttribute));
        AssertAction<CustomersController>(nameof(CustomersController.CreateIdentityAsync), "{id:int}/identity/{password?}", typeof(HttpPostAttribute));
        AssertAction<CustomersController>(nameof(CustomersController.GetIdentityAsync), "{id:int}/identity", typeof(HttpGetAttribute));
        AssertAction<CustomersController>(nameof(CustomersController.UpdateIdentityAsync), "{id:int}/identity", typeof(HttpPutAttribute));
        AssertAction<CustomersController>(nameof(CustomersController.DeleteIdentityAsync), "{id:int}/identity", typeof(HttpDeleteAttribute));
    }

    [Fact]
    public void CredentialValidation_IsNoLongerAnonymousAndRequiresLiveCriticalPermission()
    {
        var method = typeof(CustomersController).GetMethod(nameof(CustomersController.ValidateUserCredentialsAsync))!;
        Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        var permission = Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.True(permission.RequireLiveCheck);
        Assert.True(permission.IsCritical);
    }

    [Fact]
    public void IdentityCompatibilityDto_DoesNotExposeCredentialSecrets()
    {
        var names = typeof(CustomerIdentityResponse).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("PasswordHash", names);
        Assert.DoesNotContain("SecurityStamp", names);
        Assert.DoesNotContain("AuthenticatorKey", names);
        Assert.Contains("DatabaseID", names);
        Assert.Contains("Email", names);
    }

    private static void AssertAction<TController>(string methodName, string? template, Type attributeType)
    {
        var method = typeof(TController).GetMethod(methodName)!;
        var attribute = Assert.Single(method.GetCustomAttributes(), attributeType.IsInstanceOfType);
        Assert.Equal(template, ((HttpMethodAttribute)attribute).Template);
        Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>());
    }
}
