using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Users;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", table =>
        {
            table.HasCheckConstraint("ck_users_normalized_email_canonical", "normalized_email = lower(btrim(normalized_email))");
            table.HasCheckConstraint("ck_users_role", "role IN ('Customer', 'Admin')");
            table.HasCheckConstraint("ck_users_email_not_empty", "length(btrim(email)) > 0");
            table.HasCheckConstraint("ck_users_password_hash_not_empty", "length(btrim(password_hash)) > 0");
        });

        builder.HasKey(user => user.Id).HasName("pk_users");
        builder.Property(user => user.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(user => user.Email).HasColumnName("email").HasMaxLength(320).IsRequired();
        builder.Property(user => user.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(320).IsRequired();
        builder.Property(user => user.PasswordHash).HasColumnName("password_hash").IsRequired();
        builder.Property(user => user.Role).HasColumnName("role").HasMaxLength(20).HasConversion<string>().HasDefaultValue(UserRole.Customer).IsRequired();
        builder.Property(user => user.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("CURRENT_TIMESTAMP").IsRequired();
        builder.Property(user => user.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("CURRENT_TIMESTAMP").IsRequired();
        builder.HasIndex(user => user.NormalizedEmail).IsUnique().HasDatabaseName("uq_users_normalized_email");
    }
}
