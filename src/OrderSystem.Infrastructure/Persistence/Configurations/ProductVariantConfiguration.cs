using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Products;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        builder.ToTable("product_variants", table =>
        {
            table.HasCheckConstraint("ck_product_variants_name_not_empty", "length(btrim(name)) > 0");
            table.HasCheckConstraint(
                "ck_product_variants_sku_canonical",
                $"length(sku) BETWEEN 1 AND {ProductVariant.MaximumSkuLength} AND sku = upper(btrim(sku))");
            table.HasCheckConstraint("ck_product_variants_current_price_non_negative", "current_price >= 0");
            table.HasCheckConstraint("ck_product_variants_status", "status IN ('Active', 'Inactive')");
        });

        builder.HasKey(variant => variant.Id).HasName("pk_product_variants");
        builder.Property(variant => variant.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(variant => variant.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(variant => variant.Sku).HasColumnName("sku").HasMaxLength(ProductVariant.MaximumSkuLength).IsRequired();
        builder.Property(variant => variant.Name).HasColumnName("name").IsRequired();
        builder.Property(variant => variant.CurrentPrice).HasColumnName("current_price").HasPrecision(18, 2).IsRequired();
        builder.Property(variant => variant.Status).HasColumnName("status").HasMaxLength(20).HasConversion<string>().IsRequired();
        builder.Property(variant => variant.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("CURRENT_TIMESTAMP").IsRequired();
        builder.Property(variant => variant.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("CURRENT_TIMESTAMP").IsRequired();

        builder.HasOne(variant => variant.Product)
            .WithMany()
            .HasForeignKey(variant => variant.ProductId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_product_variants_products_product_id");

        builder.HasIndex(variant => variant.Sku)
            .IsUnique()
            .HasDatabaseName("uq_product_variants_sku");
        builder.HasIndex(variant => new { variant.ProductId, variant.Status, variant.Id })
            .HasDatabaseName("ix_product_variants_product_id_status_id");
    }
}
