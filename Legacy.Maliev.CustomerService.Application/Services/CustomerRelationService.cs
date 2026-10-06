using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;

namespace Legacy.Maliev.CustomerService.Application.Services;

/// <summary>Coordinates scoped relation persistence and bounded cache cleanup.</summary>
public sealed class CustomerRelationService(ICustomerRelationRepository repository, ICustomerCache cache) : ICustomerRelationService
{
    /// <inheritdoc />
    public Task<CustomerRelationVersionedResponse?> GetAsync(int customerId, CustomerRelationKind kind, int? relationId, CancellationToken cancellationToken) =>
        repository.GetAsync(customerId, kind, relationId, cancellationToken);

    /// <inheritdoc />
    public Task<CustomerRelationEditResult> SaveCompanyAsync(int customerId, int? relationId, string version, uint customerRevision, CustomerCompanyRelationRequest request, CancellationToken cancellationToken) =>
        SaveAsync(customerId, () => repository.SaveCompanyAsync(customerId, relationId, version, customerRevision, request, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<CustomerRelationEditResult> SaveAddressAsync(int customerId, CustomerRelationKind kind, int? relationId, string version, uint customerRevision, CustomerAddressRelationRequest request, CancellationToken cancellationToken) =>
        SaveAsync(customerId, () => repository.SaveAddressAsync(customerId, kind, relationId, version, customerRevision, request, cancellationToken), cancellationToken);

    private async Task<CustomerRelationEditResult> SaveAsync(int customerId, Func<Task<CustomerRelationEditResult>> save, CancellationToken cancellationToken)
    {
        CustomerRelationEditResult result;
        try { result = await save(); }
        catch (CustomerRelationUncertainException uncertain)
        {
            await InvalidateAsync(uncertain.AffectedCustomerIds);
            throw;
        }
        catch (CustomerRelationCanceledException canceled) when (cancellationToken.IsCancellationRequested)
        {
            try { await InvalidateAsync(canceled.AffectedCustomerIds); } catch (Exception) { }
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The target is always known; shared affected rows are supplied by uncertainty exceptions.
            try { await cache.RemoveAsync(customerId, CancellationToken.None); } catch (Exception) { }
            throw;
        }
        if (result.Outcome == CustomerRelationEditOutcome.Updated)
            await InvalidateAsync(result.AffectedCustomerIds ?? [customerId]);
        return result;
    }

    private async Task InvalidateAsync(IEnumerable<int> customerIds)
    {
        foreach (var id in customerIds.Distinct()) await cache.RemoveAsync(id, CancellationToken.None);
    }
}
