using Legacy.Maliev.CustomerService.Api.Authorization;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.CustomerService.Api.Controllers;

/// <summary>
/// Provides the legacy email-address lookup endpoint for customer profiles.
/// </summary>
/// <param name="service">The customer application service used to locate customer records.</param>
[ApiController]
[Route("customers/[controller]")]
[Authorize]
public sealed class EmailsController(ICustomerService service) : ControllerBase
{
    /// <summary>
    /// Retrieves the customer profile associated with an email address.
    /// </summary>
    /// <param name="email">The customer email address to look up.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The matching customer, or a not-found response when no profile uses the address.</returns>
    [HttpGet("{email}", Name = "GetCustomerByEmail")]
    [RequirePermission(CustomerPermissions.CustomersList, IsCritical = true, AuditPurpose = "Customer lookup by email")]
    public async Task<ActionResult<CustomerResponse>> GetCustomerByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var customer = await service.GetCustomerByEmailAsync(email, cancellationToken);
        return customer is null ? NotFound() : customer;
    }
}
