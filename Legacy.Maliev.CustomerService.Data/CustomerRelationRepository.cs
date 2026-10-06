using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Legacy.Maliev.CustomerService.Data;

/// <summary>Atomic customer attachment and selected metadata edits in the existing owned database.</summary>
public sealed class CustomerRelationRepository(CustomerDbContext dbContext, TimeProvider timeProvider) : ICustomerRelationRepository
{
    /// <inheritdoc />
    public async Task<CustomerRelationVersionedResponse?> GetAsync(int customerId, CustomerRelationKind kind, int? relationId, CancellationToken cancellationToken)
    {
        var customer = await new CustomerRepository(dbContext, timeProvider).GetCustomerVersionedAsync(customerId, cancellationToken);
        if (customer is null) return null;
        var state = CustomerRelationVersion.From(customer, kind);
        return state.Relation.RelationId == relationId ? state : null;
    }

    /// <inheritdoc />
    public Task<CustomerRelationEditResult> SaveCompanyAsync(int customerId, int? relationId, string version, uint customerRevision, CustomerCompanyRelationRequest request, CancellationToken cancellationToken) =>
        SaveAsync(customerId, CustomerRelationKind.Company, relationId, version, customerRevision, request, null, cancellationToken);

    /// <inheritdoc />
    public Task<CustomerRelationEditResult> SaveAddressAsync(int customerId, CustomerRelationKind kind, int? relationId, string version, uint customerRevision, CustomerAddressRelationRequest request, CancellationToken cancellationToken)
    {
        if (kind is not (CustomerRelationKind.Billing or CustomerRelationKind.Shipping)) throw new ArgumentOutOfRangeException(nameof(kind));
        return SaveAsync(customerId, kind, relationId, version, customerRevision, null, request, cancellationToken);
    }

    private Task<CustomerRelationEditResult> SaveAsync(int customerId, CustomerRelationKind kind, int? expectedRelationId, string version,
        uint customerRevision, CustomerCompanyRelationRequest? company, CustomerAddressRelationRequest? address, CancellationToken cancellationToken) =>
        dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            var commitAttempted = false;
            int[] affected = [customerId];
            try
            {
                await using var fresh = new CustomerDbContext((DbContextOptions<CustomerDbContext>)dbContext.GetService<IDbContextOptions>());
                var writer = new CustomerRepository(fresh, timeProvider);
                await using var transaction = await fresh.Database.BeginTransactionAsync(cancellationToken);
                // xmin is explicitly selected because it is a mapped PostgreSQL system column, absent from SELECT *.
                var customer = await fresh.Customers.FromSqlInterpolated($"SELECT *, xmin FROM \"Customer\" WHERE \"ID\" = {customerId} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken);
                if (customer is null) return new CustomerRelationEditResult(CustomerRelationEditOutcome.NotFound);
                var actualRelationId = kind switch
                {
                    CustomerRelationKind.Company => customer.CompanyId,
                    CustomerRelationKind.Billing => customer.BillingAddressId,
                    _ => customer.ShippingAddressId,
                };
                if (actualRelationId is { } existingId)
                {
                    if (kind == CustomerRelationKind.Company)
                    {
                        if (await fresh.Companies.FromSqlInterpolated($"SELECT * FROM \"Company\" WHERE \"ID\" = {existingId} FOR UPDATE")
                            .SingleOrDefaultAsync(cancellationToken) is null)
                            return new CustomerRelationEditResult(CustomerRelationEditOutcome.NotFound);
                    }
                    else if (await fresh.Addresses.FromSqlInterpolated($"SELECT * FROM \"Address\" WHERE \"ID\" = {existingId} FOR UPDATE")
                        .SingleOrDefaultAsync(cancellationToken) is null)
                        return new CustomerRelationEditResult(CustomerRelationEditOutcome.NotFound);
                }
                var current = (await writer.GetCustomerVersionedAsync(customerId, cancellationToken))!;
                var state = CustomerRelationVersion.From(current, kind);
                if (current.Revision != customerRevision || !string.Equals(state.Version, version, StringComparison.Ordinal))
                    return new CustomerRelationEditResult(CustomerRelationEditOutcome.Stale);
                if (actualRelationId != expectedRelationId)
                    return new CustomerRelationEditResult(CustomerRelationEditOutcome.NotFound);

                int savedRelationId;
                if (company is not null)
                {
                    var request = new UpsertCompanyRequest(company.Name, company.TaxNumber, company.Registrar);
                    if (actualRelationId is { } existingCompany)
                    {
                        await writer.UpdateCompanyAsync(existingCompany, request, cancellationToken);
                        savedRelationId = existingCompany;
                    }
                    else savedRelationId = (await writer.CreateCompanyAsync(request, cancellationToken)).Id;
                    customer.CompanyId = savedRelationId;
                    affected = await fresh.Customers.AsNoTracking().Where(row => row.CompanyId == savedRelationId)
                        .Select(row => row.Id).ToArrayAsync(cancellationToken);
                }
                else
                {
                    var input = address!;
                    var request = new UpsertAddressRequest(input.Building, input.AddressLine1, input.AddressLine2, input.City, input.State, input.PostalCode, input.CountryId);
                    if (actualRelationId is { } existingAddress)
                    {
                        await writer.UpdateAddressAsync(existingAddress, request, cancellationToken);
                        savedRelationId = existingAddress;
                    }
                    else savedRelationId = (await writer.CreateAddressAsync(customerId, request, cancellationToken))!.Id;
                    if (kind == CustomerRelationKind.Billing) customer.BillingAddressId = savedRelationId;
                    else customer.ShippingAddressId = savedRelationId;
                    affected = await fresh.Customers.AsNoTracking().Where(row => row.BillingAddressId == savedRelationId || row.ShippingAddressId == savedRelationId)
                        .Select(row => row.Id).ToArrayAsync(cancellationToken);
                }
                affected = [.. affected.Append(customerId).Distinct()];
                customer.ModifiedDate = DateTime.SpecifyKind(timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified);
                await fresh.SaveChangesAsync(cancellationToken);
                var committedCustomer = (await writer.GetCustomerVersionedAsync(customerId, cancellationToken))!;
                var committed = CustomerRelationVersion.From(committedCustomer, kind);
                var sharedAddress = kind != CustomerRelationKind.Company &&
                    committedCustomer.Customer.BillingAddressId == committedCustomer.Customer.ShippingAddressId;
                var billingVersion = sharedAddress ? CustomerRelationVersion.From(committedCustomer, CustomerRelationKind.Billing).Version : null;
                var shippingVersion = sharedAddress ? CustomerRelationVersion.From(committedCustomer, CustomerRelationKind.Shipping).Version : null;
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
                return new CustomerRelationEditResult(CustomerRelationEditOutcome.Updated, committed.Version, affected,
                    $"\"{committed.CustomerRevision:x8}\"", billingVersion, shippingVersion, savedRelationId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && commitAttempted)
            {
                throw new CustomerRelationCanceledException(affected, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) when (commitAttempted)
            {
                throw new CustomerRelationUncertainException(affected);
            }
        });
}
