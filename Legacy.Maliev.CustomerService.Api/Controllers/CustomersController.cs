using Legacy.Maliev.CustomerService.Api.Authorization;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Api;
using System.Security.Claims;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.CustomerService.Api.Controllers;

/// <summary>Preserves the legacy customer profile contract without taking ownership of authentication identities.</summary>
/// <param name="service">The customer profile application service.</param>
[ApiController]
[Route("customers")]
[Authorize]
public sealed class CustomersController(ICustomerService service) : ControllerBase
{
    /// <summary>
    /// Creates a legacy customer profile from the supplied contact details.
    /// </summary>
    /// <param name="request">The customer profile details to persist.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The created customer, or a bad-request response when required profile data is invalid.</returns>
    [HttpPost]
    [RequirePermission(CustomerPermissions.CustomersCreate)]
    public async Task<IActionResult> CreateCustomerAsync(UpsertCustomerRequest request, CancellationToken cancellationToken)
    {
        if (!Valid(request)) return BadRequest("Customer data is required");
        if (Request.Headers.TryGetValue("Idempotency-Key", out var header))
        {
            if (header.Count != 1 || !Guid.TryParse(header[0], out var key) || key == Guid.Empty)
            {
                return BadRequest("A single non-empty GUID Idempotency-Key is required");
            }

            var actorClaim = User.FindFirst(ClaimTypes.NameIdentifier)
                ?? User.FindFirst("sub")
                ?? User.FindFirst("client_id");
            if (actorClaim is null || string.IsNullOrWhiteSpace(actorClaim.Value)) return Forbid();

            var result = await HttpContext.RequestServices.GetRequiredService<CustomerCreateReplayService>()
                .CreateAsync(key, $"{actorClaim.Issuer}:{actorClaim.Value}", request, cancellationToken);
            if (result.ConflictingKey) return Conflict("Idempotency-Key was already used for a different request");
            return CreatedAtRoute("GetCustomer", new { id = result.Customer!.Id }, result.Customer);
        }

        var customer = await service.CreateCustomerAsync(request, cancellationToken);
        return CreatedAtRoute("GetCustomer", new { id = customer.Id }, customer);
    }

    /// <summary>Atomically selects or creates the complete customer profile used by instant quotation fulfillment.</summary>
    /// <param name="request">The customer, company, and billing/shipping details.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The selected customer identifier and whether a new profile was created.</returns>
    [HttpPost("instant-quotation-profile")]
    [RequirePermission(CustomerPermissions.CustomersCreate)]
    public async Task<ActionResult<InstantQuotationCustomerProfileResult>> ProvisionInstantQuotationProfileAsync(
        InstantQuotationCustomerProfileRequest request,
        CancellationToken cancellationToken)
    {
        if (!Valid(request))
        {
            return BadRequest("Complete instant-quotation customer and billing data is required");
        }

        return await service.ProvisionInstantQuotationProfileAsync(request, cancellationToken);
    }

    /// <summary>
    /// Deletes a legacy customer profile by its identifier.
    /// </summary>
    /// <param name="id">The unique identifier of the customer to delete.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>A no-content response on success, or a not-found response when the customer does not exist.</returns>
    [HttpDelete("{id:int}")]
    [RequirePermission(CustomerPermissions.CustomersDelete, ResourcePathTemplate = "/customers/{id}", IsCritical = true)]
    public async Task<IActionResult> DeleteCustomerAsync(int id, CancellationToken cancellationToken) =>
        await service.DeleteCustomerAsync(id, cancellationToken) ? NoContent() : NotFound();

    /// <summary>
    /// Retrieves a legacy customer profile by its identifier.
    /// </summary>
    /// <param name="id">The unique identifier of the customer to retrieve.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The matching customer profile, or a not-found response when it does not exist.</returns>
    [HttpGet("{id:int}", Name = "GetCustomer")]
    [RequirePermission(CustomerPermissions.CustomersRead, ResourcePathTemplate = "/customers/{id}")]
    public async Task<ActionResult<CustomerResponse>> GetCustomerAsync(int id, CancellationToken cancellationToken)
    {
        var customer = await service.GetCustomerAsync(id, cancellationToken);
        return customer is null ? NotFound() : customer;
    }

    /// <summary>Retrieves an uncached profile and its opaque edit revision in one database projection.</summary>
    [HttpGet("{id:int}/versioned")]
    [RequirePermission(CustomerPermissions.CustomersRead, ResourcePathTemplate = "/customers/{id}")]
    public async Task<ActionResult<CustomerResponse>> GetCustomerVersionedAsync(int id, CancellationToken cancellationToken)
    {
        var customer = await service.GetCustomerVersionedAsync(id, cancellationToken);
        if (customer is null) return NotFound();
        Response.Headers.ETag = $"\"{customer.Revision:x8}\"";
        Response.Headers.CacheControl = "no-store";
        return customer.Customer;
    }

