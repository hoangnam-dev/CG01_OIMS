using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Users;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class OrderStatusHistoryConfiguration : IEntityTypeConfiguration<OrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistory> builder)
    {
        builder.ToTable("order_status_history", table =>
        {
            table.HasCheckConstraint(
                "ck_order_status_history_status_transition",
                "from_status IN ('PendingPayment', 'Confirmed', 'Processing', 'Completed', 'Cancelled', 'Expired') AND to_status IN ('PendingPayment', 'Confirmed', 'Processing', 'Completed', 'Cancelled', 'Expired') AND from_status <> to_status");
            table.HasCheckConstraint(
                "ck_order_status_history_actor_type",
                "actor_type IN ('Customer', 'Admin', 'System')");
            table.HasCheckConstraint(
                "ck_order_status_history_actor_user",
                "(actor_type = 'System' AND actor_user_id IS NULL) OR (actor_type IN ('Customer', 'Admin') AND actor_user_id IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_order_status_history_reason_code",
                "reason_code IN ('CustomerRequested', 'CustomerSupport', 'FraudSuspected', 'DuplicateOrder', 'InventoryIssue', 'PolicyViolation', 'Other')");
            table.HasCheckConstraint(
                "ck_order_status_history_reason_format",
                "reason IS NULL OR (reason = btrim(reason) AND length(reason) <= 500)");
            table.HasCheckConstraint(
                "ck_order_status_history_admin_cancel_reason",
                "NOT (actor_type = 'Admin' AND to_status = 'Cancelled') OR (reason IS NOT NULL AND length(btrim(reason)) > 0)");
        });

        builder.HasKey(history => history.Id)
            .HasName("pk_order_status_history");

        builder.Property(history => history.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(history => history.OrderId)
            .HasColumnName("order_id")
            .IsRequired();
        builder.Property(history => history.FromStatus)
            .HasColumnName("from_status")
            .HasMaxLength(32)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(history => history.ToStatus)
            .HasColumnName("to_status")
            .HasMaxLength(32)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(history => history.ActorType)
            .HasColumnName("actor_type")
            .HasMaxLength(16)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(history => history.ActorUserId)
            .HasColumnName("actor_user_id");
        builder.Property(history => history.ReasonCode)
            .HasColumnName("reason_code")
            .HasMaxLength(64)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(history => history.Reason)
            .HasColumnName("reason")
            .HasMaxLength(500);
        builder.Property(history => history.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(history => history.OrderId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_order_status_history_orders_order_id");
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(history => history.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_order_status_history_users_actor_user_id");

        builder.HasIndex(history => new { history.OrderId, history.OccurredAt, history.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_order_status_history_order_occurred_id");
        builder.HasIndex(history => new { history.ActorUserId, history.OccurredAt, history.Id })
            .IsDescending(false, true, true)
            .HasFilter("actor_user_id IS NOT NULL")
            .HasDatabaseName("ix_order_status_history_actor_occurred_id");
    }
}
