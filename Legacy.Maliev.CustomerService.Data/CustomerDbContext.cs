using Legacy.Maliev.CustomerService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.CustomerService.Data;

/// <summary>
/// Provides Entity Framework Core access to the PostgreSQL schema that preserves legacy customer data.
/// </summary>
/// <param name="options">The options used to configure the customer database context.</param>
public sealed class CustomerDbContext(DbContextOptions<CustomerDbContext> options) : DbContext(options)
{
    /// <summary>
    /// The customer profiles stored in the legacy-compatible customer table.
    /// </summary>
    public DbSet<Customer> Customers => Set<Customer>();

    /// <summary>
    /// The companies associated with customer profiles for billing and registration details.
    /// </summary>
    public DbSet<Company> Companies => Set<Company>();

    /// <summary>
    /// The billing and shipping addresses associated with customer profiles.
    /// </summary>
    public DbSet<Address> Addresses => Set<Address>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var address = modelBuilder.Entity<Address>();
        address.ToTable("Address");
        address.HasKey(value => value.Id);
        address.Property(value => value.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        address.Property(value => value.AddressLine1).HasMaxLength(256).IsRequired();
        address.Property(value => value.AddressLine2).HasMaxLength(256);
        address.Property(value => value.Building).HasMaxLength(256);
        address.Property(value => value.City).HasMaxLength(256);
        address.Property(value => value.CountryId).HasColumnName("CountryID");
        address.Property(value => value.PostalCode).HasMaxLength(256);
        address.Property(value => value.State).HasMaxLength(256);
        ConfigureDates(address);

        var company = modelBuilder.Entity<Company>();
        company.ToTable("Company");
        company.HasKey(value => value.Id);
        company.Property(value => value.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        company.Property(value => value.Name).HasMaxLength(256).IsRequired();
        company.Property(value => value.Registrar).HasMaxLength(256);
        company.Property(value => value.TaxNumber).HasMaxLength(256);
        ConfigureDates(company);

        var customer = modelBuilder.Entity<Customer>();
        customer.ToTable("Customer");
        customer.HasKey(value => value.Id);
        customer.Property(value => value.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        customer.Property(value => value.BillingAddressId).HasColumnName("BillingAddressID");
        customer.Property(value => value.CompanyId).HasColumnName("CompanyID");
        customer.Property(value => value.ShippingAddressId).HasColumnName("ShippingAddressID");
        customer.Property(value => value.DateOfBirth).HasColumnType("date");
        customer.Property(value => value.Email).HasMaxLength(256).IsRequired();
        customer.Property(value => value.Fax).HasMaxLength(256);
        customer.Property(value => value.FirstName).HasMaxLength(256).IsRequired();
        customer.Property(value => value.FullName)
            .HasMaxLength(513)
            .HasComputedColumnSql("btrim(\"FirstName\" || ' ' || \"LastName\")", stored: true);
        customer.Property(value => value.LastName).HasMaxLength(256).IsRequired();
        customer.Property(value => value.InternalRemark).HasMaxLength(4000);
        customer.Property(value => value.Mobile).HasMaxLength(256);
        customer.Property(value => value.Telephone).HasMaxLength(256);
        ConfigureDates(customer);
        customer.HasOne(value => value.BillingAddress)
            .WithMany(value => value.BillingCustomers)
            .HasForeignKey(value => value.BillingAddressId)
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_Customer_Address");
        customer.HasOne(value => value.Company)
            .WithMany(value => value.Customers)
            .HasForeignKey(value => value.CompanyId)
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_Customer_Company");
        customer.HasOne(value => value.ShippingAddress)
            .WithMany(value => value.ShippingCustomers)
            .HasForeignKey(value => value.ShippingAddressId)
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_Customer_Address1");
        customer.HasIndex(value => value.Email).HasDatabaseName("IX_Customer_Email");
    }

    private static void ConfigureDates<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity)
        where TEntity : class
    {
        // Legacy imported datetime values are stored as UTC wall-clock values in
        // PostgreSQL timestamp-without-time-zone columns. Keep the conversion explicit
        // for every customer-owned table so Npgsql accepts the repository's UTC-boundary
        // DateTime values and new rows use the same representation.
        entity.Property<DateTime?>(nameof(Customer.CreatedDate))
            .HasColumnType("timestamp without time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP AT TIME ZONE 'UTC'");
        entity.Property<DateTime?>(nameof(Customer.ModifiedDate))
            .HasColumnType("timestamp without time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP AT TIME ZONE 'UTC'");
    }
}
