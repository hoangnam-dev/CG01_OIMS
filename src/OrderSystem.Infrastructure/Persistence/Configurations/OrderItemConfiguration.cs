using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Products;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("order_items", table =>
        {
            table.HasCheckConstraint(
                "ck_order_items_quantity_positive",
                "quantity > 0");
            table.HasCheckConstraint(
                "ck_order_items_unit_price_non_negative",
                "unit_price >= 0");
            table.HasCheckConstraint(
                "ck_order_items_line_total_non_negative",
                "line_total >= 0");
            table.HasCheckConstraint(
                "ck_order_items_line_total_matches_unit_price",
                "line_total = quantity * unit_price");
        });

        builder.HasKey(orderItem => orderItem.Id)
            .HasName("pk_order_items");

        builder.Property(orderItem => orderItem.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(orderItem => orderItem.OrderId)
            .HasColumnName("order_id")
            .IsRequired();
        builder.Property(orderItem => orderItem.ProductVariantId)
            .HasColumnName("product_variant_id")
            .IsRequired();
        builder.Property(orderItem => orderItem.Quantity)
            .HasColumnName("quantity")
            .IsRequired();
        builder.Property(orderItem => orderItem.UnitPrice)
            .HasColumnName("unit_price")
            .HasPrecision(18, 2)
            .IsRequired();
        builder.Property(orderItem => orderItem.LineTotal)
            .HasColumnName("line_total")
            .HasPrecision(18, 2)
            .IsRequired();

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(orderItem => orderItem.OrderId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_order_items_orders_order_id");
        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(orderItem => orderItem.ProductVariantId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_order_items_product_variants_product_variant_id");

        builder.HasIndex(orderItem => new { orderItem.OrderId, orderItem.ProductVariantId })
            .IsUnique()
            .HasDatabaseName("uq_order_items_order_variant");
        builder.HasIndex(orderItem => orderItem.ProductVariantId)
            .HasDatabaseName("ix_order_items_product_variant_id");
    }
}
