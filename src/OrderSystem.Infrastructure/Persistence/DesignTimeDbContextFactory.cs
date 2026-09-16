using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrderSystem.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OrderSystemDbContext>
{
    public OrderSystemDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("OIMS_DATABASE_CONNECTION")
            ?? throw new InvalidOperationException(
                "Set OIMS_DATABASE_CONNECTION before running EF Core design-time commands.");
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(OrderSystemDbContext).Assembly.FullName))
            .Options;

        return new(options);
    }
}
