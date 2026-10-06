using Legacy.Maliev.CustomerService.Application.Models;

namespace Legacy.Maliev.CustomerService.Application.Interfaces;

/// <summary>Scoped relation reads and edits without arbitrary relation-ID assignment.</summary>
public interface ICustomerRelationRepository
{
    /// <summary>Reads the selected existing or absent relation binding and its edit version.</summary>
    Task<CustomerRelationVersionedResponse?> GetAsync(int customerId, CustomerRelationKind kind, int? relationId, CancellationToken cancellationToken);
    /// <summary>Atomically updates or creates the selected company and preserves other profile fields.</summary>
    Task<CustomerRelationEditResult> SaveCompanyAsync(int customerId, int? relationId, string version, uint customerRevision, CustomerCompanyRelationRequest request, CancellationToken cancellationToken);
    /// <summary>Atomically updates or creates only the selected billing or shipping address binding.</summary>
    Task<CustomerRelationEditResult> SaveAddressAsync(int customerId, CustomerRelationKind kind, int? relationId, string version, uint customerRevision, CustomerAddressRelationRequest request, CancellationToken cancellationToken);
}

/// <summary>Scoped relation application boundary with shared-projection cache invalidation.</summary>
public interface ICustomerRelationService : ICustomerRelationRepository
{
}
