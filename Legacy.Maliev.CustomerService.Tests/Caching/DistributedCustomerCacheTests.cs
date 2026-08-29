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

    [Fact]
    public async Task GetAsync_WhenRedisCancels_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(value => value.GetAsync(It.IsAny<string>(), cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var cache = new DistributedCustomerCache(distributed.Object, NullLogger<DistributedCustomerCache>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.GetAsync(42, cancellation.Token));
    }

    [Fact]
    public async Task SetAsync_WhenRedisCancels_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(value => value.SetAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var cache = new DistributedCustomerCache(distributed.Object, NullLogger<DistributedCustomerCache>.Instance);
        var customer = new CustomerResponse(42, "Ada", "Lovelace", "Ada Lovelace", null, null, null, "ada@example.com", null, null, null, null, null, null, null, null, null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.SetAsync(customer, cancellation.Token));
    }

    [Fact]
    public async Task RemoveAsync_WhenRedisCancels_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(value => value.RemoveAsync(It.IsAny<string>(), cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var cache = new DistributedCustomerCache(distributed.Object, NullLogger<DistributedCustomerCache>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.RemoveAsync(42, cancellation.Token));
    }
}
