using Legacy.Maliev.CustomerService.Api.Authorization;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.CustomerService.Api.Controllers;

/// <summary>
/// Provides legacy endpoints for creating, retrieving, updating, and deleting customer addresses.
/// </summary>
/// <param name="service">The customer application service that manages address records.</param>
[ApiController]
[Route("customers/[controller]")]
[Authorize]
public sealed class AddressesController(ICustomerService service) : ControllerBase
{
    /// <summary>
    /// Adds an address to the specified customer profile.
    /// </summary>
    /// <param name="customerId">The unique identifier of the customer that owns the address.</param>
    /// <param name="item">The address details to persist.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The created address, or an error response when the request is invalid or the customer does not exist.</returns>
    [HttpPost("/customers/{customerId:int}/addresses")]
    [RequirePermission(CustomerPermissions.AddressesCreate, ResourcePathTemplate = "/customers/{customerId}")]
    public async Task<IActionResult> CreateCustomerAddressAsync(int customerId, UpsertAddressRequest item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.AddressLine1)) return BadRequest();
        var address = await service.CreateAddressAsync(customerId, item, cancellationToken);
        return address is null ? NotFound() : CreatedAtRoute("GetAddress", new { customerId, addressId = address.Id }, address);
    }

    /// <summary>
    /// Deletes an address owned by the specified customer.
    /// </summary>
    /// <param name="customerId">The unique identifier of the customer that owns the address.</param>
    /// <param name="addressId">The unique identifier of the address to delete.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>A no-content response on success, or a not-found response when the address does not exist.</returns>
    [HttpDelete("/customers/{customerId:int}/addresses/{addressId:int}")]
    [RequirePermission(CustomerPermissions.AddressesDelete, ResourcePathTemplate = "/customers/{customerId}/addresses/{addressId}")]
    public async Task<IActionResult> DeleteCustomerAddressAsync(int customerId, int addressId, CancellationToken cancellationToken) =>
        await service.DeleteAddressAsync(customerId, addressId, cancellationToken) ? NoContent() : NotFound();

    /// <summary>
    /// Retrieves all address records available through the legacy customer service.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The available addresses, or a not-found response when no records exist.</returns>
    [HttpGet]
    [RequirePermission(CustomerPermissions.AddressesList)]
    public async Task<ActionResult<IReadOnlyList<AddressResponse>>> GetAllAddressesAsync(CancellationToken cancellationToken)
    {
        var addresses = await service.GetAddressesAsync(cancellationToken);
        return addresses.Count == 0 ? NotFound() : Ok(addresses);
    }

    /// <summary>
    /// Retrieves a specific address owned by a customer.
    /// </summary>
    /// <param name="customerId">The unique identifier of the customer that owns the address.</param>
    /// <param name="addressId">The unique identifier of the address to retrieve.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The matching address, or a not-found response when it does not exist.</returns>
    [HttpGet("/customers/{customerId:int}/addresses/{addressId:int}", Name = "GetAddress")]
    [RequirePermission(CustomerPermissions.AddressesRead, ResourcePathTemplate = "/customers/{customerId}/addresses/{addressId}")]
    public async Task<ActionResult<AddressResponse>> GetCustomerAddressAsync(int customerId, int addressId, CancellationToken cancellationToken)
    {
        var address = await service.GetAddressAsync(customerId, addressId, cancellationToken);
        return address is null ? NotFound() : address;
    }

    /// <summary>
    /// Replaces the details of an existing address record.
    /// </summary>
    /// <param name="id">The unique identifier of the address to update.</param>
    /// <param name="item">The replacement address details.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>A no-content response on success, or an error response when the request is invalid or the address does not exist.</returns>
    [HttpPut("{id:int}")]
    [RequirePermission(CustomerPermissions.AddressesUpdate, ResourcePathTemplate = "/addresses/{id}")]
    public async Task<IActionResult> UpdateAddressAsync(int id, UpsertAddressRequest item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.AddressLine1)) return BadRequest();
        return await service.UpdateAddressAsync(id, item, cancellationToken) ? NoContent() : NotFound();
    }
}
