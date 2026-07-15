using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.CustomerService.Data;

/// <summary>
/// Persists and retrieves legacy-compatible customer, address, and company data through bounded projection-first queries.
/// </summary>
/// <param name="dbContext">The customer database context used for persistence operations.</param>
/// <param name="timeProvider">The time source used to assign auditable creation and modification timestamps.</param>
public sealed class CustomerRepository(CustomerDbContext dbContext, TimeProvider timeProvider) : ICustomerRepository
{
    /// <inheritdoc />
    public Task<CustomerResponse?> GetCustomerAsync(int id, CancellationToken cancellationToken) =>
        CustomerQuery().SingleOrDefaultAsync(customer => customer.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<CustomerResponse?> GetCustomerByEmailAsync(string email, CancellationToken cancellationToken) =>
        Project(dbContext.Customers.AsNoTracking().Where(customer => customer.Email.ToLower() == email.ToLower()))
            .SingleOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<PaginatedResponse<CustomerResponse>?> GetCustomersAsync(
        CustomerSortType? sort,
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken)
    {
        IQueryable<Customer> query = dbContext.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var value = search.Trim();
            var numeric = int.TryParse(value, out var id);
            var pattern = $"%{value}%";
            query = query.Where(customer =>
                (numeric && customer.Id == id) ||
                EF.Functions.ILike(customer.FirstName, pattern) ||
                EF.Functions.ILike(customer.LastName, pattern) ||
                EF.Functions.ILike(customer.FullName, pattern) ||
                EF.Functions.ILike(customer.Email, pattern) ||
                (customer.Mobile != null && EF.Functions.ILike(customer.Mobile, pattern)) ||
                (customer.Telephone != null && EF.Functions.ILike(customer.Telephone, pattern)) ||
                (customer.Company != null && EF.Functions.ILike(customer.Company.Name, pattern)));
        }

        query = sort switch
        {
            CustomerSortType.CustomerId_Descending => query.OrderByDescending(value => value.Id),
            CustomerSortType.CustomerCompany_Ascending => query.OrderBy(value => value.Company!.Name),
            CustomerSortType.CustomerCompany_Descending => query.OrderByDescending(value => value.Company!.Name),
            CustomerSortType.CustomerEmail_Ascending => query.OrderBy(value => value.Email),
            CustomerSortType.CustomerEmail_Descending => query.OrderByDescending(value => value.Email),
            CustomerSortType.CustomerCreatedDate_Ascending => query.OrderBy(value => value.CreatedDate),
            CustomerSortType.CustomerCreatedDate_Descending => query.OrderByDescending(value => value.CreatedDate),
            CustomerSortType.CustomerModifiedDate_Ascending => query.OrderBy(value => value.ModifiedDate),
            CustomerSortType.CustomerModifiedDate_Descending => query.OrderByDescending(value => value.ModifiedDate),
            _ => query.OrderBy(value => value.Id),
        };

        var total = await query.CountAsync(cancellationToken);
        if (total == 0)
        {
            return null;
        }

        var items = await Project(query)
            .Skip((pageIndex - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PaginatedResponse<CustomerResponse>(items, pageIndex, (int)Math.Ceiling(total / (double)pageSize), total);
    }

    /// <inheritdoc />
    public async Task<Customer> CreateCustomerAsync(UpsertCustomerRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entity = new Customer
        {
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Email = request.Email.Trim(),
            Telephone = request.Telephone,
            Mobile = request.Mobile,
            Fax = request.Fax,
            DateOfBirth = request.DateOfBirth,
            CompanyId = request.CompanyId,
            BillingAddressId = request.BillingAddressId,
            ShippingAddressId = request.ShippingAddressId,
            CreatedDate = now,
            ModifiedDate = now,
        };
        dbContext.Customers.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateCustomerAsync(int id, UpsertCustomerRequest request, CancellationToken cancellationToken)
    {
        var entity = await dbContext.Customers.FindAsync([id], cancellationToken);
        if (entity is null) return false;
        entity.FirstName = request.FirstName.Trim(); entity.LastName = request.LastName.Trim(); entity.Email = request.Email.Trim();
        entity.Telephone = request.Telephone; entity.Mobile = request.Mobile; entity.Fax = request.Fax; entity.DateOfBirth = request.DateOfBirth;
        entity.CompanyId = request.CompanyId; entity.BillingAddressId = request.BillingAddressId; entity.ShippingAddressId = request.ShippingAddressId;
        entity.ModifiedDate = timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteCustomerAsync(int id, CancellationToken cancellationToken)
    {
        var affected = await dbContext.Customers.Where(value => value.Id == id).ExecuteDeleteAsync(cancellationToken);
        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AddressResponse>> GetAddressesAsync(CancellationToken cancellationToken) =>
        await dbContext.Addresses.AsNoTracking().OrderBy(value => value.Id).Select(ToAddress()).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<AddressResponse?> GetAddressAsync(int customerId, int addressId, CancellationToken cancellationToken) =>
        dbContext.Customers.AsNoTracking()
            .Where(customer => customer.Id == customerId && (customer.BillingAddressId == addressId || customer.ShippingAddressId == addressId))
            .SelectMany(_ => dbContext.Addresses.Where(address => address.Id == addressId).Select(ToAddress()))
            .SingleOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Address?> CreateAddressAsync(int customerId, UpsertAddressRequest request, CancellationToken cancellationToken)
    {
        if (!await dbContext.Customers.AnyAsync(value => value.Id == customerId, cancellationToken)) return null;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entity = new Address
        {
            Building = request.Building,
            AddressLine1 = request.AddressLine1.Trim(),
            AddressLine2 = request.AddressLine2,
            City = request.City,
            State = request.State,
            PostalCode = request.PostalCode,
            CountryId = request.CountryId,
            CreatedDate = now,
            ModifiedDate = now
        };
        dbContext.Addresses.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAddressAsync(int id, UpsertAddressRequest request, CancellationToken cancellationToken)
    {
        var entity = await dbContext.Addresses.FindAsync([id], cancellationToken);
        if (entity is null) return false;
        entity.Building = request.Building; entity.AddressLine1 = request.AddressLine1.Trim(); entity.AddressLine2 = request.AddressLine2;
        entity.City = request.City; entity.State = request.State; entity.PostalCode = request.PostalCode; entity.CountryId = request.CountryId;
        entity.ModifiedDate = timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetCustomerIdsForAddressAsync(int addressId, CancellationToken cancellationToken) =>
        await dbContext.Customers.AsNoTracking()
            .Where(customer => customer.BillingAddressId == addressId || customer.ShippingAddressId == addressId)
            .Select(customer => customer.Id)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> DeleteAddressAsync(int customerId, int addressId, CancellationToken cancellationToken)
    {
        var owns = await dbContext.Customers.AnyAsync(value => value.Id == customerId && (value.BillingAddressId == addressId || value.ShippingAddressId == addressId), cancellationToken);
        if (!owns) return false;
        var affected = await dbContext.Addresses.Where(value => value.Id == addressId).ExecuteDeleteAsync(cancellationToken);
        return affected == 1;
    }

    /// <inheritdoc />
    public Task<CompanyResponse?> GetCompanyAsync(int id, CancellationToken cancellationToken) =>
        dbContext.Companies.AsNoTracking().Where(value => value.Id == id)
            .Select(value => new CompanyResponse(value.Id, value.Name, value.TaxNumber, value.Registrar, value.CreatedDate, value.ModifiedDate))
            .SingleOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Company> CreateCompanyAsync(UpsertCompanyRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entity = new Company { Name = request.Name.Trim(), TaxNumber = request.TaxNumber, Registrar = request.Registrar, CreatedDate = now, ModifiedDate = now };
        dbContext.Companies.Add(entity); await dbContext.SaveChangesAsync(cancellationToken); return entity;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateCompanyAsync(int id, UpsertCompanyRequest request, CancellationToken cancellationToken)
    {
        var entity = await dbContext.Companies.FindAsync([id], cancellationToken); if (entity is null) return false;
        entity.Name = request.Name.Trim(); entity.TaxNumber = request.TaxNumber; entity.Registrar = request.Registrar;
        entity.ModifiedDate = timeProvider.GetUtcNow().UtcDateTime; await dbContext.SaveChangesAsync(cancellationToken); return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetCustomerIdsForCompanyAsync(int companyId, CancellationToken cancellationToken) =>
        await dbContext.Customers.AsNoTracking()
            .Where(customer => customer.CompanyId == companyId)
            .Select(customer => customer.Id)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> DeleteCompanyAsync(int id, CancellationToken cancellationToken) =>
        await dbContext.Companies.Where(value => value.Id == id).ExecuteDeleteAsync(cancellationToken) == 1;

    private IQueryable<CustomerResponse> CustomerQuery() => Project(dbContext.Customers.AsNoTracking());

    private static IQueryable<CustomerResponse> Project(IQueryable<Customer> query) => query.Select(customer => new CustomerResponse(
        customer.Id, customer.FirstName, customer.LastName, customer.FullName, customer.Telephone, customer.Mobile, customer.Fax,
        customer.Email, customer.DateOfBirth, customer.CompanyId, customer.BillingAddressId, customer.ShippingAddressId,
        customer.CreatedDate, customer.ModifiedDate,
        customer.BillingAddress == null ? null : new AddressResponse(customer.BillingAddress.Id, customer.BillingAddress.Building, customer.BillingAddress.AddressLine1, customer.BillingAddress.AddressLine2, customer.BillingAddress.City, customer.BillingAddress.State, customer.BillingAddress.PostalCode, customer.BillingAddress.CountryId, customer.BillingAddress.CreatedDate, customer.BillingAddress.ModifiedDate),
        customer.Company == null ? null : new CompanyResponse(customer.Company.Id, customer.Company.Name, customer.Company.TaxNumber, customer.Company.Registrar, customer.Company.CreatedDate, customer.Company.ModifiedDate),
        customer.ShippingAddress == null ? null : new AddressResponse(customer.ShippingAddress.Id, customer.ShippingAddress.Building, customer.ShippingAddress.AddressLine1, customer.ShippingAddress.AddressLine2, customer.ShippingAddress.City, customer.ShippingAddress.State, customer.ShippingAddress.PostalCode, customer.ShippingAddress.CountryId, customer.ShippingAddress.CreatedDate, customer.ShippingAddress.ModifiedDate)));

    private static System.Linq.Expressions.Expression<Func<Address, AddressResponse>> ToAddress() => address => new AddressResponse(
        address.Id, address.Building, address.AddressLine1, address.AddressLine2, address.City, address.State, address.PostalCode,
        address.CountryId, address.CreatedDate, address.ModifiedDate);
}
