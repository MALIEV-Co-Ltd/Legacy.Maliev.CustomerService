using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Data;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.CustomerService.Api;

/// <summary>Commits a keyed profile and its replay result in one PostgreSQL transaction.</summary>
public sealed class CustomerCreateReplayService(CustomerDbContext db, ICustomerService customers)
{
    /// <summary>Creates once, replays the original result, or rejects a reused key.</summary>
    public async Task<CustomerCreateReplayResult> CreateAsync(
        Guid key,
        string actor,
        UpsertCustomerRequest request,
        CancellationToken cancellationToken)
    {
        var actorHash = Hash(actor);
        var requestHash = Hash(JsonSerializer.Serialize(request));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The primary key serializes contenders across processes. A rolled-back
        // winner leaves no row, so the next request may safely become the winner.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"CustomerCreateOperation\" (\"Key\", \"ActorHash\", \"RequestHash\", \"CustomerId\", \"ResponseJson\", \"CreatedAt\") VALUES ({key}, {actorHash}, {requestHash}, {0}, {"{}"}::jsonb, {DateTime.UtcNow}) ON CONFLICT (\"Key\") DO NOTHING",
            cancellationToken);
        if (inserted == 0)
        {
            var existing = await db.CustomerCreateOperations.AsNoTracking()
                .SingleAsync(value => value.Key == key, cancellationToken);
            if (existing.ActorHash != actorHash || existing.RequestHash != requestHash)
            {
                return new CustomerCreateReplayResult(null, ConflictingKey: true);
            }

            var response = JsonSerializer.Deserialize<CustomerResponse>(existing.ResponseJson)
                ?? throw new InvalidOperationException("Committed customer-create result is invalid.");
            return new CustomerCreateReplayResult(response, ConflictingKey: false);
        }

        var created = await customers.CreateCustomerAsync(request, cancellationToken);
        var responseJson = JsonSerializer.Serialize(created);
        await db.CustomerCreateOperations.Where(value => value.Key == key)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.CustomerId, created.Id)
                .SetProperty(value => value.ResponseJson, responseJson), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CustomerCreateReplayResult(created, ConflictingKey: false);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>Customer-create replay outcome; conflict never exposes another actor's result.</summary>
public sealed record CustomerCreateReplayResult(CustomerResponse? Customer, bool ConflictingKey);
