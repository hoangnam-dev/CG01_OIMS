using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Notifications;
using OrderSystem.Domain.Users;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class FailedMessageConfiguration : IEntityTypeConfiguration<FailedMessage>
{
    public void Configure(EntityTypeBuilder<FailedMessage> builder)
    {
        builder.ToTable("failed_messages", table =>
        {
            table.HasCheckConstraint(
                "ck_failed_messages_attempt_count",
                "attempt_count BETWEEN 1 AND 3");

            table.HasCheckConstraint(
                "ck_failed_messages_failure_kind",
                "failure_kind IN ('Malformed', 'UnsupportedVersion', " +
                "'Permanent', 'RetryExhausted', 'Expired')");

            table.HasCheckConstraint(
                "ck_failed_messages_status",
                "status IN ('Pending', 'RedriveRequested', " +
                "'Redriven', 'Closed', 'Expired')");

            table.HasCheckConstraint(
                "ck_failed_messages_consumer_name_not_blank",
                "length(btrim(consumer_name)) BETWEEN 1 AND 100");

            table.HasCheckConstraint(
                "ck_failed_messages_raw_message_id_not_blank",
                "raw_message_id IS NULL OR " +
                "length(btrim(raw_message_id)) BETWEEN 1 AND 255");

            table.HasCheckConstraint(
                "ck_failed_messages_event_type_not_blank",
                "event_type IS NULL OR " +
                "length(btrim(event_type)) BETWEEN 1 AND 100");

            table.HasCheckConstraint(
                "ck_failed_messages_original_exchange_not_blank",
                "length(btrim(original_exchange)) BETWEEN 1 AND 255");

            table.HasCheckConstraint(
                "ck_failed_messages_original_routing_key_not_blank",
                "length(btrim(original_routing_key)) BETWEEN 1 AND 255");

            table.HasCheckConstraint(
                "ck_failed_messages_failure_code_not_blank",
                "length(btrim(failure_code)) BETWEEN 1 AND 100");

            table.HasCheckConstraint(
                "ck_failed_messages_body_sha256_format",
                "body_sha256 ~ '^[0-9a-f]{64}$'");

            table.HasCheckConstraint(
                "ck_failed_messages_redrive_reason_format",
                "redrive_reason IS NULL OR " +
                "(redrive_reason = btrim(redrive_reason) " +
                "AND length(redrive_reason) BETWEEN 1 AND 500)");

            table.HasCheckConstraint(
                "ck_failed_messages_close_reason_format",
                "close_reason IS NULL OR " +
                "(close_reason = btrim(close_reason) " +
                "AND length(close_reason) BETWEEN 1 AND 500)");

            table.HasCheckConstraint(
                "ck_failed_messages_lifecycle",
                "(status = 'Pending' " +
                "AND redrive_requested_at IS NULL " +
                "AND redrive_requested_by_user_id IS NULL " +
                "AND redrive_reason IS NULL " +
                "AND redriven_at IS NULL " +
                "AND closed_at IS NULL " +
                "AND closed_by_user_id IS NULL " +
                "AND close_reason IS NULL " +
                "AND terminal_at IS NULL) " +

                "OR (status = 'RedriveRequested' " +
                "AND redrive_requested_at IS NOT NULL " +
                "AND redrive_requested_by_user_id IS NOT NULL " +
                "AND redrive_reason IS NOT NULL " +
                "AND redriven_at IS NULL " +
                "AND closed_at IS NULL " +
                "AND closed_by_user_id IS NULL " +
                "AND close_reason IS NULL " +
                "AND terminal_at IS NULL) " +

                "OR (status = 'Redriven' " +
                "AND redrive_requested_at IS NOT NULL " +
                "AND redrive_requested_by_user_id IS NOT NULL " +
                "AND redrive_reason IS NOT NULL " +
                "AND redriven_at IS NOT NULL " +
                "AND closed_at IS NULL " +
                "AND closed_by_user_id IS NULL " +
                "AND close_reason IS NULL " +
                "AND terminal_at IS NOT NULL) " +

                "OR (status = 'Closed' " +
                "AND redrive_requested_at IS NULL " +
                "AND redrive_requested_by_user_id IS NULL " +
                "AND redrive_reason IS NULL " +
                "AND redriven_at IS NULL " +
                "AND closed_at IS NOT NULL " +
                "AND closed_by_user_id IS NOT NULL " +
                "AND close_reason IS NOT NULL " +
                "AND terminal_at IS NOT NULL) " +

                "OR (status = 'Expired' " +
                "AND ((redrive_requested_at IS NULL " +
                "AND redrive_requested_by_user_id IS NULL " +
                "AND redrive_reason IS NULL) " +
                "OR (redrive_requested_at IS NOT NULL " +
                "AND redrive_requested_by_user_id IS NOT NULL " +
                "AND redrive_reason IS NOT NULL)) " +
                "AND redriven_at IS NULL " +
                "AND closed_at IS NULL " +
                "AND closed_by_user_id IS NULL " +
                "AND close_reason IS NULL " +
                "AND terminal_at IS NOT NULL)");
        });

        builder.HasKey(message => message.Id)
            .HasName("pk_failed_messages");

        builder.Property(message => message.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(message => message.ConsumerName)
            .HasColumnName("consumer_name")
            .HasColumnType("character varying(100)")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(message => message.MessageId)
            .HasColumnName("message_id")
            .HasColumnType("uuid");

        builder.Property(message => message.RawMessageId)
            .HasColumnName("raw_message_id")
            .HasColumnType("character varying(255)")
            .HasMaxLength(255);

        builder.Property(message => message.CorrelationId)
            .HasColumnName("correlation_id")
            .HasColumnType("uuid");

        builder.Property(message => message.EventType)
            .HasColumnName("event_type")
            .HasColumnType("character varying(100)")
            .HasMaxLength(100);

        builder.Property(message => message.EventVersion)
            .HasColumnName("event_version")
            .HasColumnType("integer");

        builder.Property(message => message.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(message => message.Body)
            .HasColumnName("body")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(message => message.BodySha256)
            .HasColumnName("body_sha256")
            .HasColumnType("character(64)")
            .HasMaxLength(64)
            .IsFixedLength()
            .IsRequired();

        builder.Property(message => message.SafeHeaders)
            .HasColumnName("safe_headers")
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();

        builder.Property(message => message.OriginalExchange)
            .HasColumnName("original_exchange")
            .HasColumnType("character varying(255)")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(message => message.OriginalRoutingKey)
            .HasColumnName("original_routing_key")
            .HasColumnType("character varying(255)")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(message => message.FailureKind)
            .HasColumnName("failure_kind")
            .HasColumnType("character varying(32)")
            .HasMaxLength(32)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(message => message.FailureCode)
            .HasColumnName("failure_code")
            .HasColumnType("character varying(100)")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(message => message.AttemptCount)
            .HasColumnName("attempt_count")
            .HasColumnType("integer")
            .IsRequired();

        builder.Property(message => message.Status)
            .HasColumnName("status")
            .HasColumnType("character varying(32)")
            .HasMaxLength(32)
            .HasConversion<string>()
            .HasDefaultValue(FailedMessageStatus.Pending)
            .IsRequired();

        builder.Property(message => message.CapturedAt)
            .HasColumnName("captured_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(message => message.RedriveRequestedAt)
    .HasColumnName("redrive_requested_at")
    .HasColumnType("timestamp with time zone");

        builder.Property(message => message.RedriveRequestedByUserId)
            .HasColumnName("redrive_requested_by_user_id")
            .HasColumnType("uuid");

        builder.Property(message => message.RedriveReason)
            .HasColumnName("redrive_reason")
            .HasColumnType("character varying(500)")
            .HasMaxLength(500);

        builder.Property(message => message.RedrivenAt)
            .HasColumnName("redriven_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(message => message.ClosedAt)
            .HasColumnName("closed_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(message => message.ClosedByUserId)
            .HasColumnName("closed_by_user_id")
            .HasColumnType("uuid");

        builder.Property(message => message.CloseReason)
            .HasColumnName("close_reason")
            .HasColumnType("character varying(500)")
            .HasMaxLength(500);

        builder.Property(message => message.TerminalAt)
            .HasColumnName("terminal_at")
            .HasColumnType("timestamp with time zone");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(message => message.RedriveRequestedByUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_failed_messages_users_redrive_requested_by_user_id");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(message => message.ClosedByUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_failed_messages_users_closed_by_user_id");
        builder.HasIndex(message => new
        {
            message.Status,
            message.CapturedAt,
            message.Id
        })
            .HasDatabaseName("ix_failed_messages_status_captured_id");

        builder.HasIndex(message => new
        {
            message.TerminalAt,
            message.Id
        })
            .HasFilter("terminal_at IS NOT NULL")
            .HasDatabaseName("ix_failed_messages_terminal_id");

        builder.HasIndex(message => new
        {
            message.MessageId,
            message.ConsumerName
        })
            .HasDatabaseName("ix_failed_messages_message_consumer");

        builder.HasIndex(message => message.BodySha256)
            .HasDatabaseName("ix_failed_messages_body_sha256");
    }
}