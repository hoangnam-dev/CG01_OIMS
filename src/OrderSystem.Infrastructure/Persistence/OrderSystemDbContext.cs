using Microsoft.EntityFrameworkCore;
using OrderSystem.Domain.Products;
using OrderSystem.Domain.Users;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Idempotency;

namespace OrderSystem.Infrastructure.Persistence;

public sealed class OrderSystemDbContext(DbContextOptions<OrderSystemDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<IdempotencyRequest> IdempotencyRequests => Set<IdempotencyRequest>();

    public DbSet<Inventory> Inventories => Set<Inventory>();

    public DbSet<InventoryTransaction> InventoryTransactions => Set<InventoryTransaction>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    public DbSet<OrderStatusHistory> OrderStatusHistories => Set<OrderStatusHistory>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderSystemDbContext).Assembly);
    }
}
