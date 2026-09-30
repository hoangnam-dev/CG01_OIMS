using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments", table =>
        {
            table.HasCheckConstraint(
                "ck_payments_amount_non_negative",
                "amount >= 0");

            table.HasCheckConstraint(
                "ck_payments_refund_attempt_count_non_negative",
                "refund_attempt_count >= 0");

            table.HasCheckConstraint(
                "ck_payments_status",
                "status IN ('Pending', 'Processing', 'Succeeded', 'Failed', 'RefundPending', 'Refunded')");

            table.HasCheckConstraint(
                "ck_payments_failure_code_lifecycle",
                "(status = 'Failed' AND failure_code IS NOT NULL AND failure_code = btrim(failure_code) AND length(failure_code) BETWEEN 1 AND 64) OR (status <> 'Failed' AND failure_code IS NULL)");

            table.HasCheckConstraint(
                "ck_payments_refund_lifecycle",
                "(status IN ('Pending', 'Processing', 'Succeeded', 'Failed') " +
                "AND refund_idempotency_key IS NULL " +
                "AND provider_refund_id IS NULL " +
                "AND refund_requested_at IS NULL " +
                "AND refund_attempt_count = 0 " +
                "AND next_refund_attempt_at IS NULL " +
                "AND manual_review_required_at IS NULL " +
                "AND refunded_at IS NULL) " +
                "OR " +
                "(status = 'RefundPending' " +
                "AND refund_idempotency_key IS NOT NULL " +
                "AND refund_idempotency_key = btrim(refund_idempotency_key) " +
                "AND length(refund_idempotency_key) BETWEEN 1 AND 128 " +
                "AND provider_refund_id IS NULL " +
                "AND refund_requested_at IS NOT NULL " +
                "AND refunded_at IS NULL " +
                "AND ((manual_review_required_at IS NULL AND next_refund_attempt_at IS NOT NULL) " +
                "OR (manual_review_required_at IS NOT NULL AND next_refund_attempt_at IS NULL))) " +
                "OR " +
                "(status = 'Refunded' " +
                "AND refund_idempotency_key IS NOT NULL " +
                "AND refund_idempotency_key = btrim(refund_idempotency_key) " +
                "AND length(refund_idempotency_key) BETWEEN 1 AND 128 " +
                "AND provider_refund_id IS NOT NULL " +
                "AND provider_refund_id = btrim(provider_refund_id) " +
                "AND length(provider_refund_id) BETWEEN 1 AND 128 " +
                "AND refund_requested_at IS NOT NULL " +
                "AND refunded_at IS NOT NULL " +
                "AND refunded_at >= refund_requested_at " +
                "AND next_refund_attempt_at IS NULL " +
                "AND manual_review_required_at IS NULL)");
        });

        builder.HasKey(payment => payment.Id)
            .HasName("pk_payments");

        builder.Property(payment => payment.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(payment => payment.OrderId)
            .HasColumnName("order_id")
            .IsRequired();

        builder.Property(payment => payment.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(payment => payment.Amount)
            .HasColumnName("amount")
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(payment => payment.Provider)
            .HasColumnName("provider")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(payment => payment.ProviderPaymentId)
            .HasColumnName("provider_payment_id")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(payment => payment.GatewayIdempotencyKey)
            .HasColumnName("gateway_idempotency_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(payment => payment.RefundIdempotencyKey)
            .HasColumnName("refund_idempotency_key")
            .HasMaxLength(128);

        builder.Property(payment => payment.ProviderRefundId)
            .HasColumnName("provider_refund_id")
            .HasMaxLength(128);

        builder.Property(payment => payment.FailureCode)
            .HasColumnName("failure_code")
            .HasMaxLength(64);

        builder.Property(payment => payment.LastStatusCheckedAt)
            .HasColumnName("last_status_checked_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(payment => payment.RefundRequestedAt)
            .HasColumnName("refund_requested_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(payment => payment.RefundAttemptCount)
            .HasColumnName("refund_attempt_count")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(payment => payment.NextRefundAttemptAt)
            .HasColumnName("next_refund_attempt_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(payment => payment.ManualReviewRequiredAt)
            .HasColumnName("manual_review_required_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(payment => payment.RefundedAt)
            .HasColumnName("refunded_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(payment => payment.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(payment => payment.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(payment => payment.OrderId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_payments_orders_order_id");

        builder.HasIndex(payment => payment.OrderId)
            .IsUnique()
            .HasDatabaseName("uq_payments_order_id");

        builder.HasIndex(payment => payment.GatewayIdempotencyKey)
            .IsUnique()
            .HasDatabaseName("uq_payments_gateway_idempotency_key");

        builder.HasIndex(payment => payment.ProviderPaymentId)
            .IsUnique()
            .HasDatabaseName("uq_payments_provider_payment_id");

        builder.HasIndex(payment => payment.RefundIdempotencyKey)
            .IsUnique()
            .HasDatabaseName("uq_payments_refund_idempotency_key");

        builder.HasIndex(payment => payment.ProviderRefundId)
            .IsUnique()
            .HasDatabaseName("uq_payments_provider_refund_id");

        builder.HasIndex(payment => new
        {
            payment.LastStatusCheckedAt,
            payment.CreatedAt,
            payment.Id
        })
            .HasFilter("status IN ('Pending', 'Processing')")
            .HasDatabaseName("ix_payments_unresolved");

        builder.HasIndex(payment => new
        {
            payment.NextRefundAttemptAt,
            payment.Id
        })
            .HasFilter("status = 'RefundPending' AND manual_review_required_at IS NULL")
            .HasDatabaseName("ix_payments_refund_pending");
    }
}