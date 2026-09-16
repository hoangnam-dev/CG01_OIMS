using Microsoft.EntityFrameworkCore;
using OrderSystem.Domain.Users;

namespace OrderSystem.Infrastructure.Persistence;

public sealed class OrderSystemDbContext(DbContextOptions<OrderSystemDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderSystemDbContext).Assembly);
    }
}
