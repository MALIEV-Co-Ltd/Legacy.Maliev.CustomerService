using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Legacy.Maliev.CustomerService.Data;

/// <summary>Creates the context for explicit design-time migration commands.</summary>
public sealed class CustomerDbContextFactory : IDesignTimeDbContextFactory<CustomerDbContext>
{
    /// <inheritdoc />
    public CustomerDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__CustomerDbContext");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings__CustomerDbContext is required for design-time migration commands.");
        }

        var options = new DbContextOptionsBuilder<CustomerDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new CustomerDbContext(options);
    }
}
