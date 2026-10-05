using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Shipments;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class ShipmentConfiguration : IEntityTypeConfiguration<Shipment>
{
    public void Configure(EntityTypeBuilder<Shipment> builder)
    {
        builder.ToTable("shipments", table =>
        {

            table.HasCheckConstraint(
                "ck_shipments_status",
                "status IN ('Pending', 'Picking', 'Packed', 'Shipped', " +
                "'OutForDelivery', 'Delivered', 'DeliveryFailed', 'Returning', 'Returned')");

            table.HasCheckConstraint(
                "ck_shipments_failure_reason_lifecycle",
                "(status = 'DeliveryFailed' AND failure_reason IS NOT NULL AND " +
                "failure_reason = btrim(failure_reason) AND " +
                "length(failure_reason) BETWEEN 1 AND 500) OR " +
                "(status <> 'DeliveryFailed' AND failure_reason IS NULL)");

            table.HasCheckConstraint(
                "ck_shipments_updated_after_created",
                "updated_at >= created_at");

            table.HasCheckConstraint(
                "ck_shipments_lifecycle_timestamps_after_created",
                "(shipped_at IS NULL OR shipped_at >= created_at) AND " +
                "(delivered_at IS NULL OR delivered_at >= created_at) AND " +
                "(returned_at IS NULL OR returned_at >= created_at) AND " +
                "(restocked_at IS NULL OR restocked_at >= created_at)");
        });

        builder.HasKey(shipment => shipment.Id)
            .HasName("pk_shipments");

        builder.Property(shipment => shipment.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(shipment => shipment.OrderId)
            .HasColumnName("order_id")
            .IsRequired();

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(shipment => shipment.OrderId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_shipments_orders_order_id");

        builder.HasIndex(shipment => shipment.OrderId)
            .IsUnique()
            .HasDatabaseName("uq_shipments_order_id");

        builder.HasIndex(shipment => new
        {
            shipment.Status,
            shipment.UpdatedAt,
            shipment.Id
        })
            .HasDatabaseName("ix_shipments_status_updated_id");

        builder.Property(shipment => shipment.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(shipment => shipment.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(500);

        builder.Property(shipment => shipment.ShippedAt)
            .HasColumnName("shipped_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(shipment => shipment.DeliveredAt)
            .HasColumnName("delivered_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(shipment => shipment.ReturnedAt)
            .HasColumnName("returned_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(shipment => shipment.RestockedAt)
            .HasColumnName("restocked_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(shipment => shipment.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(shipment => shipment.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();
    }
}