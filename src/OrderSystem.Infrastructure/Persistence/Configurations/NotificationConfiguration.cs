using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Notifications;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Users;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications", table =>
            table.HasCheckConstraint(
                "ck_notifications_type_not_blank",
                "length(btrim(type)) > 0"));

        builder.HasKey(notification => notification.Id)
            .HasName("pk_notifications");

        builder.Property(notification => notification.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(notification => notification.UserId)
            .HasColumnName("user_id")
            .IsRequired();
        builder.Property(notification => notification.OrderId)
            .HasColumnName("order_id")
            .IsRequired();
        builder.Property(notification => notification.MessageId)
            .HasColumnName("message_id")
            .IsRequired();
        builder.Property(notification => notification.Type)
            .HasColumnName("type")
            .HasMaxLength(64)
            .HasConversion<string>()
            .HasDefaultValue(NotificationType.OrderCreated)
            .IsRequired();
        builder.Property(notification => notification.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(notification => notification.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_notifications_users_user_id");
        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(notification => notification.OrderId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_notifications_orders_order_id");

        builder.HasIndex(notification => new { notification.MessageId, notification.Type })
            .IsUnique()
            .HasDatabaseName("uq_notifications_message_type");
        builder.HasIndex(notification => new
        {
            notification.UserId,
            notification.CreatedAt,
            notification.Id
        })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_notifications_user_created_id");
        builder.HasIndex(notification => notification.OrderId)
            .HasDatabaseName("ix_notifications_order_id");
    }
}