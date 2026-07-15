using Legacy.Maliev.CustomerService.Data;
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
        Assert.Null(customer.FindProperty("xmin"));

        Assert.Equal("FK_Customer_Address", customer.GetForeignKeys().Single(key => key.Properties.Single().Name == nameof(Customer.BillingAddressId)).GetConstraintName());
        Assert.Equal("FK_Customer_Address1", customer.GetForeignKeys().Single(key => key.Properties.Single().Name == nameof(Customer.ShippingAddressId)).GetConstraintName());
        Assert.Equal("FK_Customer_Company", customer.GetForeignKeys().Single(key => key.Properties.Single().Name == nameof(Customer.CompanyId)).GetConstraintName());
    }
}
