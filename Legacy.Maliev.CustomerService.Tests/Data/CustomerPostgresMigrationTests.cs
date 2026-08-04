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

    private CustomerDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CustomerDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;
        return new CustomerDbContext(options);
    }
}
