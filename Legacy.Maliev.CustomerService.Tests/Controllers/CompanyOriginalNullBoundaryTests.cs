using Legacy.Maliev.CustomerService.Api.Controllers;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Legacy.Maliev.CustomerService.Tests.Controllers;

public sealed class CompanyOriginalNullBoundaryTests
{
    [Fact]
    public async Task NullCreate_PreservesOriginalEmptyBadRequestWithoutServiceCalls()
    {
        var service = new Mock<ICustomerService>(MockBehavior.Strict);
        var controller = new CompaniesController(service.Object);

        var result = await controller.CreateCompanyAsync(null!, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<BadRequestResult>(result).StatusCode);
        service.VerifyNoOtherCalls();
    }

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
