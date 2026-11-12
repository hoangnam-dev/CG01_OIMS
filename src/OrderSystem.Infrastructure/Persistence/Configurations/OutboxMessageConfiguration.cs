using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages", table =>
        {
            table.HasCheckConstraint("ck_outbox_messages_event_type", "length(btrim(event_type)) > 0");
            table.HasCheckConstraint("ck_outbox_messages_event_version", "event_version > 0");
            table.HasCheckConstraint("ck_outbox_messages_payload", "jsonb_typeof(payload) = 'object'");
            table.HasCheckConstraint("ck_outbox_messages_exchange_name", "length(btrim(exchange_name)) > 0");
            table.HasCheckConstraint("ck_outbox_messages_routing_key", "length(btrim(routing_key)) > 0");
            table.HasCheckConstraint("ck_outbox_messages_attempt_count", "attempt_count >= 0");
            table.HasCheckConstraint("ck_outbox_messages_status", $"status IN ('{nameof(OutboxMessageStatus.Pending)}', '{nameof(OutboxMessageStatus.Publishing)}', '{nameof(OutboxMessageStatus.Published)}', '{nameof(OutboxMessageStatus.NeedsAttention)}')");
            table.HasCheckConstraint("ck_outbox_messages_failure_category", $"last_failure_category IS NULL OR last_failure_category IN ('{nameof(OutboxFailureCategory.BrokerUnavailable)}', '{nameof(OutboxFailureCategory.ConfirmTimeout)}', '{nameof(OutboxFailureCategory.NegativeConfirmation)}', '{nameof(OutboxFailureCategory.OutcomeUnknown)}', '{nameof(OutboxFailureCategory.Unroutable)}', '{nameof(OutboxFailureCategory.UnsupportedStoredContract)}')");
            table.HasCheckConstraint("ck_outbox_messages_lifecycle", $"(status = '{nameof(OutboxMessageStatus.Pending)}' AND next_attempt_at IS NOT NULL AND claim_token IS NULL AND lease_expires_at IS NULL AND published_at IS NULL) OR (status = '{nameof(OutboxMessageStatus.Publishing)}' AND next_attempt_at IS NULL AND claim_token IS NOT NULL AND lease_expires_at IS NOT NULL AND published_at IS NULL) OR (status = '{nameof(OutboxMessageStatus.Published)}' AND next_attempt_at IS NULL AND claim_token IS NULL AND lease_expires_at IS NULL AND published_at IS NOT NULL) OR (status = '{nameof(OutboxMessageStatus.NeedsAttention)}' AND next_attempt_at IS NULL AND claim_token IS NULL AND lease_expires_at IS NULL AND published_at IS NULL)");
        });

        builder.HasKey(message => message.MessageId).HasName("pk_outbox_messages");

        builder.Property(message => message.MessageId).HasColumnName("message_id").ValueGeneratedNever();
        builder.Property(message => message.OrderId).HasColumnName("order_id").IsRequired();
        builder.Property(message => message.EventType).HasColumnName("event_type").HasMaxLength(100).IsRequired();
        builder.Property(message => message.EventVersion).HasColumnName("event_version").IsRequired();
        builder.Property(message => message.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(message => message.CorrelationId).HasColumnName("correlation_id").IsRequired();
        builder.Property(message => message.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(message => message.ExchangeName).HasColumnName("exchange_name").HasMaxLength(255).IsRequired();
        builder.Property(message => message.RoutingKey).HasColumnName("routing_key").HasMaxLength(255).IsRequired();
        builder.Property(message => message.Status).HasColumnName("status").HasMaxLength(16).HasConversion<string>().IsRequired();
        builder.Property(message => message.PublishedAt).HasColumnName("published_at").HasColumnType("timestamp with time zone");
        builder.Property(message => message.ClaimToken).HasColumnName("claim_token");
        builder.Property(message => message.LeaseExpiresAt).HasColumnName("lease_expires_at").HasColumnType("timestamp with time zone");
        builder.Property(message => message.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0).IsRequired();
        builder.Property(message => message.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("timestamp with time zone");
        builder.Property(message => message.LastFailureCategory).HasColumnName("last_failure_category").HasMaxLength(32).HasConversion<string>();
        builder.Property(message => message.LastError).HasColumnName("last_error").HasMaxLength(500);

        builder.HasIndex(message => new { message.NextAttemptAt, message.MessageId })
            .HasDatabaseName("ix_outbox_messages_pending")
            .HasFilter($"status = '{nameof(OutboxMessageStatus.Pending)}'");
        builder.HasIndex(message => new { message.LeaseExpiresAt, message.MessageId })
            .HasDatabaseName("ix_outbox_messages_publishing")
            .HasFilter($"status = '{nameof(OutboxMessageStatus.Publishing)}'");
        builder.HasIndex(message => new { message.PublishedAt, message.MessageId })
            .HasDatabaseName("ix_outbox_messages_published")
            .HasFilter($"status = '{nameof(OutboxMessageStatus.Published)}'");
    }
}
