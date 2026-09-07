using System.Reflection;
using Legacy.Maliev.CustomerService.Api.Controllers;
using Legacy.Maliev.CustomerService.Api.Authorization;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

public sealed class CustomerControllerContractTests
{
    [Theory]
    [InlineData(nameof(CompaniesController.CreateCompanyAsync), CustomerPermissions.CompaniesCreate, null)]
    [InlineData(nameof(CompaniesController.UpdateCompanyAsync), CustomerPermissions.CompaniesUpdate, "/companies/{id}")]
    public void CompanyMutations_PreservePermissionResourceScope(string methodName, string expected, string? resource)
    {
        var permission = Assert.Single(typeof(CompaniesController).GetMethod(methodName)!.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal(expected, permission.Permission);
        Assert.Equal(resource, permission.ResourcePathTemplate);
    }

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
        AssertAction<CustomersController>(nameof(CustomersController.CreateCustomerAsync), null, typeof(HttpPostAttribute));
        AssertAction<CustomersController>(nameof(CustomersController.ProvisionInstantQuotationProfileAsync), "instant-quotation-profile", typeof(HttpPostAttribute));
        AssertAction<CustomersController>(nameof(CustomersController.DeleteCustomerAsync), "{id:int}", typeof(HttpDeleteAttribute));
        AssertAction<CustomersController>(nameof(CustomersController.GetCustomerAsync), "{id:int}", typeof(HttpGetAttribute));
        AssertAction<CustomersController>("GetInternalRemarkAsync", "{id:int}/internal-remark", typeof(HttpGetAttribute));
        AssertAction<CustomersController>(nameof(CustomersController.GetPaginatedAsync), null, typeof(HttpGetAttribute));
        AssertAction<CustomersController>(nameof(CustomersController.UpdateCustomerAsync), "{id:int}", typeof(HttpPutAttribute));
        AssertAction<CustomersController>("UpdateInternalRemarkAsync", "{id:int}/internal-remark", typeof(HttpPutAttribute));
    }

    [Fact]
    public void CustomerApi_DoesNotExposeIdentityOrCredentialOperations()
    {
        var actions = typeof(CustomersController).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(actions, method =>
            method.Name.Contains("Identity", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
            || method.GetCustomAttributes<HttpMethodAttribute>().Any(attribute =>
                attribute.Template?.Contains("identity", StringComparison.OrdinalIgnoreCase) == true
                || attribute.Template?.Contains("password", StringComparison.OrdinalIgnoreCase) == true
                || attribute.Template?.Contains("validate", StringComparison.OrdinalIgnoreCase) == true));
    }

    [Fact]
    public void InternalRemarkActions_ReuseResourceScopedCustomerReadAndUpdatePermissions()
    {
        AssertPermission("GetInternalRemarkAsync", CustomerPermissions.CustomersRead);
        AssertPermission("UpdateInternalRemarkAsync", CustomerPermissions.CustomersUpdate);
    }

    private static void AssertAction<TController>(string methodName, string? template, Type attributeType)
    {
        var method = typeof(TController).GetMethod(methodName);
        Assert.NotNull(method);
        var attribute = Assert.Single(method.GetCustomAttributes(), attributeType.IsInstanceOfType);
        Assert.Equal(template, ((HttpMethodAttribute)attribute).Template);
        Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>());
    }

    private static void AssertPermission(string methodName, string expectedPermission)
    {
        var method = typeof(CustomersController).GetMethod(methodName);
        Assert.NotNull(method);
        var permission = Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal(expectedPermission, permission.Permission);
        Assert.Equal("/customers/{id}", permission.ResourcePathTemplate);
    }
}
