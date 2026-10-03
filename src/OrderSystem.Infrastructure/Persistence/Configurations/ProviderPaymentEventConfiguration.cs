using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Payments;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class ProviderPaymentEventConfiguration
    : IEntityTypeConfiguration<ProviderPaymentEvent>
{
    public void Configure(EntityTypeBuilder<ProviderPaymentEvent> builder)
    {
        builder.ToTable("provider_payment_events");

        builder.HasKey(paymentEvent => paymentEvent.Id)
            .HasName("pk_provider_payment_events");

        builder.Property(paymentEvent => paymentEvent.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(paymentEvent => paymentEvent.PaymentId)
            .HasColumnName("payment_id")
            .IsRequired();

        builder.Property(paymentEvent => paymentEvent.Provider)
            .HasColumnName("provider")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(paymentEvent => paymentEvent.ProviderEventId)
            .HasColumnName("provider_event_id")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(paymentEvent => paymentEvent.ProviderPaymentId)
            .HasColumnName("provider_payment_id")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(paymentEvent => paymentEvent.EventType)
            .HasColumnName("event_type")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(paymentEvent => paymentEvent.PayloadHash)
            .HasColumnName("payload_hash")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(paymentEvent => paymentEvent.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(paymentEvent => paymentEvent.ReceivedAt)
            .HasColumnName("received_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(paymentEvent => paymentEvent.ProcessedAt)
            .HasColumnName("processed_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasOne<Payment>()
            .WithMany()
            .HasForeignKey(paymentEvent => paymentEvent.PaymentId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_provider_payment_events_payments_payment_id");

        builder.HasIndex(paymentEvent => new
        {
            paymentEvent.Provider,
            paymentEvent.ProviderEventId
        })
            .IsUnique()
            .HasDatabaseName("uq_provider_payment_events_provider_event");

        builder.HasIndex(paymentEvent => new
        {
            paymentEvent.Provider,
            paymentEvent.ProviderPaymentId,
            paymentEvent.OccurredAt,
            paymentEvent.Id
        })
            .HasDatabaseName("ix_provider_payment_events_provider_payment_occurred_id");
    }
}