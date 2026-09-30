using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.CustomerService.Data;

/// <summary>Atomic graph reads, fill-missing mutations, and durable owner-bound replay.</summary>
public sealed class QuotationProfileCompletionStore(CustomerDbContext db)
{
    /// <summary>Reads a consistent uncached graph and aggregate revision.</summary>
    public async Task<QuotationProfileCompletionGraph?> ReadAsync(int id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var revision = await RevisionAsync(id, cancellationToken);
        var customer = await Graph(id).SingleOrDefaultAsync(cancellationToken);
        if (customer is null || revision is null) return null;
        var response = new CustomerRepository(db, TimeProvider.System);
        var profile = await response.GetCustomerAsync(id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(profile!, revision);
    }

    /// <summary>Commits completion and receipt together; retries return the original receipt before revision checking.</summary>
    public async Task<QuotationProfileCompletionResult> CompleteAsync(int id, string actor, Guid key, string revision,
        QuotationProfileCompletionRequest request, CancellationToken cancellationToken)
    {
        request = Normalize(request);
        var actorHash = Hash(actor);
        var requestHash = Hash(JsonSerializer.Serialize(request));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"QuotationProfileCompletionOperation\" (\"CustomerId\", \"Key\", \"ActorHash\", \"RequestHash\", \"CompletionId\", \"Changed\", \"CreatedAt\") VALUES ({id}, {key}, {actorHash}, {requestHash}, {Guid.NewGuid()}, {false}, {DateTime.UtcNow}) ON CONFLICT (\"CustomerId\", \"Key\") DO NOTHING", cancellationToken);
        if (inserted == 0)
        {
            var previous = await db.QuotationProfileCompletionOperations.AsNoTracking().SingleAsync(x => x.CustomerId == id && x.Key == key, cancellationToken);
            return previous.ActorHash == actorHash && previous.RequestHash == requestHash
                ? new(200, new(id, previous.CompletionId, previous.Changed)) : new(409, null);
        }

        var customer = await db.Customers.FromSqlInterpolated($"SELECT *, xmin FROM \"Customer\" WHERE \"ID\" = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (customer is null) return new(404, null);
        // Lock shared related rows in a stable order before reading their values. Normal legacy
        // replacements take the same PostgreSQL row locks and cannot race the missing-field merge.
        if (customer.CompanyId is int companyId)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Company\" WHERE \"ID\" = {companyId} FOR UPDATE", cancellationToken);
        foreach (var addressId in new[] { customer.BillingAddressId, customer.ShippingAddressId }.OfType<int>().Distinct().Order())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Address\" WHERE \"ID\" = {addressId} FOR UPDATE", cancellationToken);
        if (await RevisionAsync(id, cancellationToken) != revision) return new(412, null);
        customer = await Graph(id).SingleAsync(cancellationToken);

        customer.FirstName = Fill(customer.FirstName, request.FirstName) ?? string.Empty;
        customer.LastName = Fill(customer.LastName, request.LastName) ?? string.Empty;
        customer.Telephone = Fill(customer.Telephone, request.Telephone);
        customer.Mobile = Fill(customer.Mobile, request.Mobile);
        if (customer.Company is null && (request.Company is not null || request.TaxNumber is not null))
            customer.Company = new Company { Name = request.Company ?? string.Empty, TaxNumber = request.TaxNumber };
        else if (customer.Company is not null)
        {
            customer.Company.Name = Fill(customer.Company.Name, request.Company) ?? string.Empty;
            customer.Company.TaxNumber = Fill(customer.Company.TaxNumber, request.TaxNumber);
        }
        if (customer.BillingAddress is null)
        {
            if (!CompleteAddress(request.Billing)) return new(400, null);
            customer.BillingAddress = NewAddress(request.Billing!);
        }
        else ApplyAddress(customer.BillingAddress, request.Billing);

        // A stored shipping record is authoritative even if the browser posts same-as-billing.
        if (customer.ShippingAddress is not null)
            ApplyAddress(customer.ShippingAddress, request.Shipping);
        else if (request.ShipToBillingAddress)
            customer.ShippingAddress = customer.BillingAddress;
        else
        {
            if (!CompleteAddress(request.Shipping)) return new(400, null);
            customer.ShippingAddress = NewAddress(request.Shipping!);
        }

        db.ChangeTracker.DetectChanges();
        await IsolateSharedChangesAsync(customer, cancellationToken);
        db.ChangeTracker.DetectChanges();
        var changed = db.ChangeTracker.Entries().Any(x => x.State is EntityState.Modified or EntityState.Added);
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        foreach (var entry in db.ChangeTracker.Entries().Where(x => x.State is EntityState.Modified or EntityState.Added))
        {
            entry.Property("ModifiedDate").CurrentValue = now;
            if (entry.State == EntityState.Added) entry.Property("CreatedDate").CurrentValue = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        var operation = await db.QuotationProfileCompletionOperations.SingleAsync(x => x.CustomerId == id && x.Key == key, cancellationToken);
        operation.Changed = changed;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(200, new(id, operation.CompletionId, changed));
    }

