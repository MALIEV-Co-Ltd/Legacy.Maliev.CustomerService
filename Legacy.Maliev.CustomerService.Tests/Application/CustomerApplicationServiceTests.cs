using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Application.Services;
using Legacy.Maliev.CustomerService.Domain;
using Moq;

namespace Legacy.Maliev.CustomerService.Tests.Application;

public sealed class CustomerApplicationServiceTests
{
    [Fact]
    public async Task GetCustomerAsync_CacheHit_DoesNotQueryPostgreSql()
    {
        var cached = SampleCustomer();
        var repository = new Mock<ICustomerRepository>(MockBehavior.Strict);
        var cache = new Mock<ICustomerCache>();
        cache.Setup(value => value.GetAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(cached);
        var service = new CustomerApplicationService(repository.Object, cache.Object);

        var result = await service.GetCustomerAsync(7, CancellationToken.None);

        Assert.Same(cached, result);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateCustomerAsync_Success_InvalidatesCustomerCache()
    {
        var repository = new Mock<ICustomerRepository>();
        repository.Setup(value => value.UpdateCustomerAsync(7, It.IsAny<UpsertCustomerRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var cache = new Mock<ICustomerCache>();
        var service = new CustomerApplicationService(repository.Object, cache.Object);

        var result = await service.UpdateCustomerAsync(7, SampleRequest(), CancellationToken.None);

        Assert.True(result);
        cache.Verify(value => value.RemoveAsync(7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateInternalRemarkAsync_Success_InvalidatesCustomerCache()
    {
        var repository = new Mock<ICustomerRepository>();
        repository.Setup(value => value.UpdateInternalRemarkAsync(
                7,
                It.IsAny<UpdateCustomerInternalRemarkRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var cache = new Mock<ICustomerCache>();
        var service = new CustomerApplicationService(repository.Object, cache.Object);

        var result = await service.UpdateInternalRemarkAsync(
            7,
            new UpdateCustomerInternalRemarkRequest("Employee only"),
            CancellationToken.None);

        Assert.True(result);
        cache.Verify(value => value.RemoveAsync(7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAddressAsync_Success_InvalidatesEveryReferencingCustomer()
    {
        var repository = new Mock<ICustomerRepository>();
        repository.Setup(value => value.GetCustomerIdsForAddressAsync(13, It.IsAny<CancellationToken>()))
            .ReturnsAsync([7, 8]);
        repository.Setup(value => value.UpdateAddressAsync(13, It.IsAny<UpsertAddressRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var cache = new Mock<ICustomerCache>();
        var service = new CustomerApplicationService(repository.Object, cache.Object);

        var result = await service.UpdateAddressAsync(
            13,
            new UpsertAddressRequest(null, "1 Legacy Road", null, "Bangkok", null, "10110", 764),
            CancellationToken.None);

        Assert.True(result);
        cache.Verify(value => value.RemoveAsync(7, It.IsAny<CancellationToken>()), Times.Once);
        cache.Verify(value => value.RemoveAsync(8, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateCompanyAsync_Failure_DoesNotInvalidateReferencingCustomers()
    {
        var repository = new Mock<ICustomerRepository>();
        repository.Setup(value => value.GetCustomerIdsForCompanyAsync(21, It.IsAny<CancellationToken>()))
            .ReturnsAsync([7]);
        repository.Setup(value => value.UpdateCompanyAsync(21, It.IsAny<UpsertCompanyRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var cache = new Mock<ICustomerCache>();
        var service = new CustomerApplicationService(repository.Object, cache.Object);

        var result = await service.UpdateCompanyAsync(
            21,
            new UpsertCompanyRequest("MALIEV", null, null),
            CancellationToken.None);

        Assert.False(result);
        cache.Verify(value => value.RemoveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteCompanyAsync_Success_InvalidatesEveryReferencingCustomer()
    {
        var repository = new Mock<ICustomerRepository>();
        repository.Setup(value => value.GetCustomerIdsForCompanyAsync(21, It.IsAny<CancellationToken>()))
            .ReturnsAsync([7, 8]);
        repository.Setup(value => value.DeleteCompanyAsync(21, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var cache = new Mock<ICustomerCache>();
        var service = new CustomerApplicationService(repository.Object, cache.Object);

        var result = await service.DeleteCompanyAsync(21, CancellationToken.None);

        Assert.True(result);
        cache.Verify(value => value.RemoveAsync(7, It.IsAny<CancellationToken>()), Times.Once);
        cache.Verify(value => value.RemoveAsync(8, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static CustomerResponse SampleCustomer() => new(7, "Ada", "Lovelace", "Ada Lovelace", null, null, null, "ada@example.com", null, null, null, null, null, null, null, null, null);
    private static UpsertCustomerRequest SampleRequest() => new("Ada", "Lovelace", null, null, null, "ada@example.com", null, null, null, null);
}
