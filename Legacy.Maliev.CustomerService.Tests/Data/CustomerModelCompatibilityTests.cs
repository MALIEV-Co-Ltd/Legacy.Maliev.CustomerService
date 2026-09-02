using Legacy.Maliev.CustomerService.Data;
using Legacy.Maliev.CustomerService.Application.Models;
using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Legacy.Maliev.CustomerService.Tests.Data;

public sealed class CustomerModelCompatibilityTests
{
    [Fact]
    public void Model_PreservesLegacyTablesColumnsLengthsAndRelationships()
    {
        var options = new DbContextOptionsBuilder<CustomerDbContext>().UseNpgsql("Host=localhost;Database=model").Options;
        using var context = new CustomerDbContext(options);

        var customer = context.Model.FindEntityType(typeof(Customer))!;
        var customerTable = StoreObjectIdentifier.Table("Customer", null);
        Assert.Equal("Customer", customer.GetTableName());
        Assert.Equal("ID", customer.FindProperty(nameof(Customer.Id))!.GetColumnName(customerTable));
        Assert.Equal("BillingAddressID", customer.FindProperty(nameof(Customer.BillingAddressId))!.GetColumnName(customerTable));
        Assert.Equal("ShippingAddressID", customer.FindProperty(nameof(Customer.ShippingAddressId))!.GetColumnName(customerTable));
        Assert.Equal("CompanyID", customer.FindProperty(nameof(Customer.CompanyId))!.GetColumnName(customerTable));
        Assert.Equal(513, customer.FindProperty(nameof(Customer.FullName))!.GetMaxLength());
        var internalRemark = customer.FindProperty("InternalRemark");
        Assert.NotNull(internalRemark);
        Assert.Equal("InternalRemark", internalRemark.GetColumnName(customerTable));
        Assert.Equal(4000, internalRemark.GetMaxLength());
        Assert.True(internalRemark.IsNullable);
        Assert.Null(customer.FindProperty("xmin"));

        Assert.Equal("FK_Customer_Address", customer.GetForeignKeys().Single(key => key.Properties.Single().Name == nameof(Customer.BillingAddressId)).GetConstraintName());
        Assert.Equal("FK_Customer_Address1", customer.GetForeignKeys().Single(key => key.Properties.Single().Name == nameof(Customer.ShippingAddressId)).GetConstraintName());
        Assert.Equal("FK_Customer_Company", customer.GetForeignKeys().Single(key => key.Properties.Single().Name == nameof(Customer.CompanyId)).GetConstraintName());

        foreach (var entityType in context.Model.GetEntityTypes().Where(entity =>
                     entity.ClrType == typeof(Address)
                     || entity.ClrType == typeof(Company)
                     || entity.ClrType == typeof(Customer)))
        {
            var created = entityType.FindProperty(nameof(Customer.CreatedDate))!;
            var modified = entityType.FindProperty(nameof(Customer.ModifiedDate))!;
            Assert.Equal("timestamp without time zone", created.GetColumnType());
            Assert.Equal("timestamp without time zone", modified.GetColumnType());
            Assert.Equal("CURRENT_TIMESTAMP AT TIME ZONE 'UTC'", created.GetDefaultValueSql());
            Assert.Equal("CURRENT_TIMESTAMP AT TIME ZONE 'UTC'", modified.GetDefaultValueSql());
        }
    }

    [Fact]
    public void PublicCustomerContracts_DoNotExposeInternalRemark()
    {
        Assert.DoesNotContain(typeof(CustomerResponse).GetProperties(), property =>
            property.Name.Contains("Remark", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(UpsertCustomerRequest).GetProperties(), property =>
            property.Name.Contains("Remark", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TimestampMigration_UsesExplicitUtcConversions()
    {
        var migration = Path.Combine(
            Path.GetDirectoryName(typeof(CustomerModelCompatibilityTests).Assembly.Location)!,
            "..", "..", "..", "..", "Legacy.Maliev.CustomerService.Data", "Migrations",
            "20260807140250_AlignUtcTimestampColumns.cs");
        var source = File.ReadAllText(Path.GetFullPath(migration));

        Assert.Contains("ALTER TABLE \"Customer\"", source, StringComparison.Ordinal);
        Assert.Contains("USING \"CreatedDate\" AT TIME ZONE 'UTC'", source, StringComparison.Ordinal);
        Assert.Contains("USING \"ModifiedDate\" AT TIME ZONE 'UTC'", source, StringComparison.Ordinal);
    }
}