    private IQueryable<Customer> Graph(int id) => db.Customers.Include(x => x.Company).Include(x => x.BillingAddress).Include(x => x.ShippingAddress).Where(x => x.Id == id);

    private async Task IsolateSharedChangesAsync(Customer customer, CancellationToken token)
    {
        if (customer.Company is { Id: > 0 } company && db.Entry(company).State == EntityState.Modified &&
            await db.Customers.AnyAsync(x => x.Id != customer.Id && x.CompanyId == company.Id, token))
        {
            var copy = new Company { Name = company.Name, TaxNumber = company.TaxNumber, Registrar = company.Registrar };
            await db.Entry(company).ReloadAsync(token);
            customer.Company = copy;
        }
        foreach (var address in new[] { customer.BillingAddress, customer.ShippingAddress }.OfType<Address>().Distinct().ToArray())
        {
            if (address.Id <= 0 || db.Entry(address).State != EntityState.Modified ||
                !await db.Customers.AnyAsync(x => x.Id != customer.Id && (x.BillingAddressId == address.Id || x.ShippingAddressId == address.Id), token)) continue;
            var copy = new Address
            {
                Building = address.Building,
                AddressLine1 = address.AddressLine1,
                AddressLine2 = address.AddressLine2,
                City = address.City,
                State = address.State,
                PostalCode = address.PostalCode,
                CountryId = address.CountryId
            };
            var billing = ReferenceEquals(customer.BillingAddress, address);
            var shipping = ReferenceEquals(customer.ShippingAddress, address);
            await db.Entry(address).ReloadAsync(token);
            if (billing) customer.BillingAddress = copy;
            if (shipping) customer.ShippingAddress = copy;
        }
    }

    private async Task<string?> RevisionAsync(int id, CancellationToken token)
    {
        var version = await db.Database.SqlQuery<string>($"SELECT concat(c.\"ID\", ':', c.xmin::text, ':', p.\"ID\", ':', p.xmin::text, ':', b.\"ID\", ':', b.xmin::text, ':', s.\"ID\", ':', s.xmin::text) AS \"Value\" FROM \"Customer\" c LEFT JOIN \"Company\" p ON p.\"ID\" = c.\"CompanyID\" LEFT JOIN \"Address\" b ON b.\"ID\" = c.\"BillingAddressID\" LEFT JOIN \"Address\" s ON s.\"ID\" = c.\"ShippingAddressID\" WHERE c.\"ID\" = {id}").SingleOrDefaultAsync(token);
        return version is null ? null : $"\"{Hash(version)}\"";
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Fill(string? stored, string? candidate) => string.IsNullOrWhiteSpace(stored) ? candidate : stored;
    private static bool CompleteAddress(QuotationProfileCompletionAddress? address) => address is { AddressLine1: not null, City: not null, State: not null, PostalCode: not null, CountryId: > 0 };
    private static Address NewAddress(QuotationProfileCompletionAddress value) => new() { AddressLine1 = value.AddressLine1!, Building = value.Building, AddressLine2 = value.AddressLine2, City = value.City, State = value.State, PostalCode = value.PostalCode, CountryId = value.CountryId };
    private static void ApplyAddress(Address address, QuotationProfileCompletionAddress? value)
    {
        if (value is null) return;
        address.Building = Fill(address.Building, value.Building); address.AddressLine1 = Fill(address.AddressLine1, value.AddressLine1) ?? string.Empty;
        address.AddressLine2 = Fill(address.AddressLine2, value.AddressLine2); address.City = Fill(address.City, value.City);
        address.State = Fill(address.State, value.State); address.PostalCode = Fill(address.PostalCode, value.PostalCode);
        if (address.CountryId <= 0) address.CountryId = value.CountryId;
    }
    private static QuotationProfileCompletionAddress? NormalizeAddress(QuotationProfileCompletionAddress? value) => value is null ? null : value with
    { Building = Clean(value.Building), AddressLine1 = Clean(value.AddressLine1), AddressLine2 = Clean(value.AddressLine2), City = Clean(value.City), State = Clean(value.State), PostalCode = Clean(value.PostalCode) };
    private static QuotationProfileCompletionRequest Normalize(QuotationProfileCompletionRequest value) => value with
    { FirstName = Clean(value.FirstName), LastName = Clean(value.LastName), Telephone = Clean(value.Telephone), Mobile = Clean(value.Mobile), Company = Clean(value.Company), TaxNumber = Clean(value.TaxNumber), Billing = NormalizeAddress(value.Billing), Shipping = NormalizeAddress(value.Shipping) };
}

/// <summary>Uncached consistent customer graph and strong aggregate ETag.</summary>
public sealed record QuotationProfileCompletionGraph(CustomerResponse Customer, string ETag);
/// <summary>HTTP-mapped completion outcome; rejected outcomes have no receipt or mutation.</summary>
public sealed record QuotationProfileCompletionResult(int Status, QuotationProfileCompletionReceipt? Receipt);
