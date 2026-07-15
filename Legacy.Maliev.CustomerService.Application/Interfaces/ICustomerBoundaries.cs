using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Domain;

namespace Legacy.Maliev.CustomerService.Application.Interfaces;

/// <summary>Customer application boundary.</summary>
public interface ICustomerService
{
    /// <summary>Retrieves a customer profile by its legacy database identifier.</summary>
    /// <param name="id">The legacy customer identifier.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The customer profile if found; otherwise, <see langword="null"/>.</returns>
    Task<CustomerResponse?> GetCustomerAsync(int id, CancellationToken cancellationToken);
    /// <summary>Retrieves a customer profile by its normalized email address.</summary>
    /// <param name="email">The customer email address to locate.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The matching customer profile if found; otherwise, <see langword="null"/>.</returns>
    Task<CustomerResponse?> GetCustomerByEmailAsync(string email, CancellationToken cancellationToken);
    /// <summary>Searches and sorts customer profiles using the legacy pagination contract.</summary>
    /// <param name="sort">The optional legacy sort mode.</param>
    /// <param name="search">The optional customer search text.</param>
    /// <param name="index">The optional one-based page index.</param>
    /// <param name="size">The optional number of records per page.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>A page of matching customers, or <see langword="null"/> when no page can be produced.</returns>
    Task<PaginatedResponse<CustomerResponse>?> GetCustomersAsync(CustomerSortType? sort, string? search, int? index, int? size, CancellationToken cancellationToken);
    /// <summary>Creates a customer profile from the legacy-compatible request.</summary>
    /// <param name="request">The customer details to persist.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The newly created customer profile.</returns>
    Task<CustomerResponse> CreateCustomerAsync(UpsertCustomerRequest request, CancellationToken cancellationToken);
    /// <summary>Updates an existing customer profile and invalidates its cached representation.</summary>
    /// <param name="id">The legacy customer identifier.</param>
    /// <param name="request">The replacement customer details.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns><see langword="true"/> when the customer was updated; otherwise, <see langword="false"/>.</returns>
    Task<bool> UpdateCustomerAsync(int id, UpsertCustomerRequest request, CancellationToken cancellationToken);
    /// <summary>Deletes a customer profile and invalidates its cached representation.</summary>
    /// <param name="id">The legacy customer identifier.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns><see langword="true"/> when the customer was deleted; otherwise, <see langword="false"/>.</returns>
    Task<bool> DeleteCustomerAsync(int id, CancellationToken cancellationToken);
    /// <summary>Retrieves all address records exposed by the legacy customer boundary.</summary>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The available customer address records.</returns>
    Task<IReadOnlyList<AddressResponse>> GetAddressesAsync(CancellationToken cancellationToken);
    /// <summary>Retrieves an address owned by a specific customer.</summary>
    /// <param name="customerId">The owning customer's legacy identifier.</param>
    /// <param name="addressId">The address identifier.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The address if it exists and belongs to the customer; otherwise, <see langword="null"/>.</returns>
    Task<AddressResponse?> GetAddressAsync(int customerId, int addressId, CancellationToken cancellationToken);
    /// <summary>Creates and associates an address with a customer profile.</summary>
    /// <param name="customerId">The owning customer's legacy identifier.</param>
    /// <param name="request">The address details to persist.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The created address, or <see langword="null"/> when the customer does not exist.</returns>
    Task<AddressResponse?> CreateAddressAsync(int customerId, UpsertAddressRequest request, CancellationToken cancellationToken);
    /// <summary>Updates an existing customer address.</summary>
    /// <param name="id">The address identifier.</param>
    /// <param name="request">The replacement address details.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns><see langword="true"/> when the address was updated; otherwise, <see langword="false"/>.</returns>
    Task<bool> UpdateAddressAsync(int id, UpsertAddressRequest request, CancellationToken cancellationToken);
    /// <summary>Deletes an address owned by a specific customer.</summary>
    /// <param name="customerId">The owning customer's legacy identifier.</param>
    /// <param name="addressId">The address identifier.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns><see langword="true"/> when the address was deleted; otherwise, <see langword="false"/>.</returns>
    Task<bool> DeleteAddressAsync(int customerId, int addressId, CancellationToken cancellationToken);
    /// <summary>Retrieves a company record by its legacy database identifier.</summary>
    /// <param name="id">The legacy company identifier.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The company if found; otherwise, <see langword="null"/>.</returns>
    Task<CompanyResponse?> GetCompanyAsync(int id, CancellationToken cancellationToken);
    /// <summary>Creates a company record for association with customer profiles.</summary>
    /// <param name="request">The company details to persist.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The newly created company record.</returns>
    Task<CompanyResponse> CreateCompanyAsync(UpsertCompanyRequest request, CancellationToken cancellationToken);
    /// <summary>Updates an existing company record.</summary>
    /// <param name="id">The legacy company identifier.</param>
    /// <param name="request">The replacement company details.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns><see langword="true"/> when the company was updated; otherwise, <see langword="false"/>.</returns>
    Task<bool> UpdateCompanyAsync(int id, UpsertCompanyRequest request, CancellationToken cancellationToken);
    /// <summary>Deletes a company record by its legacy database identifier.</summary>
    /// <param name="id">The legacy company identifier.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns><see langword="true"/> when the company was deleted; otherwise, <see langword="false"/>.</returns>
    Task<bool> DeleteCompanyAsync(int id, CancellationToken cancellationToken);
}

