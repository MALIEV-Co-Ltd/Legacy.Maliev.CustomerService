namespace Legacy.Maliev.CustomerService.Application.Models;

/// <summary>Legacy customer response with related records.</summary>
public sealed record CustomerResponse(
    int Id,
    string FirstName,
    string LastName,
    string FullName,
    string? Telephone,
    string? Mobile,
    string? Fax,
    string Email,
    DateTime? DateOfBirth,
    int? CompanyId,
    int? BillingAddressId,
    int? ShippingAddressId,
    DateTime? CreatedDate,
    DateTime? ModifiedDate,
    AddressResponse? BillingAddress,
    CompanyResponse? Company,
    AddressResponse? ShippingAddress);

/// <summary>Legacy company response.</summary>
public sealed record CompanyResponse(int Id, string Name, string? TaxNumber, string? Registrar, DateTime? CreatedDate, DateTime? ModifiedDate);

/// <summary>Legacy address response.</summary>
public sealed record AddressResponse(
    int Id,
    string? Building,
    string AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    int CountryId,
    DateTime? CreatedDate,
    DateTime? ModifiedDate);

/// <summary>Customer create/update request preserving legacy field names.</summary>
public sealed record UpsertCustomerRequest(
    string FirstName,
    string LastName,
    string? Telephone,
    string? Mobile,
    string? Fax,
    string Email,
    DateTime? DateOfBirth,
    int? CompanyId,
    int? BillingAddressId,
    int? ShippingAddressId);

/// <summary>Company create/update request.</summary>
public sealed record UpsertCompanyRequest(string Name, string? TaxNumber, string? Registrar);

/// <summary>Address create/update request.</summary>
public sealed record UpsertAddressRequest(
    string? Building,
    string AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    int CountryId);

/// <summary>Preserves the legacy paginated response shape.</summary>
public sealed record PaginatedResponse<T>(IReadOnlyList<T> Items, int PageIndex, int TotalPages, int TotalRecords)
{
    /// <summary>Indicates whether another page follows the current page.</summary>
    public bool HasNextPage => PageIndex < TotalPages;
    /// <summary>Indicates whether a page precedes the current page.</summary>
    public bool HasPreviousPage => PageIndex > 1;
}

/// <summary>Legacy customer sort names and numeric values.</summary>
public enum CustomerSortType
{
    /// <summary>Orders customers by legacy identifier from lowest to highest.</summary>
    CustomerId_Ascending,
    /// <summary>Orders customers by legacy identifier from highest to lowest.</summary>
    CustomerId_Descending,
    /// <summary>Orders customers alphabetically by associated company name.</summary>
    CustomerCompany_Ascending,
    /// <summary>Orders customers in reverse alphabetical order by associated company name.</summary>
    CustomerCompany_Descending,
    /// <summary>Orders customers alphabetically by email address.</summary>
    CustomerEmail_Ascending,
    /// <summary>Orders customers in reverse alphabetical order by email address.</summary>
    CustomerEmail_Descending,
    /// <summary>Orders customers from earliest to latest creation time.</summary>
    CustomerCreatedDate_Ascending,
    /// <summary>Orders customers from latest to earliest creation time.</summary>
    CustomerCreatedDate_Descending,
    /// <summary>Orders customers from earliest to latest modification time.</summary>
    CustomerModifiedDate_Ascending,
    /// <summary>Orders customers from latest to earliest modification time.</summary>
    CustomerModifiedDate_Descending,
}
