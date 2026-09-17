using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Users;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens", table =>
        {
            table.HasCheckConstraint(
                "ck_refresh_tokens_token_hash_canonical",
                "length(token_hash) = 64 AND token_hash = lower(token_hash) AND token_hash ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "ck_refresh_tokens_expiry_after_creation",
                "expires_at > created_at");
            table.HasCheckConstraint(
                "ck_refresh_tokens_revocation_after_creation",
                "revoked_at IS NULL OR revoked_at >= created_at");
            table.HasCheckConstraint(
                "ck_refresh_tokens_replacement_not_self",
                "replaced_by_token_id IS NULL OR replaced_by_token_id <> id");
            table.HasCheckConstraint(
                "ck_refresh_tokens_replacement_requires_revocation",
                "replaced_by_token_id IS NULL OR revoked_at IS NOT NULL");
        });

        builder.HasKey(token => token.Id).HasName("pk_refresh_tokens");
        builder.Property(token => token.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(token => token.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(token => token.TokenHash)
            .HasColumnName("token_hash")
            .HasMaxLength(RefreshToken.Sha256HexLength)
            .IsFixedLength()
            .IsRequired();
        builder.Property(token => token.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
        builder.Property(token => token.RevokedAt)
            .HasColumnName("revoked_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(token => token.ReplacedByTokenId).HasColumnName("replaced_by_token_id");
        builder.Property(token => token.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refresh_tokens_users_user_id");
        builder.HasOne<RefreshToken>()
            .WithMany()
            .HasForeignKey(token => token.ReplacedByTokenId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refresh_tokens_refresh_tokens_replaced_by_token_id");

        builder.HasIndex(token => token.TokenHash)
            .IsUnique()
            .HasDatabaseName("uq_refresh_tokens_token_hash");
        builder.HasIndex(token => token.ReplacedByTokenId)
            .HasDatabaseName("ix_refresh_tokens_replaced_by_token_id");
        builder.HasIndex(token => new { token.UserId, token.ExpiresAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_refresh_tokens_user_id_expires_at");
    }
}