/// <summary>Customer PostgreSQL boundary.</summary>
public interface ICustomerRepository
{
    /// <summary>Loads a customer projection by its legacy database identifier.</summary>
    /// <param name="id">The legacy customer identifier.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The customer projection if found; otherwise, <see langword="null"/>.</returns>
    Task<CustomerResponse?> GetCustomerAsync(int id, CancellationToken cancellationToken);
    /// <summary>Loads a customer projection by its normalized email address.</summary>
    /// <param name="email">The customer email address to locate.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The matching customer projection if found; otherwise, <see langword="null"/>.</returns>
    Task<CustomerResponse?> GetCustomerByEmailAsync(string email, CancellationToken cancellationToken);
    /// <summary>Queries customer projections with legacy sorting, searching, and pagination.</summary>
    /// <param name="sort">The optional legacy sort mode.</param>
    /// <param name="search">The optional customer search text.</param>
    /// <param name="pageIndex">The one-based page index.</param>
    /// <param name="pageSize">The maximum number of records in the page.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>A page of matching customer projections, or <see langword="null"/> when no page can be produced.</returns>
    Task<PaginatedResponse<CustomerResponse>?> GetCustomersAsync(CustomerSortType? sort, string? search, int pageIndex, int pageSize, CancellationToken cancellationToken);
    /// <summary>Persists a new customer entity using legacy-compatible fields.</summary>
    /// <param name="request">The customer details to persist.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The persisted customer entity with its assigned identifier.</returns>
    Task<Customer> CreateCustomerAsync(UpsertCustomerRequest request, CancellationToken cancellationToken);
    /// <summary>Persists replacement details for an existing customer.</summary>
    /// <param name="id">The legacy customer identifier.</param>
    /// <param name="request">The replacement customer details.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns><see langword="true"/> when a customer was updated; otherwise, <see langword="false"/>.</returns>
    Task<bool> UpdateCustomerAsync(int id, UpsertCustomerRequest request, CancellationToken cancellationToken);
    /// <summary>Removes a customer record from persistence.</summary>
    /// <param name="id">The legacy customer identifier.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns><see langword="true"/> when a customer was deleted; otherwise, <see langword="false"/>.</returns>
    Task<bool> DeleteCustomerAsync(int id, CancellationToken cancellationToken);
    /// <summary>Loads all address projections exposed by the legacy persistence contract.</summary>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The available customer address projections.</returns>
    Task<IReadOnlyList<AddressResponse>> GetAddressesAsync(CancellationToken cancellationToken);
    /// <summary>Loads an address projection scoped to its owning customer.</summary>
    /// <param name="customerId">The owning customer's legacy identifier.</param>
    /// <param name="addressId">The address identifier.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The matching address projection if found; otherwise, <see langword="null"/>.</returns>
    Task<AddressResponse?> GetAddressAsync(int customerId, int addressId, CancellationToken cancellationToken);
    /// <summary>Persists an address associated with an existing customer.</summary>
    /// <param name="customerId">The owning customer's legacy identifier.</param>
    /// <param name="request">The address details to persist.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The persisted address entity, or <see langword="null"/> when the customer does not exist.</returns>
    Task<Address?> CreateAddressAsync(int customerId, UpsertAddressRequest request, CancellationToken cancellationToken);
    /// <summary>Persists replacement details for an existing address.</summary>
    /// <param name="id">The address identifier.</param>
    /// <param name="request">The replacement address details.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns><see langword="true"/> when an address was updated; otherwise, <see langword="false"/>.</returns>
    Task<bool> UpdateAddressAsync(int id, UpsertAddressRequest request, CancellationToken cancellationToken);
    /// <summary>Finds customers whose cached projections embed a specific address.</summary>
    /// <param name="addressId">The address identifier referenced as billing or shipping address.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The identifiers of customers that reference the address.</returns>
    Task<IReadOnlyList<int>> GetCustomerIdsForAddressAsync(int addressId, CancellationToken cancellationToken);
    /// <summary>Removes an address scoped to its owning customer.</summary>
    /// <param name="customerId">The owning customer's legacy identifier.</param>
    /// <param name="addressId">The address identifier.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns><see langword="true"/> when the address was deleted; otherwise, <see langword="false"/>.</returns>
    Task<bool> DeleteAddressAsync(int customerId, int addressId, CancellationToken cancellationToken);
    /// <summary>Loads a company projection by its legacy database identifier.</summary>
    /// <param name="id">The legacy company identifier.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The company projection if found; otherwise, <see langword="null"/>.</returns>
    Task<CompanyResponse?> GetCompanyAsync(int id, CancellationToken cancellationToken);
    /// <summary>Persists a new company entity for customer association.</summary>
    /// <param name="request">The company details to persist.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The persisted company entity with its assigned identifier.</returns>
    Task<Company> CreateCompanyAsync(UpsertCompanyRequest request, CancellationToken cancellationToken);
    /// <summary>Persists replacement details for an existing company.</summary>
    /// <param name="id">The legacy company identifier.</param>
    /// <param name="request">The replacement company details.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns><see langword="true"/> when a company was updated; otherwise, <see langword="false"/>.</returns>
    Task<bool> UpdateCompanyAsync(int id, UpsertCompanyRequest request, CancellationToken cancellationToken);
    /// <summary>Finds customers whose cached projections embed a specific company.</summary>
    /// <param name="companyId">The company identifier referenced by customer profiles.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The identifiers of customers that reference the company.</returns>
    Task<IReadOnlyList<int>> GetCustomerIdsForCompanyAsync(int companyId, CancellationToken cancellationToken);
    /// <summary>Removes a company record from persistence.</summary>
    /// <param name="id">The legacy company identifier.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns><see langword="true"/> when a company was deleted; otherwise, <see langword="false"/>.</returns>
    Task<bool> DeleteCompanyAsync(int id, CancellationToken cancellationToken);
}

/// <summary>Short-lived Redis cache for authorized customer reads.</summary>
public interface ICustomerCache
{
    /// <summary>Retrieves a short-lived authorized customer projection from the cache.</summary>
    /// <param name="id">The legacy customer identifier used as the cache key.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>The cached customer if present; otherwise, <see langword="null"/>.</returns>
    Task<CustomerResponse?> GetAsync(int id, CancellationToken cancellationToken);
    /// <summary>Stores an authorized customer projection using the configured short lifetime.</summary>
    /// <param name="customer">The customer projection to cache.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>A task representing the asynchronous cache write.</returns>
    Task SetAsync(CustomerResponse customer, CancellationToken cancellationToken);
    /// <summary>Removes a customer's cached projection after a related write.</summary>
    /// <param name="id">The legacy customer identifier used as the cache key.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>A task representing the asynchronous cache removal.</returns>
    Task RemoveAsync(int id, CancellationToken cancellationToken);
}
