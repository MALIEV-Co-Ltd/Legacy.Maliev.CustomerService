using Legacy.Maliev.CustomerService.Api.Authorization;
using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.CustomerService.Api.Controllers;

/// <summary>
/// Provides legacy endpoints for managing company records associated with customers.
/// </summary>
/// <param name="service">The customer application service that manages company records.</param>
[ApiController]
[Route("customers/[controller]")]
[Authorize]
public sealed class CompaniesController(ICustomerService service) : ControllerBase
{
    /// <summary>
    /// Creates a company record for use by legacy customer profiles.
    /// </summary>
    /// <param name="item">The company details to persist.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The created company, or a bad-request response when required details are missing.</returns>
    [HttpPost]
    [RequirePermission(CustomerPermissions.CompaniesCreate)]
    public async Task<ActionResult> CreateCompanyAsync(UpsertCompanyRequest item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Name)) return BadRequest();
        var company = await service.CreateCompanyAsync(item, cancellationToken);
        return CreatedAtRoute("GetCompany", new { id = company.Id }, company);
    }

    /// <summary>
    /// Deletes a company record from the legacy customer domain.
    /// </summary>
    /// <param name="id">The unique identifier of the company to delete.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>A no-content response on success, or a not-found response when the company does not exist.</returns>
    [HttpDelete("{id}")]
    [RequirePermission(CustomerPermissions.CompaniesDelete, ResourcePathTemplate = "/companies/{id}")]
    public async Task<ActionResult> DeleteCompanyAsync(int id, CancellationToken cancellationToken) =>
        await service.DeleteCompanyAsync(id, cancellationToken) ? NoContent() : NotFound();

    /// <summary>
    /// Retrieves a company record by its identifier.
    /// </summary>
    /// <param name="id">The unique identifier of the company to retrieve.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The matching company, or a not-found response when it does not exist.</returns>
    [HttpGet("{id}", Name = "GetCompany")]
    [RequirePermission(CustomerPermissions.CompaniesRead, ResourcePathTemplate = "/companies/{id}")]
    public async Task<ActionResult<CompanyResponse>> GetCompanyAsync(int id, CancellationToken cancellationToken)
    {
        var company = await service.GetCompanyAsync(id, cancellationToken);
        return company is null ? NotFound() : company;
    }

    /// <summary>
    /// Replaces the details of an existing company record.
    /// </summary>
    /// <param name="id">The unique identifier of the company to update.</param>
    /// <param name="item">The replacement company details.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>A no-content response on success, or an error response when the request is invalid or the company does not exist.</returns>
    [HttpPut("{id}")]
    [RequirePermission(CustomerPermissions.CompaniesUpdate, ResourcePathTemplate = "/companies/{id}")]
    public async Task<ActionResult> UpdateCompanyAsync(int id, UpsertCompanyRequest item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Name)) return BadRequest();
        return await service.UpdateCompanyAsync(id, item, cancellationToken) ? NoContent() : NotFound();
    }
}
