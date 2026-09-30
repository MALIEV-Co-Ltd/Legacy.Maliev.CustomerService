using System.ComponentModel.DataAnnotations;

namespace Legacy.Maliev.CustomerService.Application.Models;

/// <summary>Non-authoritative candidates used only to fill empty fields in the token owner's profile.</summary>
public sealed record QuotationProfileCompletionRequest(
    [StringLength(50)] string? FirstName,
    [StringLength(50)] string? LastName,
    [StringLength(50)] string? Telephone,
    [StringLength(50)] string? Mobile,
    [StringLength(50)] string? Company,
    [RegularExpression("^[0-9]{13}$")] string? TaxNumber,
    QuotationProfileCompletionAddress? Billing,
    QuotationProfileCompletionAddress? Shipping,
    bool ShipToBillingAddress,
    [StringLength(20)] string? TaxBranch = null,
    [StringLength(5)] string? TaxBranchCode = null) : IValidatableObject
{
    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var branch = string.IsNullOrWhiteSpace(TaxBranch) ? null : TaxBranch.Trim();
        var code = string.IsNullOrWhiteSpace(TaxBranchCode) ? null : TaxBranchCode;
        if (branch is null && code is null) yield break;
        if (string.IsNullOrWhiteSpace(TaxNumber) ||
            !(string.Equals(branch, "head-office", StringComparison.OrdinalIgnoreCase) && code is null ||
              string.Equals(branch, "branch", StringComparison.OrdinalIgnoreCase) && code is { Length: 5 } && code.All(character => character is >= '0' and <= '9')))
            yield return new ValidationResult("Tax branch requires a tax ID and either head-office without code or branch with five ASCII digits.", [nameof(TaxBranch), nameof(TaxBranchCode)]);
    }
}

/// <summary>Bounded missing-address candidates; existing positive country identifiers are never replaced.</summary>
public sealed record QuotationProfileCompletionAddress(
    [StringLength(256)] string? Building,
    [StringLength(256)] string? AddressLine1,
    [StringLength(256)] string? AddressLine2,
    [StringLength(256)] string? City,
    [StringLength(256)] string? State,
    [StringLength(20)] string? PostalCode,
    [Range(1, int.MaxValue)] int CountryId);

/// <summary>PII-free durable completion receipt.</summary>
public sealed record QuotationProfileCompletionReceipt(int CustomerId, Guid CompletionId, bool Changed);
