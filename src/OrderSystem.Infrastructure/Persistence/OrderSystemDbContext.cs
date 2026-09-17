using Microsoft.EntityFrameworkCore;
using OrderSystem.Domain.Products;
using OrderSystem.Domain.Users;

namespace OrderSystem.Infrastructure.Persistence;

public sealed class OrderSystemDbContext(DbContextOptions<OrderSystemDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderSystemDbContext).Assembly);
    }
}
