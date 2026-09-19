using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Products;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class InventoryConfiguration : IEntityTypeConfiguration<Inventory>
{
    public void Configure(EntityTypeBuilder<Inventory> builder)
    {
        builder.ToTable("inventories", table =>
        {
            table.HasCheckConstraint(
                "ck_inventories_on_hand_quantity_non_negative",
                "on_hand_quantity >= 0");
            table.HasCheckConstraint(
                "ck_inventories_reserved_quantity_non_negative",
                "reserved_quantity >= 0");
            table.HasCheckConstraint(
                "ck_inventories_reserved_not_greater_than_on_hand",
                "reserved_quantity <= on_hand_quantity");
        });

        builder.HasKey(inventory => inventory.Id)
            .HasName("pk_inventories");

        builder.Property(inventory => inventory.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(inventory => inventory.ProductVariantId)
            .HasColumnName("product_variant_id")
            .IsRequired();
        builder.Property(inventory => inventory.OnHandQuantity)
            .HasColumnName("on_hand_quantity")
            .HasDefaultValue(0)
            .IsRequired();
        builder.Property(inventory => inventory.ReservedQuantity)
            .HasColumnName("reserved_quantity")
            .HasDefaultValue(0)
            .IsRequired();
        builder.Property(inventory => inventory.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Ignore(inventory => inventory.AvailableQuantity);

        builder.HasOne<ProductVariant>()
            .WithOne()
            .HasForeignKey<Inventory>(inventory => inventory.ProductVariantId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_inventories_product_variants_product_variant_id");

        builder.HasIndex(inventory => inventory.ProductVariantId)
            .IsUnique()
            .HasDatabaseName("uq_inventories_product_variant_id");
    }
}
