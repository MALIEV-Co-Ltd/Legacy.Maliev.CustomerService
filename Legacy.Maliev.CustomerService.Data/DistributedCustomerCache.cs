using System.Text.Json;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.CustomerService.Data;

/// <summary>
/// Provides short-lived Redis caching for customer lookups while failing open to PostgreSQL when caching is unavailable.
/// </summary>
/// <param name="cache">The distributed cache used to store serialized customer projections.</param>
/// <param name="logger">The logger used to record cache failures that require a PostgreSQL fallback.</param>
public sealed class DistributedCustomerCache(IDistributedCache cache, ILogger<DistributedCustomerCache> logger) : ICustomerCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DistributedCacheEntryOptions EntryOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
        SlidingExpiration = TimeSpan.FromMinutes(2),
    };

    /// <inheritdoc />
    public async Task<CustomerResponse?> GetAsync(int id, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await cache.GetAsync(Key(id), cancellationToken);
            return bytes is null ? null : JsonSerializer.Deserialize<CustomerResponse>(bytes, JsonOptions);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Customer cache read failed; using PostgreSQL");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(CustomerResponse customer, CancellationToken cancellationToken)
    {
        try
        {
            await cache.SetAsync(Key(customer.Id), JsonSerializer.SerializeToUtf8Bytes(customer, JsonOptions), EntryOptions, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Customer cache write failed; continuing without cache");
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(int id, CancellationToken cancellationToken)
    {
        try
        {
            await cache.RemoveAsync(Key(id), cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Customer cache invalidation failed");
        }
    }

    private static string Key(int id) => $"customer:{id}";
}
