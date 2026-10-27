using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Notifications;
using OrderSystem.Domain.Users;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class FailedMessagePayloadAccessConfiguration
    : IEntityTypeConfiguration<FailedMessagePayloadAccess>
{
    public void Configure(EntityTypeBuilder<FailedMessagePayloadAccess> builder)
    {
        builder.ToTable("failed_message_payload_accesses", table =>
            table.HasCheckConstraint(
                "ck_failed_message_payload_accesses_reason",
                "length(btrim(reason)) BETWEEN 1 AND 500"
            )
        );

        builder.HasKey(access => access.Id)
            .HasName("pk_failed_message_payload_accesses");

        builder.Property(access => access.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(access => access.FailedMessageId)
            .HasColumnName("failed_message_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(access => access.AccessedByUserId)
            .HasColumnName("accessed_by_user_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(access => access.Reason)
            .HasColumnName("reason")
            .HasColumnType("character varying(500)")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(access => access.AccessedAt)
            .HasColumnName("accessed_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasOne<FailedMessage>()
            .WithMany()
            .HasForeignKey(access => access.FailedMessageId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_failed_message_payload_accesses_failed_message");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(access => access.AccessedByUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_failed_message_payload_accesses_user");

        builder.HasIndex(access => new
        {
            access.FailedMessageId,
            access.AccessedAt,
            access.Id
        })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_failed_message_payload_accesses_failure_time");
    }
}