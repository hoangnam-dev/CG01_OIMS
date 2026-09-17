using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Products;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", table =>
        {
            table.HasCheckConstraint("ck_products_name_not_empty", "length(btrim(name)) > 0");
            table.HasCheckConstraint("ck_products_status", "status IN ('Active', 'Inactive')");
        });

        builder.HasKey(product => product.Id).HasName("pk_products");
        builder.Property(product => product.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(product => product.Name).HasColumnName("name").IsRequired();
        builder.Property(product => product.Description).HasColumnName("description").IsRequired();
        builder.Property(product => product.Status).HasColumnName("status").HasMaxLength(20).HasConversion<string>().IsRequired();
        builder.Property(product => product.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("CURRENT_TIMESTAMP").IsRequired();
        builder.Property(product => product.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("CURRENT_TIMESTAMP").IsRequired();

        builder.HasIndex(product => new { product.Status, product.Name, product.Id })
            .HasDatabaseName("ix_products_status_name_id");
        builder.HasIndex(product => new { product.Status, product.CreatedAt, product.Id })
            .HasDatabaseName("ix_products_status_created_at_id");
        builder.HasIndex(product => new { product.Status, product.UpdatedAt, product.Id })
            .HasDatabaseName("ix_products_status_updated_at_id");
    }
}
