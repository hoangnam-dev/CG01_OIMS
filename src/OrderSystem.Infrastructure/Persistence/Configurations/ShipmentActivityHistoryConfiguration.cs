using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Shipments;
using OrderSystem.Domain.Users;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class ShipmentActivityHistoryConfiguration
    : IEntityTypeConfiguration<ShipmentActivityHistory>
{
    public void Configure(EntityTypeBuilder<ShipmentActivityHistory> builder)
    {
        builder.ToTable("shipment_activity_histories", table =>
        {
            table.HasCheckConstraint(
                "ck_shipment_activity_histories_activity_type",
                "activity_type IN ('Created', 'PickingStarted', 'Packed', 'Shipped', " +
                "'OutForDeliveryStarted', 'Delivered', 'DeliveryFailed', 'ReturnStarted', 'Returned', 'Restocked')");

            table.HasCheckConstraint(
                "ck_shipment_activity_histories_actor_type",
                "actor_type IN ('Admin', 'System', 'Carrier')");

            table.HasCheckConstraint(
                "ck_shipment_activity_histories_actor_user",
                "(actor_type IN ('System', 'Carrier') AND actor_user_id IS NULL) OR " +
                "(actor_type = 'Admin' AND actor_user_id IS NOT NULL)");

            table.HasCheckConstraint(
                "ck_shipment_activity_histories_from_status",
                "from_status IS NULL OR from_status IN ('Pending', 'Picking', 'Packed', " +
                "'Shipped', 'OutForDelivery', 'Delivered', 'DeliveryFailed', 'Returning', 'Returned')");

            table.HasCheckConstraint(
                "ck_shipment_activity_histories_to_status",
                "to_status IS NULL OR to_status IN ('Pending', 'Picking', 'Packed', " +
                "'Shipped', 'OutForDelivery', 'Delivered', 'DeliveryFailed', 'Returning', 'Returned')");

            table.HasCheckConstraint(
                "ck_shipment_activity_histories_reason_format",
                "reason IS NULL OR (reason = btrim(reason) AND length(reason) <= 500)");
        });

        builder.HasKey(activity => activity.Id)
            .HasName("pk_shipment_activity_histories");

        builder.Property(activity => activity.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(activity => activity.ShipmentId)
            .HasColumnName("shipment_id")
            .IsRequired();

        builder.Property(activity => activity.ActivityType)
            .HasColumnName("activity_type")
            .HasMaxLength(32)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(activity => activity.FromStatus)
            .HasColumnName("from_status")
            .HasMaxLength(32)
            .HasConversion<string>();

        builder.Property(activity => activity.ToStatus)
            .HasColumnName("to_status")
            .HasMaxLength(32)
            .HasConversion<string>();

        builder.Property(activity => activity.ActorType)
            .HasColumnName("actor_type")
            .HasMaxLength(16)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(activity => activity.ActorUserId)
            .HasColumnName("actor_user_id");

        builder.Property(activity => activity.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(activity => activity.Reason)
            .HasColumnName("reason")
            .HasMaxLength(500);

        builder.HasOne<Shipment>()
            .WithMany()
            .HasForeignKey(activity => activity.ShipmentId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_shipment_activity_histories_shipments_shipment_id");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(activity => activity.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_shipment_activity_histories_users_actor_user_id");

        builder.HasIndex(activity => new
        {
            activity.ShipmentId,
            activity.OccurredAt,
            activity.Id
        })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_shipment_activity_histories_shipment_occurred_id");
    }
}