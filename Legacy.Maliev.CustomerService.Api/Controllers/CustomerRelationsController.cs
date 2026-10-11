using System.Globalization;
using Legacy.Maliev.CustomerService.Api.Authorization;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.CustomerService.Api.Controllers;

/// <summary>Edits only metadata bound to the selected customer and relation kind.</summary>
[ApiController]
[Route("customers/{customerId:int}/relations")]
[Authorize]
[RequirePermission(CustomerPermissions.CustomersUpdate, ResourcePathTemplate = "/customers/{customerId}")]
public sealed class CustomerRelationsController(ICustomerRelationService service) : ControllerBase
{
    /// <summary>Reads the currently bound company and its attachment/metadata state validator.</summary>
    [HttpGet("company/{companyId:int}/edit")]
    [RequirePermission(CustomerPermissions.CompaniesRead, ResourcePathTemplate = "/companies/{companyId}")]
    public Task<ActionResult<CustomerRelationResponse>> GetCompanyAsync(int customerId, int companyId, CancellationToken cancellationToken) =>
        ReadAsync(customerId, CustomerRelationKind.Company, companyId, cancellationToken);

    /// <summary>Reads an absent company binding before explicitly creating its metadata.</summary>
    [HttpGet("company/new")]
    [RequirePermission(CustomerPermissions.CompaniesCreate)]
    public Task<ActionResult<CustomerRelationResponse>> GetNewCompanyAsync(int customerId, CancellationToken cancellationToken) =>
        ReadAsync(customerId, CustomerRelationKind.Company, null, cancellationToken);

    /// <summary>Edits the existing bound company without accepting relation-ID assignments.</summary>
    [HttpPut("company/{companyId:int}/versioned")]
    [RequirePermission(CustomerPermissions.CompaniesUpdate, ResourcePathTemplate = "/companies/{companyId}")]
    public Task<IActionResult> UpdateCompanyAsync(int customerId, int companyId, CustomerCompanyRelationRequest request, CancellationToken cancellationToken) =>
        SaveCompanyAsync(customerId, companyId, request, cancellationToken);

    /// <summary>Creates company metadata and attaches it only when the original missing-binding version matches.</summary>
    [HttpPut("company/new/versioned")]
    [RequirePermission(CustomerPermissions.CompaniesCreate)]
    public Task<IActionResult> CreateCompanyAsync(int customerId, CustomerCompanyRelationRequest request, CancellationToken cancellationToken) =>
        SaveCompanyAsync(customerId, null, request, cancellationToken);

    /// <summary>Reads the currently bound billing or shipping address independently.</summary>
    [HttpGet("{kind:regex(^(billing|shipping)$)}/address/{addressId:int}/edit")]
    [RequirePermission(CustomerPermissions.AddressesRead, ResourcePathTemplate = "/addresses/{addressId}")]
    public Task<ActionResult<CustomerRelationResponse>> GetAddressAsync(int customerId, string kind, int addressId, CancellationToken cancellationToken) =>
        ReadAsync(customerId, Kind(kind), addressId, cancellationToken);

    /// <summary>Reads an absent selected address binding before explicit creation.</summary>
    [HttpGet("{kind:regex(^(billing|shipping)$)}/address/new")]
    [RequirePermission(CustomerPermissions.AddressesCreate)]
    public Task<ActionResult<CustomerRelationResponse>> GetNewAddressAsync(int customerId, string kind, CancellationToken cancellationToken) =>
        ReadAsync(customerId, Kind(kind), null, cancellationToken);

    /// <summary>Edits the bound address without accepting arbitrary metadata or profile identifiers.</summary>
    [HttpPut("{kind:regex(^(billing|shipping)$)}/address/{addressId:int}/versioned")]
    [RequirePermission(CustomerPermissions.AddressesUpdate, ResourcePathTemplate = "/addresses/{addressId}")]
    public Task<IActionResult> UpdateAddressAsync(int customerId, string kind, int addressId, CustomerAddressRelationRequest request, CancellationToken cancellationToken) =>
        SaveAddressAsync(customerId, Kind(kind), addressId, request, cancellationToken);

