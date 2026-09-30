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
    bool ShipToBillingAddress);

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