    /// <summary>Retrieves the employee-only remark for one legacy customer.</summary>
    /// <param name="id">The unique identifier of the customer.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The private remark projection, or not found when the customer does not exist.</returns>
    [HttpGet("{id:int}/internal-remark")]
    [RequirePermission(CustomerPermissions.CustomersRead, ResourcePathTemplate = "/customers/{id}")]
    public async Task<ActionResult<CustomerInternalRemarkResponse>> GetInternalRemarkAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var remark = await service.GetInternalRemarkAsync(id, cancellationToken);
        return remark is null ? NotFound() : remark;
    }

    /// <summary>
    /// Searches and pages through legacy customer profiles.
    /// </summary>
    /// <param name="sort">The optional ordering applied to the customer results.</param>
    /// <param name="search">The optional text used to filter matching customer profiles.</param>
    /// <param name="index">The optional zero-based page index.</param>
    /// <param name="size">The optional maximum number of profiles returned per page.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>A page of matching customers, or a not-found response when no result can be produced.</returns>
    [HttpGet]
    [RequirePermission(CustomerPermissions.CustomersList)]
    public async Task<ActionResult<PaginatedResponse<CustomerResponse>>> GetPaginatedAsync(
        [FromQuery] CustomerSortType? sort,
        [FromQuery] string? search,
        [FromQuery] int? index,
        [FromQuery] int? size,
        CancellationToken cancellationToken)
    {
        var result = await service.GetCustomersAsync(sort, search, index, size, cancellationToken);
        return result is null ? NotFound() : result;
    }

    /// <summary>
    /// Replaces the profile details of an existing legacy customer.
    /// </summary>
    /// <param name="id">The unique identifier of the customer to update.</param>
    /// <param name="request">The replacement customer profile details.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>A no-content response on success, or an error response when the request is invalid or the customer does not exist.</returns>
    [HttpPut("{id:int}")]
    [RequirePermission(CustomerPermissions.CustomersUpdate, ResourcePathTemplate = "/customers/{id}")]
    public async Task<ActionResult> UpdateCustomerAsync(int id, UpsertCustomerRequest request, CancellationToken cancellationToken)
    {
        if (!Valid(request)) return BadRequest();
        return await service.UpdateCustomerAsync(id, request, cancellationToken) ? NoContent() : NotFound();
    }

    /// <summary>Replaces a profile only when the versioned-read ETag still matches.</summary>
    [HttpPut("{id:int}/versioned")]
    [RequirePermission(CustomerPermissions.CustomersUpdate, ResourcePathTemplate = "/customers/{id}")]
    public async Task<ActionResult> UpdateCustomerVersionedAsync(int id, UpsertCustomerRequest request, CancellationToken cancellationToken)
    {
        if (!Valid(request)) return BadRequest();
        if (!Request.Headers.TryGetValue("If-Match", out var header)) return StatusCode(428);
        if (header.Count != 1 || header[0] is not { Length: 10 } value ||
            value[0] != '"' || value[^1] != '"' ||
            !uint.TryParse(value.AsSpan(1, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var revision))
        {
            return BadRequest("A single strong customer ETag is required");
        }

        var result = await service.UpdateCustomerIfRevisionAsync(id, request, revision, cancellationToken);
        return result switch
        {
            CustomerRevisionUpdateResult.Updated => NoContent(),
            CustomerRevisionUpdateResult.NotFound => NotFound(),
            _ => StatusCode(412),
        };
    }

    /// <summary>Replaces the employee-only remark for one legacy customer.</summary>
    /// <param name="id">The unique identifier of the customer.</param>
    /// <param name="request">The bounded private remark replacement.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>No content on success, or not found when the customer does not exist.</returns>
    [HttpPut("{id:int}/internal-remark")]
    [RequirePermission(CustomerPermissions.CustomersUpdate, ResourcePathTemplate = "/customers/{id}")]
    public async Task<ActionResult> UpdateInternalRemarkAsync(
        int id,
        UpdateCustomerInternalRemarkRequest request,
        CancellationToken cancellationToken) =>
        await service.UpdateInternalRemarkAsync(id, request, cancellationToken) ? NoContent() : NotFound();

    private static bool Valid(UpsertCustomerRequest request) =>
        !string.IsNullOrWhiteSpace(request.FirstName) && !string.IsNullOrWhiteSpace(request.LastName) &&
        !string.IsNullOrWhiteSpace(request.Email) && request.Email.Contains('@', StringComparison.Ordinal);

    private static bool Valid(InstantQuotationCustomerProfileRequest request) =>
        !string.IsNullOrWhiteSpace(request.FirstName) &&
        !string.IsNullOrWhiteSpace(request.LastName) &&
        !string.IsNullOrWhiteSpace(request.Email) &&
        request.Email.Contains('@', StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(request.Billing.AddressLine1) &&
        request.Billing.CountryId > 0 &&
        (request.ShipToBillingAddress ||
            request.Shipping is { CountryId: > 0 } && !string.IsNullOrWhiteSpace(request.Shipping.AddressLine1));
}
