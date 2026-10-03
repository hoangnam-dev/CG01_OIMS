using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Payments;
using OrderSystem.Infrastructure.Payments.FakeProvider;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class FakeProviderOperationConfiguration
    : IEntityTypeConfiguration<FakeProviderOperation>
{
    public void Configure(EntityTypeBuilder<FakeProviderOperation> builder)
    {
        builder.ToTable("operations", "fake_provider", table =>
        {
            table.HasCheckConstraint(
                "ck_fake_provider_operations_type",
                $"operation_type IN ('{nameof(FakeProviderOperationType.CreatePayment)}', '{nameof(FakeProviderOperationType.RefundPayment)}')");

            table.HasCheckConstraint(
                "ck_fake_provider_operations_scenario",
                $"scenario IN ('{PaymentScenarioCodes.Success}', '{PaymentScenarioCodes.Failed}', '{PaymentScenarioCodes.SuccessButResponseLost}', '{PaymentScenarioCodes.DelayedSuccess}')");

            table.HasCheckConstraint(
                "ck_fake_provider_operations_status",
                $"status IN ('{nameof(FakeProviderOperationStatus.Pending)}', '{nameof(FakeProviderOperationStatus.Processing)}', '{nameof(FakeProviderOperationStatus.Succeeded)}', '{nameof(FakeProviderOperationStatus.Failed)}')");

            table.HasCheckConstraint(
                "ck_fake_provider_operations_amount_non_negative",
                "amount >= 0");

            table.HasCheckConstraint(
                "ck_fake_provider_operations_updated_after_created",
                "updated_at >= created_at");

            table.HasCheckConstraint(
                "ck_fake_provider_operations_available_after_created",
                "available_at IS NULL OR available_at >= created_at");

            table.HasCheckConstraint(
                "ck_fake_provider_operations_parent_identity",
                "(operation_type = 'CreatePayment' AND parent_provider_payment_id IS NULL) OR " +
                "(operation_type = 'RefundPayment' AND parent_provider_payment_id IS NOT NULL " +
                "AND parent_provider_payment_id = btrim(parent_provider_payment_id) " +
                "AND length(parent_provider_payment_id) BETWEEN 1 AND 128)");
        });

        builder.HasKey(operation => operation.Id)
            .HasName("pk_fake_provider_operations");

        builder.Property(operation => operation.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(operation => operation.OperationType)
            .HasColumnName("operation_type")
            .HasMaxLength(32)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(operation => operation.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(operation => operation.ProviderResourceId)
            .HasColumnName("provider_resource_id")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(operation => operation.ParentProviderPaymentId)
            .HasColumnName("parent_provider_payment_id")
            .HasMaxLength(128);

        builder.Property(operation => operation.Scenario)
            .HasColumnName("scenario")
            .HasMaxLength(32)
            .HasConversion(
                scenario => PaymentScenarioCodes.ToCode(scenario),
                code => PaymentScenarioCodes.Parse(code))
            .IsRequired();

        builder.Property(operation => operation.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(operation => operation.Amount)
            .HasColumnName("amount")
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(operation => operation.AvailableAt)
            .HasColumnName("available_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(operation => operation.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(operation => operation.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasIndex(operation => new
        {
            operation.OperationType,
            operation.IdempotencyKey
        })
            .IsUnique()
            .HasDatabaseName("uq_fake_provider_operations_type_key");

        builder.HasIndex(operation => new
        {
            operation.OperationType,
            operation.ProviderResourceId
        })
            .IsUnique()
            .HasDatabaseName("uq_fake_provider_operations_type_resource");
    }
}
