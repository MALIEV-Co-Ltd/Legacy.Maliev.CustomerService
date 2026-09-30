using System.Data.Common;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Data;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.CustomerService.Tests.Data;

public sealed class QuotationProfileRetryPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Read_RetriesWholeSnapshotAndHonorsCancellation()
    {
        await using var seed = new CustomerDbContext(new DbContextOptionsBuilder<CustomerDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options);
        await seed.Database.MigrateAsync();
        var owner = new Customer { Email = "owner@example.test", FirstName = "Stored" };
        seed.Add(owner);
        await seed.SaveChangesAsync();
        var attempts = new AttemptProbe(failFirstRead: true);
        await using var db = new CustomerDbContext(new DbContextOptionsBuilder<CustomerDbContext>()
            .UseNpgsql(postgres.GetConnectionString(), options => options.EnableRetryOnFailure(2, TimeSpan.Zero, null))
            .AddInterceptors(attempts).Options);
        var store = new QuotationProfileCompletionStore(db);
        var graph = await store.ReadAsync(owner.Id, CancellationToken.None);
        Assert.NotNull(graph);
        Assert.Equal(owner.Id, graph.Customer.Id);
        Assert.Equal(2, attempts.ContextIds.Count);
        Assert.Equal(1, attempts.ReadFaults);
        Assert.Empty(db.ChangeTracker.Entries());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(owner.Id, cancelled.Token));
        Assert.Empty(await seed.QuotationProfileCompletionOperations.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_RetriesRollbackOrLostCommitWithFreshContextAndOneDurableReceipt(bool lostCommit)
    {
        await using var seed = new CustomerDbContext(new DbContextOptionsBuilder<CustomerDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options);
        await seed.Database.MigrateAsync();
        var shared = new Company { Name = string.Empty };
        var owner = new Customer
        {
            Email = "owner@example.test",
            Company = shared,
            BillingAddress = new Address { AddressLine1 = "Stored", CountryId = 764 }
        };
        var other = new Customer { Email = "other@example.test", Company = shared };
        seed.AddRange(owner, other);
        await seed.SaveChangesAsync();
        var graph = await new QuotationProfileCompletionStore(seed).ReadAsync(owner.Id, CancellationToken.None);
        var fault = new CommitFault(lostCommit);
        var attempts = new AttemptProbe();
        await using var db = new CustomerDbContext(new DbContextOptionsBuilder<CustomerDbContext>()
            .UseNpgsql(postgres.GetConnectionString(), options => options.EnableRetryOnFailure(2, TimeSpan.Zero, null))
            .AddInterceptors(fault, attempts).Options);
        var request = new QuotationProfileCompletionRequest(null, null, null, null, "Completed", null, null, null, true);
        var key = Guid.NewGuid();
        var result = await new QuotationProfileCompletionStore(db).CompleteAsync(
            owner.Id, "owner-subject", key, graph!.ETag, request, CancellationToken.None);

        Assert.Equal(200, result.Status);
        Assert.True(result.Receipt!.Changed);
        Assert.Equal(1, fault.FaultCount);
        Assert.Equal(2, attempts.ContextIds.Count);
        Assert.Empty(db.ChangeTracker.Entries());
        seed.ChangeTracker.Clear();
        var saved = await seed.Customers.Include(x => x.Company).SingleAsync(x => x.Id == owner.Id);
        Assert.Equal("Completed", saved.Company!.Name);
        Assert.NotEqual(shared.Id, saved.CompanyId);
        Assert.Equal(string.Empty, (await seed.Customers.Include(x => x.Company).SingleAsync(x => x.Id == other.Id)).Company!.Name);
        Assert.Equal(2, await seed.Companies.CountAsync());
        var receipt = Assert.Single(await seed.QuotationProfileCompletionOperations.ToListAsync());
        Assert.Equal(key, receipt.Key);
        Assert.Equal(result.Receipt.CompletionId, receipt.CompletionId);
        var replay = await new QuotationProfileCompletionStore(db).CompleteAsync(
            owner.Id, "owner-subject", key, graph.ETag, request, CancellationToken.None);
        Assert.Equal(result, replay);
    }

    private sealed class CommitFault(bool lostCommit) : DbTransactionInterceptor
    {
        public int FaultCount { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!lostCommit && FaultCount == 0)
            {
                FaultCount++;
                throw new TimeoutException("Disposable pre-commit retry probe.");
            }
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (lostCommit && FaultCount == 0)
            {
                FaultCount++;
                throw new TimeoutException("Disposable lost-commit acknowledgement probe.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class AttemptProbe(bool failFirstRead = false) : DbCommandInterceptor
    {
        public HashSet<Guid> ContextIds { get; } = [];
        public int ReadFaults { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not null) ContextIds.Add(eventData.Context.ContextId.InstanceId);
            if (failFirstRead && ReadFaults == 0 && command.CommandText.Contains("concat(", StringComparison.Ordinal))
            {
                ReadFaults++;
                throw new TimeoutException("Disposable snapshot read retry probe.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