    /// <summary>Creates and attaches only the selected missing billing or shipping address.</summary>
    [HttpPut("{kind:regex(^(billing|shipping)$)}/address/new/versioned")]
    [RequirePermission(CustomerPermissions.AddressesCreate)]
    public Task<IActionResult> CreateAddressAsync(int customerId, string kind, CustomerAddressRelationRequest request, CancellationToken cancellationToken) =>
        SaveAddressAsync(customerId, Kind(kind), null, request, cancellationToken);

    private async Task<ActionResult<CustomerRelationResponse>> ReadAsync(int customerId, CustomerRelationKind kind, int? relationId, CancellationToken cancellationToken)
    {
        var state = await service.GetAsync(customerId, kind, relationId, cancellationToken);
        if (state is null) return NotFound();
        Response.Headers.ETag = state.Version;
        Response.Headers["X-Customer-ETag"] = $"\"{state.CustomerRevision:x8}\"";
        Response.Headers.CacheControl = "no-store";
        return state.Relation;
    }

    private async Task<IActionResult> SaveCompanyAsync(int customerId, int? relationId, CustomerCompanyRelationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || !CompanyNameLengthAttribute.IsStorable(request.Name)) return BadRequest();
        var refusal = Precondition(out var version, out var revision);
        if (refusal is not null) return refusal;
        return Result(await service.SaveCompanyAsync(customerId, relationId, version!, revision, request, cancellationToken));
    }

    private async Task<IActionResult> SaveAddressAsync(int customerId, CustomerRelationKind kind, int? relationId, CustomerAddressRelationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AddressLine1) || !AddressLine1LengthAttribute.IsStorable(request.AddressLine1) || request.CountryId == 0) return BadRequest();
        var refusal = Precondition(out var version, out var revision);
        if (refusal is not null) return refusal;
        return Result(await service.SaveAddressAsync(customerId, kind, relationId, version!, revision, request, cancellationToken));
    }

    private IActionResult? Precondition(out string? version, out uint revision)
    {
        version = null;
        revision = 0;
        if (!Request.Headers.TryGetValue("If-Match", out var values)) return StatusCode(StatusCodes.Status428PreconditionRequired);
        if (values.Count != 1 || values[0] is not { } candidate || !CustomerRelationVersion.IsValid(candidate)) return BadRequest();
        version = candidate;
        if (!Request.Headers.TryGetValue("X-Customer-If-Match", out var customerValues)) return StatusCode(StatusCodes.Status428PreconditionRequired);
        if (customerValues.Count != 1 || customerValues[0] is not { Length: 10 } customerVersion ||
            customerVersion[0] != '"' || customerVersion[^1] != '"' ||
            !uint.TryParse(customerVersion.AsSpan(1, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out revision)) return BadRequest();
        return null;
    }

    private IActionResult Result(CustomerRelationEditResult result)
    {
        if (result.Outcome == CustomerRelationEditOutcome.NotFound) return NotFound();
        if (result.Outcome == CustomerRelationEditOutcome.Stale) return StatusCode(StatusCodes.Status412PreconditionFailed);
        Response.Headers.ETag = result.Version;
        Response.Headers["X-Customer-ETag"] = result.CustomerVersion;
        Response.Headers["X-Relation-Id"] = result.RelationId?.ToString(CultureInfo.InvariantCulture);
        if (result.BillingVersion is not null) Response.Headers["X-Billing-ETag"] = result.BillingVersion;
        if (result.ShippingVersion is not null) Response.Headers["X-Shipping-ETag"] = result.ShippingVersion;
        return NoContent();
    }

    private static CustomerRelationKind Kind(string value) => value.Equals("billing", StringComparison.OrdinalIgnoreCase)
        ? CustomerRelationKind.Billing : CustomerRelationKind.Shipping;
}
