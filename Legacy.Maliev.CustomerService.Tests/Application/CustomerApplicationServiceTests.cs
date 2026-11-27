using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Application.Services;
using Legacy.Maliev.CustomerService.Domain;
using Moq;

namespace Legacy.Maliev.CustomerService.Tests.Application;

/// <summary>Checks application-service persistence outcomes, current repository projection, and precisely scoped customer cache invalidation.</summary>
public sealed class CustomerApplicationServiceTests
{
    /// <summary>Checks that updating a tax-only company invalidates every customer referencing that company.</summary>
    /// <returns>A task that completes after asserting a successful update and exactly one invalidation for each linked customer.</returns>
    [Fact]
    public async Task UpdateCompanyAsync_TaxOnlyCompany_InvalidatesAllReferencingCustomers()
    {
        var request = new UpsertCompanyRequest("", "0100000000000", null);
        var repository = new Mock<ICustomerRepository>(MockBehavior.Strict);
        repository.Setup(value => value.GetCustomerIdsForCompanyAsync(23, It.IsAny<CancellationToken>())).ReturnsAsync([7, 8]);
        repository.Setup(value => value.UpdateCompanyAsync(23, request, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var cache = new Mock<ICustomerCache>();
        var service = new CustomerApplicationService(repository.Object, cache.Object);
        Assert.True(await service.UpdateCompanyAsync(23, request, CancellationToken.None));
        cache.Verify(value => value.RemoveAsync(7, It.IsAny<CancellationToken>()), Times.Once);
        cache.Verify(value => value.RemoveAsync(8, It.IsAny<CancellationToken>()), Times.Once);
        cache.VerifyNoOtherCalls();
        repository.VerifyAll();
    }

    /// <summary>Checks that the current repository customer projection is returned without consulting or rewriting stale cache entries.</summary>
    /// <returns>A task that completes after asserting the current response instance, one repository read, and no cache reads or writes.</returns>
    [Fact]
    public async Task GetCustomerAsync_StaleCacheCannotOverrideCurrentPostgreSqlProjection()
    {
        var cached = SampleCustomer() with { FirstName = "Old" };
        var current = SampleCustomer() with { FirstName = "Current" };
        var repository = new Mock<ICustomerRepository>(MockBehavior.Strict);
        repository.Setup(value => value.GetCustomerAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(current);
        var cache = new Mock<ICustomerCache>();
        cache.Setup(value => value.GetAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(cached);
        var service = new CustomerApplicationService(repository.Object, cache.Object);

        var result = await service.GetCustomerAsync(7, CancellationToken.None);

        Assert.Same(current, result);
        Assert.NotSame(cached, result);
        repository.Verify(value => value.GetCustomerAsync(7, It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
        cache.Verify(value => value.GetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        cache.Verify(value => value.SetAsync(It.IsAny<CustomerResponse>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Checks that a successful customer update removes its cached projection.</summary>
    /// <returns>A task that completes after asserting a successful result and one invalidation for the updated customer.</returns>
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

    /// <summary>Checks that versioned updates invalidate the cache only for a confirmed persisted update.</summary>
    /// <param name="outcome">The repository result selecting updated, stale, or missing-customer behavior.</param>
    /// <param name="invalidations">The expected cache removal count: one after an update or zero after stale and not-found outcomes.</param>
    /// <returns>A task that completes after asserting the returned repository outcome and exact cache invalidation count.</returns>
    [Theory]
    [InlineData(CustomerRevisionUpdateResult.Updated, 1)]
    [InlineData(CustomerRevisionUpdateResult.Stale, 0)]
    [InlineData(CustomerRevisionUpdateResult.NotFound, 0)]
    public async Task VersionedUpdate_InvalidatesCacheOnlyAfterPersistedWrite(
        CustomerRevisionUpdateResult outcome, int invalidations)
    {
        var repository = new Mock<ICustomerRepository>(MockBehavior.Strict);
        repository.Setup(value => value.UpdateCustomerIfRevisionAsync(7, SampleRequest(), 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);
        var cache = new Mock<ICustomerCache>(MockBehavior.Strict);
        if (invalidations == 1)
        {
            cache.Setup(value => value.RemoveAsync(7, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        }

        var service = new CustomerApplicationService(repository.Object, cache.Object);
        Assert.Equal(outcome, await service.UpdateCustomerIfRevisionAsync(7, SampleRequest(), 42, CancellationToken.None));
        cache.Verify(value => value.RemoveAsync(7, It.IsAny<CancellationToken>()), Times.Exactly(invalidations));
        repository.VerifyAll();
        cache.VerifyNoOtherCalls();
    }

    /// <summary>Checks that updating an internal remark invalidates the corresponding customer cache.</summary>
    /// <returns>A task that completes after asserting a successful remark update and one customer cache removal.</returns>
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

    /// <summary>Checks that a successful shared address update invalidates every referencing customer.</summary>
    /// <returns>A task that completes after asserting the successful update and exactly one removal for each linked customer.</returns>
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

    /// <summary>Checks that a failed company update leaves all referencing customer caches intact.</summary>
    /// <returns>A task that completes after asserting a false update result and no cache removals.</returns>
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

    /// <summary>Checks that deleting a company invalidates every previously referencing customer.</summary>
    /// <returns>A task that completes after asserting successful deletion and one removal for each linked customer.</returns>
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
