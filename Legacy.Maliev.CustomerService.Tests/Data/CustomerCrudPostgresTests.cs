using Legacy.Maliev.CustomerService.Application.Interfaces;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Application.Services;
using Legacy.Maliev.CustomerService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.CustomerService.Tests.Data;

public sealed class CustomerCrudPostgresTests(CustomerCrudPostgresFixture fixture) : IClassFixture<CustomerCrudPostgresFixture>
{
    [Fact]
    public async Task CustomerGraph_CRUD_PreservesRelationshipsTimestampsAndCacheBoundaries()
    {
        await using var db = fixture.CreateDbContext();
        await db.Database.MigrateAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 1, 2, 3, TimeSpan.Zero));
        var cache = new Mock<ICustomerCache>();
        var service = new CustomerApplicationService(new CustomerRepository(db, clock), cache.Object);
        var cancellation = CancellationToken.None;
        var company = await service.CreateCompanyAsync(new(" บริษัทตัวอย่าง ", "0100000000000", "Registrar"), cancellation);
        Assert.Equal("บริษัทตัวอย่าง", company.Name);
        Assert.Equal(new DateTime(2026, 9, 1, 1, 2, 3), company.CreatedDate);
        var request = new UpsertCustomerRequest(" ทดสอบ ", " ตัวอย่าง ", "02-000-0000", "080-000-0000", "02-000-0001",
            " sample@example.test ", new DateTime(1990, 1, 2), company.Id, null, null);
        var customer = await service.CreateCustomerAsync(request, cancellation);
        Assert.Equal("ทดสอบ ตัวอย่าง", customer.FullName);
        Assert.Equal("sample@example.test", customer.Email);
        Assert.Equal("02-000-0001", customer.Fax);
        Assert.Equal(new DateTime(1990, 1, 2), customer.DateOfBirth);
        Assert.Equal(customer.CreatedDate, customer.ModifiedDate);
        Assert.Equal(company.Id, customer.Company?.Id);
        Assert.Equal(customer.Id, (await service.GetCustomerByEmailAsync(" SAMPLE@EXAMPLE.TEST ", cancellation))?.Id);
        Assert.Equal(customer.Id, (await service.GetCustomerAsync(customer.Id, cancellation))?.Id);
        cache.Verify(value => value.SetAsync(It.Is<CustomerResponse>(result => result.Id == customer.Id), cancellation), Times.Once);
        Assert.Null(await service.GetCustomerAsync(int.MaxValue, cancellation));
        Assert.Null(await service.GetCustomerByEmailAsync("missing@example.test", cancellation));

        var addressRequest = new UpsertAddressRequest("อาคาร", " 1 ถนนตัวอย่าง ", "ชั้น 2", "กรุงเทพมหานคร", "กรุงเทพมหานคร", "10110", 764);
        var address = await service.CreateAddressAsync(customer.Id, addressRequest, cancellation);
        Assert.NotNull(address);
        Assert.Equal("1 ถนนตัวอย่าง", address.AddressLine1);
        Assert.Equal("อาคาร", address.Building);
        Assert.Equal("ชั้น 2", address.AddressLine2);
        Assert.Equal("กรุงเทพมหานคร", address.City);
        Assert.Equal("กรุงเทพมหานคร", address.State);
        Assert.Equal("10110", address.PostalCode);
        Assert.Equal(764, address.CountryId);
        Assert.Equal(company.CreatedDate, address.CreatedDate);
        Assert.Equal(address.CreatedDate, address.ModifiedDate);
        Assert.Null(await service.GetAddressAsync(customer.Id, address.Id, cancellation));
        clock.Advance(TimeSpan.FromHours(1));
        request = request with { FirstName = " ใหม่ ", BillingAddressId = address.Id, ShippingAddressId = address.Id };
        Assert.True(await service.UpdateCustomerAsync(customer.Id, request, cancellation));
        db.ChangeTracker.Clear();
        var updated = await service.GetCustomerAsync(customer.Id, cancellation);
        Assert.Equal("ใหม่", updated?.FirstName);
        Assert.Equal(customer.CreatedDate, updated?.CreatedDate);
        Assert.Equal(new DateTime(2026, 9, 1, 2, 2, 3), updated?.ModifiedDate);
        Assert.Equal(address.Id, updated?.BillingAddress?.Id);
        Assert.Equal(address.Id, updated?.ShippingAddress?.Id);
        Assert.Equal(address.Id, (await service.GetAddressAsync(customer.Id, address.Id, cancellation))?.Id);
        Assert.Null(await service.GetAddressAsync(int.MaxValue, address.Id, cancellation));
        Assert.Single(await service.GetAddressesAsync(cancellation));
        cache.Invocations.Clear();
        Assert.True(await service.UpdateAddressAsync(address.Id, addressRequest with { AddressLine1 = " 2 ถนนใหม่ " }, cancellation));
        Assert.Equal("2 ถนนใหม่", (await service.GetAddressAsync(customer.Id, address.Id, cancellation))?.AddressLine1);
        cache.Verify(value => value.RemoveAsync(customer.Id, cancellation), Times.Once);
        cache.Invocations.Clear();
        Assert.True(await service.UpdateCompanyAsync(company.Id, new("บริษัทใหม่", "0200000000000", "Registrar updated"), cancellation));
        var updatedCompany = await service.GetCompanyAsync(company.Id, cancellation);
        Assert.Equal("บริษัทใหม่", updatedCompany?.Name);
        Assert.Equal("Registrar updated", updatedCompany?.Registrar);
        Assert.Equal(company.CreatedDate, updatedCompany?.CreatedDate);
        Assert.Equal(new DateTime(2026, 9, 1, 2, 2, 3), updatedCompany?.ModifiedDate);
        cache.Verify(value => value.RemoveAsync(customer.Id, cancellation), Times.Once);
        Assert.True(await service.UpdateInternalRemarkAsync(customer.Id, new(" staff only "), cancellation));
        Assert.Equal("staff only", (await service.GetInternalRemarkAsync(customer.Id, cancellation))?.InternalRemark);

        foreach (var sort in Enum.GetValues<CustomerSortType>())
        {
            var page = await service.GetCustomersAsync(sort, null, 0, 1000, cancellation);
            Assert.NotNull(page);
            Assert.Equal(customer.Id, Assert.Single(page.Items).Id);
            Assert.Equal(1, page.PageIndex);
            Assert.Equal(1, page.TotalRecords);
            Assert.Equal(1, page.TotalPages);
            Assert.False(page.HasNextPage);
            Assert.False(page.HasPreviousPage);
        }
        Assert.Equal(customer.Id, Assert.Single((await service.GetCustomersAsync(null, "บริษัทใหม่", null, null, cancellation))!.Items).Id);
        Assert.Null(await service.GetCustomersAsync(null, "does-not-exist", null, null, cancellation));

        cache.Invocations.Clear();
        Assert.Null(await service.CreateAddressAsync(int.MaxValue, addressRequest, cancellation));
        Assert.False(await service.UpdateCustomerAsync(int.MaxValue, request, cancellation));
        Assert.False(await service.UpdateInternalRemarkAsync(int.MaxValue, new("missing"), cancellation));
        Assert.False(await service.UpdateAddressAsync(int.MaxValue, addressRequest, cancellation));
        Assert.False(await service.UpdateCompanyAsync(int.MaxValue, new("missing", null, null), cancellation));
        Assert.False(await service.DeleteCustomerAsync(int.MaxValue, cancellation));
        Assert.False(await service.DeleteCompanyAsync(int.MaxValue, cancellation));
        Assert.False(await service.DeleteAddressAsync(int.MaxValue, address.Id, cancellation));
        cache.VerifyNoOtherCalls();

        // The migrated source schema deliberately retains NO ACTION relations.
        // Referenced records must not disappear or invalidate cache after a failed delete.
        var companyDelete = await Assert.ThrowsAsync<PostgresException>(() => service.DeleteCompanyAsync(company.Id, cancellation));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, companyDelete.SqlState);
        var addressDelete = await Assert.ThrowsAsync<PostgresException>(() => service.DeleteAddressAsync(customer.Id, address.Id, cancellation));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, addressDelete.SqlState);
        cache.VerifyNoOtherCalls();
        Assert.True(await service.DeleteCustomerAsync(customer.Id, cancellation));
        cache.Verify(value => value.RemoveAsync(customer.Id, cancellation), Times.Once);
        Assert.True(await service.DeleteCompanyAsync(company.Id, cancellation));
        Assert.Null(await service.GetCompanyAsync(company.Id, cancellation));
        Assert.Empty(await db.Customers.ToListAsync());
        Assert.Empty(await db.Companies.ToListAsync());
        Assert.Single(await db.Addresses.ToListAsync());
    }
}

public sealed class CustomerCrudPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();
    public CustomerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<CustomerDbContext>()
        .UseNpgsql(postgres.GetConnectionString()).Options);
}
