using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Products;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

public sealed class InventoryTransactionConfiguration : IEntityTypeConfiguration<InventoryTransaction>
{
    public void Configure(EntityTypeBuilder<InventoryTransaction> builder)
    {
        builder.ToTable("inventory_transactions", table =>
        {
            table.HasCheckConstraint(
                "ck_inventory_transactions_type",
                "type IN ('Receipt', 'Reserve', 'Release', 'Issue', 'Adjustment')");
            table.HasCheckConstraint(
                "ck_inventory_transactions_non_zero_delta",
                "on_hand_delta <> 0 OR reserved_delta <> 0");
            table.HasCheckConstraint(
                "ck_inventory_transactions_reference_pair",
                "(reference_type IS NULL AND reference_id IS NULL) OR (reference_type IS NOT NULL AND reference_id IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_inventory_transactions_reference_type",
                "reference_type IS NULL OR reference_type IN ('Order', 'GoodsReceipt', 'Shipment', 'InventoryAdjustment')");
            table.HasCheckConstraint(
                "ck_inventory_transactions_delta_shape",
                "(type = 'Receipt' AND on_hand_delta > 0 AND reserved_delta = 0) OR " +
                "(type = 'Reserve' AND on_hand_delta = 0 AND reserved_delta > 0) OR " +
                "(type = 'Release' AND on_hand_delta = 0 AND reserved_delta < 0) OR " +
                "(type = 'Issue' AND on_hand_delta < 0 AND reserved_delta = on_hand_delta) OR " +
                "(type = 'Adjustment' AND on_hand_delta <> 0 AND reserved_delta = 0)");
            table.HasCheckConstraint(
                "ck_inventory_transactions_adjustment_reason",
                "type <> 'Adjustment' OR (reason IS NOT NULL AND length(btrim(reason)) > 0)");
            table.HasCheckConstraint(
                "ck_inventory_transactions_order_reference",
                "type NOT IN ('Reserve', 'Release') OR (reference_type = 'Order' AND reference_id IS NOT NULL)");
        });

        builder.HasKey(transaction => transaction.Id)
            .HasName("pk_inventory_transactions");

        builder.Property(transaction => transaction.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(transaction => transaction.ProductVariantId)
            .HasColumnName("product_variant_id")
            .IsRequired();
        builder.Property(transaction => transaction.Type)
            .HasColumnName("type")
            .HasMaxLength(20)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(transaction => transaction.OnHandQuantityDelta)
            .HasColumnName("on_hand_delta")
            .HasDefaultValue(0)
            .IsRequired();
        builder.Property(transaction => transaction.ReservedQuantityDelta)
            .HasColumnName("reserved_delta")
            .HasDefaultValue(0)
            .IsRequired();
        builder.Property(transaction => transaction.ReferenceType)
            .HasColumnName("reference_type")
            .HasMaxLength(32)
            .HasConversion<string>();
        builder.Property(transaction => transaction.ReferenceId)
            .HasColumnName("reference_id");
        builder.Property(transaction => transaction.Reason)
            .HasColumnName("reason")
            .HasMaxLength(256);
        builder.Property(transaction => transaction.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(transaction => transaction.ProductVariantId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_inventory_transactions_product_variants_product_variant_id");

        builder.HasIndex(transaction => new
        {
            transaction.ProductVariantId,
            transaction.CreatedAt,
            transaction.Id
        })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_inventory_transactions_variant_created_id");

        builder.HasIndex(transaction => new
        {
            transaction.ReferenceType,
            transaction.ReferenceId,
            transaction.CreatedAt,
            transaction.Id
        })
            .HasFilter("reference_id IS NOT NULL")
            .HasDatabaseName("ix_inventory_transactions_reference_created");
    }
}
