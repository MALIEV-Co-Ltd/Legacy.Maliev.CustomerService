using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;

namespace Legacy.Maliev.CustomerService.Application.Services;

/// <summary>Coordinates customer persistence, cache invalidation, and AuthService identity ownership.</summary>
public sealed class CustomerApplicationService(
    ICustomerRepository repository,
    ICustomerCache cache,
    ICustomerIdentityDirectory identities) : ICustomerService
{
    /// <inheritdoc />
    public async Task<CustomerResponse?> GetCustomerAsync(int id, CancellationToken cancellationToken)
    {
        var cached = await cache.GetAsync(id, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var customer = await repository.GetCustomerAsync(id, cancellationToken);
        if (customer is not null)
        {
            await cache.SetAsync(customer, cancellationToken);
        }

        return customer;
    }

    /// <inheritdoc />
    public Task<CustomerResponse?> GetCustomerByEmailAsync(string email, CancellationToken cancellationToken) =>
        repository.GetCustomerByEmailAsync(email.Trim(), cancellationToken);

    /// <inheritdoc />
    public Task<PaginatedResponse<CustomerResponse>?> GetCustomersAsync(
        CustomerSortType? sort,
        string? search,
        int? index,
        int? size,
        CancellationToken cancellationToken) =>
        repository.GetCustomersAsync(sort, search, Math.Max(index ?? 1, 1), Math.Clamp(size ?? 50, 1, 250), cancellationToken);

    /// <inheritdoc />
    public async Task<CustomerResponse> CreateCustomerAsync(UpsertCustomerRequest request, CancellationToken cancellationToken)
    {
        var entity = await repository.CreateCustomerAsync(request, cancellationToken);
        return (await repository.GetCustomerAsync(entity.Id, cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateCustomerAsync(int id, UpsertCustomerRequest request, CancellationToken cancellationToken)
    {
        var updated = await repository.UpdateCustomerAsync(id, request, cancellationToken);
        if (updated)
        {
            await cache.RemoveAsync(id, cancellationToken);
        }

        return updated;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteCustomerAsync(int id, CancellationToken cancellationToken)
    {
        var deleted = await repository.DeleteCustomerAsync(id, cancellationToken);
        if (deleted)
        {
            await cache.RemoveAsync(id, cancellationToken);
        }

        return deleted;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AddressResponse>> GetAddressesAsync(CancellationToken cancellationToken) =>
        repository.GetAddressesAsync(cancellationToken);

    /// <inheritdoc />
    public Task<AddressResponse?> GetAddressAsync(int customerId, int addressId, CancellationToken cancellationToken) =>
        repository.GetAddressAsync(customerId, addressId, cancellationToken);

    /// <inheritdoc />
    public async Task<AddressResponse?> CreateAddressAsync(int customerId, UpsertAddressRequest request, CancellationToken cancellationToken)
    {
        var address = await repository.CreateAddressAsync(customerId, request, cancellationToken);
        if (address is null)
        {
            return null;
        }

        await cache.RemoveAsync(customerId, cancellationToken);
        return new AddressResponse(
            address.Id,
            address.Building,
            address.AddressLine1,
            address.AddressLine2,
            address.City,
            address.State,
            address.PostalCode,
            address.CountryId,
            address.CreatedDate,
            address.ModifiedDate);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAddressAsync(int id, UpsertAddressRequest request, CancellationToken cancellationToken)
    {
        var customerIds = await repository.GetCustomerIdsForAddressAsync(id, cancellationToken);
        var updated = await repository.UpdateAddressAsync(id, request, cancellationToken);
        if (updated)
        {
            await InvalidateCustomersAsync(customerIds, cancellationToken);
        }

        return updated;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAddressAsync(int customerId, int addressId, CancellationToken cancellationToken)
    {
        var deleted = await repository.DeleteAddressAsync(customerId, addressId, cancellationToken);
        if (deleted)
        {
            await cache.RemoveAsync(customerId, cancellationToken);
        }

        return deleted;
    }

    /// <inheritdoc />
    public Task<CompanyResponse?> GetCompanyAsync(int id, CancellationToken cancellationToken) =>
        repository.GetCompanyAsync(id, cancellationToken);

    /// <inheritdoc />
    public async Task<CompanyResponse> CreateCompanyAsync(UpsertCompanyRequest request, CancellationToken cancellationToken)
    {
        var company = await repository.CreateCompanyAsync(request, cancellationToken);
        return new CompanyResponse(company.Id, company.Name, company.TaxNumber, company.Registrar, company.CreatedDate, company.ModifiedDate);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateCompanyAsync(int id, UpsertCompanyRequest request, CancellationToken cancellationToken)
    {
        var customerIds = await repository.GetCustomerIdsForCompanyAsync(id, cancellationToken);
        var updated = await repository.UpdateCompanyAsync(id, request, cancellationToken);
        if (updated)
        {
            await InvalidateCustomersAsync(customerIds, cancellationToken);
        }

        return updated;
    }

    /// <inheritdoc />
    public Task<bool> DeleteCompanyAsync(int id, CancellationToken cancellationToken) =>
        repository.DeleteCompanyAsync(id, cancellationToken);

    private Task InvalidateCustomersAsync(IReadOnlyList<int> customerIds, CancellationToken cancellationToken) =>
        Task.WhenAll(customerIds.Select(customerId => cache.RemoveAsync(customerId, cancellationToken)));

    /// <inheritdoc />
    public Task<IdentityOperationResult> ValidateCredentialsAsync(UserValidationRequest request, CancellationToken cancellationToken) =>
        identities.ValidateCredentialsAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<IdentityOperationResult> CreateIdentityAsync(
        int customerId,
        CustomerIdentityRequest request,
        string? legacyPassword,
        CancellationToken cancellationToken)
    {
        return await repository.GetCustomerAsync(customerId, cancellationToken) is null
            ? new IdentityOperationResult(false, Errors: ["Customer not found"])
            : await identities.CreateAsync(customerId, request, legacyPassword, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CustomerIdentityResponse?> GetIdentityAsync(int customerId, CancellationToken cancellationToken)
    {
        return await repository.GetCustomerAsync(customerId, cancellationToken) is null
            ? null
            : await identities.GetAsync(customerId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IdentityOperationResult> UpdateIdentityAsync(int customerId, CustomerIdentityRequest request, CancellationToken cancellationToken)
    {
        return await repository.GetCustomerAsync(customerId, cancellationToken) is null
            ? new IdentityOperationResult(false, Errors: ["Customer not found"])
            : await identities.UpdateAsync(customerId, request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IdentityOperationResult> DeleteIdentityAsync(int customerId, CancellationToken cancellationToken)
    {
        return await repository.GetCustomerAsync(customerId, cancellationToken) is null
            ? new IdentityOperationResult(false, Errors: ["Customer not found"])
            : await identities.DeleteAsync(customerId, cancellationToken);
    }
}
