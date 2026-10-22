using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Data;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Legacy.Maliev.CustomerService.Tests.Caching;

/// <summary>Checks customer cache serialization, scoped keys, fail-open read errors, and cancellation propagation.</summary>
public sealed class DistributedCustomerCacheTests
{
    /// <summary>Checks that an underlying Redis read failure is treated as a customer cache miss.</summary>
    /// <returns>A task that completes after asserting a null cache result after the controlled read exception.</returns>
    [Fact]
    public async Task GetAsync_RedisFailure_FailsOpen()
    {
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(value => value.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("redis unavailable"));
        var cache = new DistributedCustomerCache(distributed.Object, NullLogger<DistributedCustomerCache>.Instance);

        var result = await cache.GetAsync(42, CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>Checks a customer response round-trip through serialized bytes under its customer-specific key.</summary>
    /// <returns>A task that completes after asserting equality of the original and retrieved customer response.</returns>
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

    /// <summary>Checks that Redis read cancellation propagates to the customer cache caller.</summary>
    /// <returns>A task that completes after asserting propagation of OperationCanceledException from the cache read.</returns>
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

    /// <summary>Checks that Redis write cancellation propagates to the customer cache caller.</summary>
    /// <returns>A task that completes after asserting propagation of OperationCanceledException from the cache write.</returns>
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

    /// <summary>Checks that Redis removal cancellation propagates to the customer cache caller.</summary>
    /// <returns>A task that completes after asserting propagation of OperationCanceledException from the cache removal.</returns>
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
