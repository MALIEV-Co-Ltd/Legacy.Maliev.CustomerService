using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Legacy.Maliev.CustomerService.Data;

/// <summary>Atomic graph reads, fill-missing mutations, and durable owner-bound replay.</summary>
public sealed class QuotationProfileCompletionStore(CustomerDbContext db)
{
    /// <summary>Proves the receipt shape, immediate conflict key and current-role access using bounded read-only metadata.</summary>
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await db.Database.SqlQuery<bool>($"""
                WITH target AS (
                  SELECT c.oid, n.oid AS schema_oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                  WHERE n.nspname = current_schema() AND c.relname = 'QuotationProfileCompletionOperation' AND c.relkind = 'r'
                  AND NOT c.relrowsecurity AND NOT c.relforcerowsecurity
                ), expected(name, type) AS (VALUES
                  ('CustomerId', 'integer'), ('Key', 'uuid'), ('ActorHash', 'character varying(64)'),
                  ('RequestHash', 'character varying(64)'), ('CompletionId', 'uuid'), ('Changed', 'boolean'), ('CreatedAt', 'timestamp with time zone'))
                SELECT EXISTS (SELECT 1 FROM target t WHERE
                  has_schema_privilege(t.schema_oid, 'USAGE') AND has_table_privilege(t.oid, 'SELECT')
                  AND has_table_privilege(t.oid, 'INSERT') AND has_table_privilege(t.oid, 'UPDATE')
                  AND NOT EXISTS (SELECT 1 FROM expected e WHERE NOT EXISTS (
                    SELECT 1 FROM pg_attribute a WHERE a.attrelid = t.oid AND a.attname = e.name
                    AND a.attnum > 0 AND NOT a.attisdropped AND a.attnotnull AND a.attgenerated = '' AND a.attidentity = ''
                    AND format_type(a.atttypid, a.atttypmod) = e.type))
                  AND NOT EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = t.oid AND a.attnum > 0 AND NOT a.attisdropped
                    AND a.attnotnull AND NOT a.atthasdef AND a.attidentity = '' AND NOT EXISTS (SELECT 1 FROM expected e WHERE e.name = a.attname))
                  AND NOT EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conrelid = t.oid AND c.contype NOT IN ('p', 'n'))
                  AND NOT EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid = t.oid AND i.indisunique AND NOT i.indisprimary)
                  AND NOT EXISTS (SELECT 1 FROM pg_trigger g WHERE g.tgrelid = t.oid AND NOT g.tgisinternal)
                  AND NOT EXISTS (SELECT 1 FROM pg_rewrite r WHERE r.ev_class = t.oid)
                  AND EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid = t.oid AND i.indisprimary AND i.indisunique AND i.indisvalid
                    AND i.indimmediate AND i.indpred IS NULL AND i.indexprs IS NULL AND i.indnkeyatts = 2
                    AND ARRAY(SELECT a.attname::text FROM unnest(i.indkey) WITH ORDINALITY AS k(number, position)
                      JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.number WHERE k.position <= 2 ORDER BY a.attname)
                      = ARRAY['CustomerId', 'Key'])) AS "Value"
                """).SingleAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Reads a consistent uncached graph and aggregate revision.</summary>
    public async Task<QuotationProfileCompletionGraph?> ReadAsync(int id, CancellationToken cancellationToken)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            await using var attempt = NewAttempt();
            return await new QuotationProfileCompletionStore(attempt).ReadOnceAsync(id, token);
        }, cancellationToken);
    }

    private async Task<QuotationProfileCompletionGraph?> ReadOnceAsync(int id, CancellationToken cancellationToken)
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
        QuotationProfileCompletionRequest request, CancellationToken cancellationToken, string? trustedEmail = null)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            // Rollback cannot undo EF's in-memory graph changes. Each retry owns a
            // fresh context; a lost commit acknowledgement replays the durable key.
            await using var attempt = NewAttempt();
            return await new QuotationProfileCompletionStore(attempt).CompleteOnceAsync(
                id, actor, key, revision, request, token, trustedEmail);
        }, cancellationToken);
    }

    private CustomerDbContext NewAttempt() => new((DbContextOptions<CustomerDbContext>)db.GetService<IDbContextOptions>());

    private async Task<QuotationProfileCompletionResult> CompleteOnceAsync(int id, string actor, Guid key, string revision,
        QuotationProfileCompletionRequest request, CancellationToken cancellationToken, string? trustedEmail)
    {
        request = Normalize(request);
        var actorHash = Hash(actor);
        // Freeze PR #30's exact property order/null representation. Optional
        // wire fields never silently alter old receipt meaning; branch folds into TaxNumber.
        var projection = new
        {
            request.FirstName,
            request.LastName,
            request.Telephone,
            request.Mobile,
            request.Company,
            request.TaxNumber,
            request.Billing,
            request.Shipping,
            request.ShipToBillingAddress
        };
        var legacyHash = Hash(JsonSerializer.Serialize(projection));
        var requestHash = trustedEmail is null ? legacyHash : Hash(JsonSerializer.Serialize(new { Version = 2, Request = projection, TrustedEmail = trustedEmail }));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"QuotationProfileCompletionOperation\" (\"CustomerId\", \"Key\", \"ActorHash\", \"RequestHash\", \"CompletionId\", \"Changed\", \"CreatedAt\") VALUES ({id}, {key}, {actorHash}, {requestHash}, {Guid.NewGuid()}, {false}, {DateTime.UtcNow}) ON CONFLICT (\"CustomerId\", \"Key\") DO NOTHING", cancellationToken);
        if (inserted == 0)
        {
            var previous = await db.QuotationProfileCompletionOperations.AsNoTracking().SingleAsync(x => x.CustomerId == id && x.Key == key, cancellationToken);
            // Old matching receipts can only be returned, before graph mutation. They
            // do not authorize adding email after the old operation was already committed.
            return previous.ActorHash == actorHash && (previous.RequestHash == requestHash || previous.RequestHash == legacyHash)
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
        if (trustedEmail is not null) customer.Email = Fill(customer.Email, trustedEmail) ?? string.Empty;
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
    {
        FirstName = Clean(value.FirstName),
        LastName = Clean(value.LastName),
        Telephone = Clean(value.Telephone),
        Mobile = Clean(value.Mobile),
        Company = Clean(value.Company),
        TaxNumber = Clean(value.TaxNumber) is not { } tax ? null : string.Equals(Clean(value.TaxBranch), "head-office", StringComparison.OrdinalIgnoreCase)
            ? $"{tax} (สำนักงานใหญ่)" : string.Equals(Clean(value.TaxBranch), "branch", StringComparison.OrdinalIgnoreCase) ? $"{tax} (สาขาที่ {value.TaxBranchCode})" : tax,
        TaxBranch = null,
        TaxBranchCode = null,
        Billing = NormalizeAddress(value.Billing),
        Shipping = NormalizeAddress(value.Shipping)
    };
}

/// <summary>Uncached consistent customer graph and strong aggregate ETag.</summary>
public sealed record QuotationProfileCompletionGraph(CustomerResponse Customer, string ETag);
/// <summary>HTTP-mapped completion outcome; rejected outcomes have no receipt or mutation.</summary>
public sealed record QuotationProfileCompletionResult(int Status, QuotationProfileCompletionReceipt? Receipt);
