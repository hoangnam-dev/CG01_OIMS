using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Users;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders", table =>
        {
            table.HasCheckConstraint(
                "ck_orders_status",
                "status IN ('PendingPayment', 'Confirmed', 'Processing', 'Completed', 'Cancelled', 'Expired')");
            table.HasCheckConstraint(
                "ck_orders_total_amount_non_negative",
                "total_amount >= 0");
            table.HasCheckConstraint(
                "ck_orders_reservation_expires_after_created",
                "reservation_expires_at > created_at");
        });

        builder.HasKey(order => order.Id)
            .HasName("pk_orders");

        builder.Property(order => order.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(order => order.UserId)
            .HasColumnName("user_id")
            .IsRequired();
        builder.Property(order => order.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(order => order.TotalAmount)
            .HasColumnName("total_amount")
            .HasPrecision(18, 2)
            .IsRequired();
        builder.Property(order => order.ReservationExpiresAt)
            .HasColumnName("reservation_expires_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
        builder.Property(order => order.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();
        builder.Property(order => order.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(order => order.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_orders_users_user_id");

        builder.HasIndex(order => new { order.UserId, order.CreatedAt, order.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_orders_user_created_id");
        builder.HasIndex(order => new { order.Status, order.CreatedAt, order.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_orders_status_created_id");
        builder.HasIndex(order => new { order.ReservationExpiresAt, order.Id })
            .HasFilter("status = 'PendingPayment'")
            .HasDatabaseName("ix_orders_pending_expiration");
    }
}
