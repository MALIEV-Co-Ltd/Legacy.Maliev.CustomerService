using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Legacy.Maliev.CustomerService.Application.Models;

/// <summary>The selected customer-owned relation binding.</summary>
public enum CustomerRelationKind
{
    /// <summary>The customer's company.</summary>
    Company,
    /// <summary>The customer's billing address.</summary>
    Billing,
    /// <summary>The customer's shipping address.</summary>
    Shipping,
}

/// <summary>Safe selected relation fields without unrelated customer or identity data.</summary>
public sealed record CustomerRelationResponse(int CustomerId, int? RelationId, CompanyResponse? Company, AddressResponse? Address);

/// <summary>A relation read with the state validator needed for its scoped edit.</summary>
public sealed record CustomerRelationVersionedResponse(CustomerRelationResponse Relation, string Version, uint CustomerRevision);

/// <summary>Scoped company replacement; callers cannot assign relation identifiers or timestamps.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CustomerCompanyRelationRequest(
    [Required, CompanyNameLength] string Name,
    [StringLength(256)] string? TaxNumber,
    [StringLength(256)] string? Registrar);

/// <summary>Scoped address replacement; callers cannot assign relation identifiers or timestamps.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CustomerAddressRelationRequest(
    [StringLength(256)] string? Building,
    [Required, StringLength(256)] string AddressLine1,
    [StringLength(256)] string? AddressLine2,
    [StringLength(256)] string? City,
    [StringLength(256)] string? State,
    [StringLength(256)] string? PostalCode,
    int CountryId);

/// <summary>Outcome of a scoped relation edit.</summary>
public enum CustomerRelationEditOutcome
{
    /// <summary>The relation and attachment were committed.</summary>
    Updated,
    /// <summary>The customer or selected relation binding does not exist.</summary>
    NotFound,
    /// <summary>The customer's attachment or metadata state has changed.</summary>
    Stale,
}

/// <summary>A committed edit version and the customer projections requiring cache invalidation.</summary>
public sealed record CustomerRelationEditResult(CustomerRelationEditOutcome Outcome, string? Version = null, int[]? AffectedCustomerIds = null,
    string? CustomerVersion = null, string? BillingVersion = null, string? ShippingVersion = null, int? RelationId = null);

/// <summary>A potentially committed relation edit that must not be replayed.</summary>
public sealed class CustomerRelationUncertainException(int[] affectedCustomerIds) : Exception("The relation edit commit outcome is uncertain")
{
    /// <summary>The bounded customer projections requiring safe invalidation.</summary>
    public int[] AffectedCustomerIds { get; } = affectedCustomerIds;
}

/// <summary>Preserves caller cancellation after a potentially committed shared-relation write.</summary>
public sealed class CustomerRelationCanceledException(int[] affectedCustomerIds, CancellationToken cancellationToken)
    : OperationCanceledException("The relation edit was canceled during commit", cancellationToken)
{
    /// <summary>The customer projections requiring safe cleanup despite cancellation.</summary>
    public int[] AffectedCustomerIds { get; } = affectedCustomerIds;
}

/// <summary>Constructs a resource-bound selected relation projection and state validator.</summary>
public static class CustomerRelationVersion
{
    /// <summary>Includes selected binding and metadata state; the customer revision is fenced separately.</summary>
    public static CustomerRelationVersionedResponse From(CustomerVersionedResponse customer, CustomerRelationKind kind)
    {
        var profile = customer.Customer;
        var relation = kind switch
        {
            CustomerRelationKind.Company => new CustomerRelationResponse(profile.Id, profile.CompanyId, profile.Company, null),
            CustomerRelationKind.Billing => new CustomerRelationResponse(profile.Id, profile.BillingAddressId, null, profile.BillingAddress),
            CustomerRelationKind.Shipping => new CustomerRelationResponse(profile.Id, profile.ShippingAddressId, null, profile.ShippingAddress),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind,
            relation,
        });
        return new(relation, '"' + Convert.ToHexStringLower(SHA256.HashData(bytes)) + '"', customer.Revision);
    }

    /// <summary>Accepts a single quoted strong state validator produced by this boundary.</summary>
    public static bool IsValid(string version) => version.Length == 66 && version[0] == '"' && version[^1] == '"' &&
        version.AsSpan(1, 64).IndexOfAnyExcept("0123456789abcdef".AsSpan()) < 0;
}
