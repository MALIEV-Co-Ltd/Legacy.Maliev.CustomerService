using Legacy.Maliev.CustomerService.Application.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Checks internal remark validation for nullable, empty, maximum-length, and oversized record inputs.</summary>
public sealed class CustomerInternalRemarkValidationTests
{
    /// <summary>Checks that MVC accepts remarks through 4000 characters and records one error for 4001 characters without throwing.</summary>
    /// <param name="length">The remark length to generate, or null to validate a null remark.</param>
    /// <param name="expectedValid">Whether MVC model state should remain valid for the selected remark length.</param>
    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(4000, true)]
    [InlineData(4001, false)]
    public void InternalRemark_MvcValidation_EnforcesBoundWithoutThrowing(int? length, bool expectedValid)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        using var provider = services.BuildServiceProvider();
        var context = new ActionContext(
            new DefaultHttpContext { RequestServices = provider },
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary());
        var request = new UpdateCustomerInternalRemarkRequest(
            length is { } count ? new string('x', count) : null);

        provider.GetRequiredService<IObjectModelValidator>().Validate(context, null, string.Empty, request);

        Assert.Equal(expectedValid, context.ModelState.IsValid);
        if (!expectedValid)
        {
            Assert.Single(context.ModelState[nameof(request.InternalRemark)]!.Errors);
        }
    }
}
