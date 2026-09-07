using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Data;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.CustomerService.Tests.Data;

public sealed class CustomerPostgresMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public Task InitializeAsync() => postgres.StartAsync();

    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task InitialMigration_PreservesLegacyNamesRelationsAndComputedFullName()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();

        var address = new Address { AddressLine1 = "1 Legacy Road", CountryId = 764 };
        var company = new Company { Name = "MALIEV" };
        dbContext.Add(address);
        dbContext.Add(company);
        await dbContext.SaveChangesAsync();

        dbContext.Add(new Customer
        {
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "Ada@Example.com",
            BillingAddressId = address.Id,
            ShippingAddressId = address.Id,
            CompanyId = company.Id,
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var repository = new CustomerRepository(dbContext, TimeProvider.System);
        var customer = await repository.GetCustomerByEmailAsync("ada@example.com", CancellationToken.None);
        var addressOwners = await repository.GetCustomerIdsForAddressAsync(address.Id, CancellationToken.None);
        var companyOwners = await repository.GetCustomerIdsForCompanyAsync(company.Id, CancellationToken.None);

        Assert.NotNull(customer);
        Assert.Equal("Ada Lovelace", customer.FullName);
        Assert.Equal(address.Id, customer.BillingAddressId);
        Assert.Equal(address.Id, customer.ShippingAddressId);
        Assert.Equal(company.Id, customer.CompanyId);
        Assert.Equal([customer.Id], addressOwners);
        Assert.Equal([customer.Id], companyOwners);
        Assert.Equal(3, await dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN ('Address', 'Company', 'Customer')")
            .SingleAsync());
    }

    [Fact]
    public async Task LatestMigration_AddsNullableBoundedInternalRemarkColumn()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();

        var column = await dbContext.Database.SqlQueryRaw<InternalRemarkColumn>(
            """
            SELECT
                column_name AS "ColumnName",
                character_maximum_length::int AS "MaximumLength",
                is_nullable AS "IsNullable"
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'Customer'
              AND column_name = 'InternalRemark'
            """).SingleOrDefaultAsync();

        Assert.NotNull(column);
        Assert.Equal("InternalRemark", column.ColumnName);
        Assert.Equal(4000, column.MaximumLength);
        Assert.Equal("YES", column.IsNullable);
    }

    [Fact]
    public async Task InternalRemark_RoundTripsSeparatelyFromPublicCustomerProjection()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
        var customer = new Customer
        {
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "ada.internal@example.com",
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();
        var repository = new CustomerRepository(dbContext, TimeProvider.System);

        var updated = await repository.UpdateInternalRemarkAsync(
            customer.Id,
            new UpdateCustomerInternalRemarkRequest("  Confirm billing contact.  "),
            CancellationToken.None);
        dbContext.ChangeTracker.Clear();
        var remark = await repository.GetInternalRemarkAsync(customer.Id, CancellationToken.None);
        var publicProfile = await repository.GetCustomerAsync(customer.Id, CancellationToken.None);

        Assert.True(updated);
        Assert.Equal("Confirm billing contact.", remark?.InternalRemark);
        Assert.NotNull(publicProfile);
        Assert.DoesNotContain(publicProfile.GetType().GetProperties(), property =>
            property.Name.Contains("Remark", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetCustomerAsync_FiltersBeforeProjectingNestedLegacyRelations()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();

        var address = new Address { AddressLine1 = "1 Profile Road", CountryId = 764 };
        var company = new Company { Name = "Analytical Engines" };
        dbContext.AddRange(address, company);
        await dbContext.SaveChangesAsync();

        var entity = new Customer
        {
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "ada.profile@example.com",
            BillingAddressId = address.Id,
            ShippingAddressId = address.Id,
            CompanyId = company.Id,
        };
        dbContext.Add(entity);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var repository = new CustomerRepository(dbContext, TimeProvider.System);

        var customer = await repository.GetCustomerAsync(entity.Id, CancellationToken.None);

        Assert.NotNull(customer);
        Assert.Equal("Analytical Engines", customer.Company?.Name);
        Assert.Equal("1 Profile Road", customer.BillingAddress?.AddressLine1);
        Assert.Equal("1 Profile Road", customer.ShippingAddress?.AddressLine1);
    }

    [Fact]
    public async Task GetCustomersAsync_SearchesMigratedNondeterministicCollations()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE COLLATION customer_search_nondeterministic
                (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
            ALTER TABLE "Company"
                ALTER COLUMN "Name" TYPE character varying(256)
                COLLATE customer_search_nondeterministic;
            """);

        var company = new Company { Name = "บริษัท มาลีฟ จำกัด" };
        dbContext.Add(company);
        await dbContext.SaveChangesAsync();
        dbContext.Add(new Customer
        {
            FirstName = "ธีระ",
            LastName = "ทิพอาสน์",
            Email = "customer-search@example.com",
            CompanyId = company.Id,
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var repository = new CustomerRepository(dbContext, TimeProvider.System);

        var result = await repository.GetCustomersAsync(
            CustomerSortType.CustomerId_Descending,
            "มาลีฟ",
            pageIndex: 1,
            pageSize: 25,
            CancellationToken.None);

        Assert.NotNull(result);
        var customer = Assert.Single(result.Items);
        Assert.Equal("customer-search@example.com", customer.Email);
        Assert.Equal("บริษัท มาลีฟ จำกัด", customer.Company?.Name);
    }

    [Fact]
    public async Task CreateCompanyAsync_UsesUtcWallClockForTimestampWithoutTimeZoneColumns()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();

        var repository = new CustomerRepository(dbContext, TimeProvider.System);

        var company = await repository.CreateCompanyAsync(
            new UpsertCompanyRequest("UTC wall-clock company", null, null),
            CancellationToken.None);

        Assert.NotNull(company.CreatedDate);
        Assert.NotNull(company.ModifiedDate);
        Assert.Equal(DateTimeKind.Unspecified, company.CreatedDate.Value.Kind);
        Assert.Equal(DateTimeKind.Unspecified, company.ModifiedDate.Value.Kind);
    }

    [Fact]
    public async Task ProvisionInstantQuotationProfile_NewGuest_CommitsCompleteShipToBillingProfileAtomically()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
        var repository = new CustomerRepository(dbContext, TimeProvider.System);
        var request = new InstantQuotationCustomerProfileRequest(
            " Ada ",
            " Lovelace ",
            " Ada.IQ@Example.com ",
            "02-123-4567",
            "081-234-5678",
            " Analytical Engines ",
            "0115562011815 (สำนักงานใหญ่)",
            new InstantQuotationAddressInput("Engine House", "1 Billing Road", null, "Bangkok", "Bangkok", "10110", 764),
            Shipping: null,
            ShipToBillingAddress: true);

        var result = await repository.ProvisionInstantQuotationProfileAsync(request, CancellationToken.None);
        dbContext.ChangeTracker.Clear();
        var persisted = await repository.GetCustomerAsync(result.CustomerId, CancellationToken.None);

        Assert.True(result.CustomerCreated);
        Assert.NotNull(persisted);
        Assert.Equal("Ada", persisted.FirstName);
        Assert.Equal("ada.iq@example.com", persisted.Email, ignoreCase: true);
        Assert.Equal("081-234-5678", persisted.Mobile);
        Assert.Equal(persisted.BillingAddressId, persisted.ShippingAddressId);
        Assert.Equal("1 Billing Road", persisted.BillingAddress?.AddressLine1);
        Assert.Equal("Analytical Engines", persisted.Company?.Name);
        Assert.Equal("0115562011815 (สำนักงานใหญ่)", persisted.Company?.TaxNumber);
        Assert.Equal(1, await dbContext.Customers.CountAsync());
        Assert.Equal(1, await dbContext.Addresses.CountAsync());
        Assert.Equal(1, await dbContext.Companies.CountAsync());
    }

    [Fact]
    public async Task ProvisionInstantQuotationProfile_ConcurrentSameEmail_ReturnsOneDeterministicCustomer()
    {
        await using var setup = CreateDbContext();
        await setup.Database.MigrateAsync();
        var request = new InstantQuotationCustomerProfileRequest(
            "Grace",
            "Hopper",
            "concurrent.iq@example.com",
            null,
            "0890000000",
            null,
            null,
            new InstantQuotationAddressInput(null, "1 Billing Road", null, "Bangkok", "Bangkok", "10110", 764),
            null,
            true);

        await using var firstContext = CreateDbContext();
        await using var secondContext = CreateDbContext();
        var firstRepository = new CustomerRepository(firstContext, TimeProvider.System);
        var secondRepository = new CustomerRepository(secondContext, TimeProvider.System);
        var results = await Task.WhenAll(
            firstRepository.ProvisionInstantQuotationProfileAsync(request, CancellationToken.None),
            secondRepository.ProvisionInstantQuotationProfileAsync(request, CancellationToken.None));

        Assert.Equal(results[0].CustomerId, results[1].CustomerId);
        Assert.Single(results, result => result.CustomerCreated);
        Assert.Single(results, result => !result.CustomerCreated);
        await using var verification = CreateDbContext();
        Assert.Equal(1, await verification.Customers.CountAsync(customer => customer.Email.ToLower() == "concurrent.iq@example.com"));
        Assert.Equal(1, await verification.Addresses.CountAsync());
    }

    [Fact]
    public async Task ProvisionInstantQuotationProfile_LegacyDuplicateEmail_SelectsLowestIdWithoutCreatingResources()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
        dbContext.Customers.AddRange(
            new Customer { Id = 91, FirstName = "Later", LastName = "Import", Email = "DUPLICATE.IQ@example.com" },
            new Customer { Id = 17, FirstName = "Earlier", LastName = "Import", Email = "duplicate.iq@example.com" });
        await dbContext.SaveChangesAsync();
        var repository = new CustomerRepository(dbContext, TimeProvider.System);

        var result = await repository.ProvisionInstantQuotationProfileAsync(
            new InstantQuotationCustomerProfileRequest(
                "Ignored",
                "Input",
                " duplicate.iq@EXAMPLE.com ",
                null,
                null,
                null,
                null,
                new InstantQuotationAddressInput(null, "1 Should Not Persist", null, null, null, null, 764),
                null,
                true),
            CancellationToken.None);

        Assert.Equal(17, result.CustomerId);
        Assert.False(result.CustomerCreated);
        Assert.Equal(2, await dbContext.Customers.CountAsync());
        Assert.Empty(await dbContext.Addresses.ToListAsync());
        Assert.Empty(await dbContext.Companies.ToListAsync());
    }

    [Fact]
    public async Task ProvisionInstantQuotationProfile_SeparateShipping_PersistsTwoLinkedAddresses()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
        var repository = new CustomerRepository(dbContext, TimeProvider.System);

        var result = await repository.ProvisionInstantQuotationProfileAsync(
            new InstantQuotationCustomerProfileRequest(
                "Katherine",
                "Johnson",
                "katherine.iq@example.com",
                null,
                "081-000-0000",
                null,
                null,
                new InstantQuotationAddressInput(null, "1 Billing Road", null, "Bangkok", "Bangkok", "10110", 764),
                new InstantQuotationAddressInput(null, "2 Shipping Road", null, "Nonthaburi", "Nonthaburi", "11000", 764),
                false),
            CancellationToken.None);
        dbContext.ChangeTracker.Clear();
        var persisted = await repository.GetCustomerAsync(result.CustomerId, CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.NotEqual(persisted.BillingAddressId, persisted.ShippingAddressId);
        Assert.Equal("1 Billing Road", persisted.BillingAddress?.AddressLine1);
        Assert.Equal("2 Shipping Road", persisted.ShippingAddress?.AddressLine1);
        Assert.Equal(2, await dbContext.Addresses.CountAsync());
    }

    private CustomerDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CustomerDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;
        return new CustomerDbContext(options);
    }

    private sealed record InternalRemarkColumn(string ColumnName, int MaximumLength, string IsNullable);

    [Fact]
    public async Task TaxOnlyCompany_ExistingSchemaPreservesBlankNameAndCustomerRelation()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
        var repository = new CustomerRepository(dbContext, TimeProvider.System);
        var company = await repository.CreateCompanyAsync(new("  ", "0100000000000", null), CancellationToken.None);
        dbContext.Customers.Add(new Customer { FirstName = "ทดสอบ", LastName = "ตัวอย่าง", CompanyId = company.Id });
        await dbContext.SaveChangesAsync();
        var customerId = await dbContext.Customers.Select(value => value.Id).SingleAsync();
        dbContext.ChangeTracker.Clear();
        var loaded = await repository.GetCompanyAsync(company.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("", loaded.Name);
        Assert.Equal("0100000000000", loaded.TaxNumber);
        Assert.NotNull(loaded.CreatedDate);
        Assert.True(await repository.UpdateCompanyAsync(company.Id, new("", "0200000000000", null), CancellationToken.None));
        dbContext.ChangeTracker.Clear();
        var customer = await repository.GetCustomerAsync(customerId, CancellationToken.None);
        Assert.NotNull(customer?.Company);
        Assert.Equal("", customer.Company.Name);
        Assert.Equal("0200000000000", customer.Company.TaxNumber);
        Assert.Equal([customerId], await repository.GetCustomerIdsForCompanyAsync(company.Id, CancellationToken.None));
        Assert.Equal("NO", await dbContext.Database.SqlQueryRaw<string>(
            "SELECT is_nullable AS \"Value\" FROM information_schema.columns WHERE table_name = 'Company' AND column_name = 'Name'").SingleAsync());
        Assert.False(dbContext.Database.HasPendingModelChanges());
    }
}
