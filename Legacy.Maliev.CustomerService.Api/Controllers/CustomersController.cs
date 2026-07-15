using Legacy.Maliev.CustomerService.Api.Authorization;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.CustomerService.Api.Controllers;

/// <summary>Preserves the legacy customer profile contract without taking ownership of authentication identity storage.</summary>
/// <param name="service">The customer application service that coordinates profile and identity operations.</param>
[ApiController]
[Route("customers")]
[Authorize]
public sealed class CustomersController(ICustomerService service) : ControllerBase
{
    /// <summary>
    /// Validates supplied credentials against the legacy customer identity provider.
    /// </summary>
    /// <param name="request">The username and password to validate.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>An OK response for valid credentials, an unauthorized response for invalid credentials, or a bad-request response when credentials are missing.</returns>
    [HttpPost("v1/validate")]
    [RequirePermission(CustomerPermissions.CredentialsValidate, RequireLiveCheck = true, IsCritical = true, AuditPurpose = "Legacy credential validation")]
    public async Task<IActionResult> ValidateUserCredentialsAsync(UserValidationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("Username and password are required.");
        }

        var result = await service.ValidateCredentialsAsync(request, cancellationToken);
        return result.Succeeded ? Ok() : Unauthorized();
    }

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
        var customer = await service.CreateCustomerAsync(request, cancellationToken);
        return CreatedAtRoute("GetCustomer", new { id = customer.Id }, customer);
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

    /// <summary>
    /// Creates an authentication identity link for an existing customer profile.
    /// </summary>
    /// <param name="id">The unique identifier of the customer receiving the identity link.</param>
    /// <param name="item">The identity attributes to register with the authentication service.</param>
    /// <param name="password">The optional initial password for the customer identity.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The created identity, or an error response when the customer is missing or the identity cannot be created.</returns>
    [HttpPost("{id:int}/identity/{password?}")]
    [RequirePermission(CustomerPermissions.IdentitiesManage, ResourcePathTemplate = "/customers/{id}/identity", RequireLiveCheck = true, IsCritical = true)]
    public async Task<IActionResult> CreateIdentityAsync(int id, CustomerIdentityRequest item, string? password, CancellationToken cancellationToken)
    {
        var result = await service.CreateIdentityAsync(id, item, password, cancellationToken);
        if (!result.Succeeded) return result.Errors?.Contains("Customer not found") == true ? NotFound() : BadRequest(result.Errors);
        return CreatedAtRoute("GetIdentity", new { id }, result.Identity);
    }

    /// <summary>
    /// Retrieves the authentication identity linked to a customer profile.
    /// </summary>
    /// <param name="id">The unique identifier of the customer whose identity is requested.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The linked customer identity, or a not-found response when no identity exists.</returns>
    [HttpGet("{id:int}/identity", Name = "GetIdentity")]
    [RequirePermission(CustomerPermissions.IdentitiesRead, ResourcePathTemplate = "/customers/{id}/identity", RequireLiveCheck = true)]
    public async Task<ActionResult<CustomerIdentityResponse>> GetIdentityAsync(int id, CancellationToken cancellationToken)
    {
        var identity = await service.GetIdentityAsync(id, cancellationToken);
        return identity is null ? NotFound() : identity;
    }

    /// <summary>
    /// Updates the authentication identity linked to a customer profile.
    /// </summary>
    /// <param name="id">The unique identifier of the customer whose identity is changing.</param>
    /// <param name="item">The replacement identity attributes.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>A no-content response on success, or a bad-request response when the identity cannot be updated.</returns>
    [HttpPut("{id:int}/identity")]
    [RequirePermission(CustomerPermissions.IdentitiesManage, ResourcePathTemplate = "/customers/{id}/identity", RequireLiveCheck = true, IsCritical = true)]
    public async Task<ActionResult> UpdateIdentityAsync(int id, CustomerIdentityRequest item, CancellationToken cancellationToken)
    {
        var result = await service.UpdateIdentityAsync(id, item, cancellationToken);
        return result.Succeeded ? NoContent() : BadRequest(result.Errors);
    }

    /// <summary>
    /// Deletes the authentication identity linked to a customer profile.
    /// </summary>
    /// <param name="id">The unique identifier of the customer whose identity is being removed.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>A no-content response on success, or a bad-request response when the identity cannot be deleted.</returns>
    [HttpDelete("{id:int}/identity")]
    [RequirePermission(CustomerPermissions.IdentitiesManage, ResourcePathTemplate = "/customers/{id}/identity", RequireLiveCheck = true, IsCritical = true)]
    public async Task<ActionResult> DeleteIdentityAsync(int id, CancellationToken cancellationToken)
    {
        var result = await service.DeleteIdentityAsync(id, cancellationToken);
        return result.Succeeded ? NoContent() : BadRequest(result.Errors);
    }

    private static bool Valid(UpsertCustomerRequest request) =>
        !string.IsNullOrWhiteSpace(request.FirstName) && !string.IsNullOrWhiteSpace(request.LastName) &&
        !string.IsNullOrWhiteSpace(request.Email) && request.Email.Contains('@', StringComparison.Ordinal);
}
