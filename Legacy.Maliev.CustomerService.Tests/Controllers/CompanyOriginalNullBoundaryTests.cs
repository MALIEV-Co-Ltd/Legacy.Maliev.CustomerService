using Legacy.Maliev.CustomerService.Api.Controllers;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

/// <summary>Checks that null company bodies retain the original empty bad-request guard before service access.</summary>
public sealed class CompanyOriginalNullBoundaryTests
{
    /// <summary>Checks that a null company-create request produces an empty 400 response without service calls.</summary>
    /// <returns>A task that completes after asserting the original bad-request result and no service calls.</returns>
    [Fact]
    public async Task NullCreate_PreservesOriginalEmptyBadRequestWithoutServiceCalls()
    {
        var service = new Mock<ICustomerService>(MockBehavior.Strict);
        var controller = new CompaniesController(service.Object);

        var result = await controller.CreateCompanyAsync(null!, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<BadRequestResult>(result).StatusCode);
        service.VerifyNoOtherCalls();
    }

    /// <summary>Checks that a null company-update body is rejected before looking up any supplied identifier.</summary>
    /// <param name="companyId">The existing-shaped or maximum Int32 route identifier that must not cause a lookup for a null body.</param>
    /// <returns>A task that completes after asserting the empty bad-request result and no service calls.</returns>
    [Theory]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public async Task NullUpdate_PreservesOriginalEmptyBadRequestBeforeLookup(int companyId)
    {
        var service = new Mock<ICustomerService>(MockBehavior.Strict);
        var controller = new CompaniesController(service.Object);

        var result = await controller.UpdateCompanyAsync(companyId, null!, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<BadRequestResult>(result).StatusCode);
        service.VerifyNoOtherCalls();
    }
}
