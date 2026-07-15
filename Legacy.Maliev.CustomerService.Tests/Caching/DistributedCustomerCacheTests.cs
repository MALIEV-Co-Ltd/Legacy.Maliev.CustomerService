using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Data;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Legacy.Maliev.CustomerService.Tests.Caching;

public sealed class DistributedCustomerCacheTests
{
    [Fact]
    public async Task GetAsync_RedisFailure_FailsOpen()
    {
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(value => value.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("redis unavailable"));
        var cache = new DistributedCustomerCache(distributed.Object, NullLogger<DistributedCustomerCache>.Instance);

        var result = await cache.GetAsync(42, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SetAndGet_RoundTripsCustomerWithScopedKey()
    {
        byte[]? stored = null;
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(value => value.SetAsync("customer:42", It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((_, bytes, _, _) => stored = bytes)
            .Returns(Task.CompletedTask);
        distributed.Setup(value => value.GetAsync("customer:42", It.IsAny<CancellationToken>())).ReturnsAsync(() => stored);
        var cache = new DistributedCustomerCache(distributed.Object, NullLogger<DistributedCustomerCache>.Instance);
        var customer = new CustomerResponse(42, "Ada", "Lovelace", "Ada Lovelace", null, null, null, "ada@example.com", null, null, null, null, null, null, null, null, null);

        await cache.SetAsync(customer, CancellationToken.None);
        var result = await cache.GetAsync(42, CancellationToken.None);

        Assert.Equal(customer, result);
    }
}
