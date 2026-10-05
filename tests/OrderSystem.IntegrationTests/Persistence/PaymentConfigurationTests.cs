using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class PaymentConfigurationTests
{
    [Fact]
    public void Model_MapsPaymentToPaymentsTableWithOrderRelationship()
    {
        using var dbContext = CreateDbContext();

        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entityType = model.FindEntityType(typeof(Payment))
            ?? throw new InvalidOperationException("Payment is missing from the EF Core model.");
        var table = StoreObjectIdentifier.Table("payments", schema: null);

        Assert.Equal("payments", entityType.GetTableName());
        Assert.Equal(["Id"], entityType.FindPrimaryKey()!.Properties.Select(x => x.Name));

        AssertProperty(entityType, table, nameof(Payment.Id), "id", false);
        AssertProperty(entityType, table, nameof(Payment.OrderId), "order_id", false);
        AssertProperty(entityType, table, nameof(Payment.Status), "status", false, 32);
        AssertProperty(entityType, table, nameof(Payment.Amount), "amount", false);
        AssertProperty(entityType, table, nameof(Payment.Provider), "provider", false, 32);
        AssertProperty(entityType, table, nameof(Payment.ProviderPaymentId), "provider_payment_id", false, 128);
        AssertProperty(entityType, table, nameof(Payment.GatewayIdempotencyKey), "gateway_idempotency_key", false, 128);
        AssertProperty(entityType, table, nameof(Payment.Scenario), "scenario", true, 32);
        AssertProperty(entityType, table, nameof(Payment.RefundIdempotencyKey), "refund_idempotency_key", true, 128);
        AssertProperty(entityType, table, nameof(Payment.ProviderRefundId), "provider_refund_id", true, 128);
        AssertProperty(entityType, table, nameof(Payment.FailureCode), "failure_code", true, 64);
        AssertProperty(entityType, table, nameof(Payment.CreatedAt), "created_at", false);
        AssertProperty(entityType, table, nameof(Payment.UpdatedAt), "updated_at", false);

        var scenarioConverter = entityType.FindProperty(nameof(Payment.Scenario))!.GetValueConverter();

        Assert.NotNull(scenarioConverter);
        Assert.Equal(PaymentScenarioCodes.DelayedSuccess, scenarioConverter.ConvertToProvider(PaymentScenario.DelayedSuccess));
        Assert.Equal(PaymentScenario.DelayedSuccess, scenarioConverter.ConvertFromProvider(PaymentScenarioCodes.DelayedSuccess));

        var foreignKey = Assert.Single(entityType.GetForeignKeys());

        Assert.Equal(typeof(Order), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal([nameof(Payment.OrderId)], foreignKey.Properties.Select(x => x.Name));
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.Equal("fk_payments_orders_order_id", foreignKey.GetConstraintName());
    }

    [Fact]
    public void Model_ProtectsPaymentStatusAmountAttemptCountAndFailureCodeLifecycle()
    {
        using var dbContext = CreateDbContext();

        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entityType = model.FindEntityType(typeof(Payment))
            ?? throw new InvalidOperationException("Payment is missing from the EF Core model.");

        var constraints = entityType.GetCheckConstraints()
            .ToDictionary(constraint => constraint.Name!, constraint => constraint.Sql);

        Assert.Equal("amount >= 0", constraints["ck_payments_amount_non_negative"]);
        Assert.Equal(
            "refund_attempt_count >= 0",
            constraints["ck_payments_refund_attempt_count_non_negative"]);
        Assert.Equal(
            "status IN ('Pending', 'Processing', 'Succeeded', 'Failed', 'RefundPending', 'Refunded')",
            constraints["ck_payments_status"]);
        Assert.Equal(
            "scenario IS NULL OR scenario IN ('SUCCESS', 'FAILED', 'CLIENT_RESPONSE_LOST', 'PROVIDER_RESPONSE_LOST', 'DELAYED_SUCCESS')",
            constraints["ck_payments_scenario"]);
        Assert.Equal(
            "(status = 'Failed' AND failure_code IS NOT NULL AND failure_code = btrim(failure_code) AND length(failure_code) BETWEEN 1 AND 64) OR (status <> 'Failed' AND failure_code IS NULL)",
            constraints["ck_payments_failure_code_lifecycle"]);
    }

    [Fact]
    public void Model_MapsPaymentUniquenessAndRecoveryIndexes()
    {
        using var dbContext = CreateDbContext();

        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entityType = model.FindEntityType(typeof(Payment))
            ?? throw new InvalidOperationException("Payment is missing from the EF Core model.");

        AssertIndex(
            entityType,
            "uq_payments_order_id",
            [nameof(Payment.OrderId)],
            unique: true);

        AssertIndex(
            entityType,
            "uq_payments_gateway_idempotency_key",
            [nameof(Payment.GatewayIdempotencyKey)],
            unique: true);

        AssertIndex(
            entityType,
            "uq_payments_provider_payment_id",
            [nameof(Payment.ProviderPaymentId)],
            unique: true);

        AssertIndex(
            entityType,
            "uq_payments_refund_idempotency_key",
            [nameof(Payment.RefundIdempotencyKey)],
            unique: true);

        AssertIndex(
            entityType,
            "uq_payments_provider_refund_id",
            [nameof(Payment.ProviderRefundId)],
            unique: true);

        var unresolvedIndex = entityType.GetIndexes().Single(
            index => index.GetDatabaseName() == "ix_payments_unresolved");

        Assert.Equal(
            [
                nameof(Payment.LastStatusCheckedAt),
            nameof(Payment.CreatedAt),
            nameof(Payment.Id)
            ],
            unresolvedIndex.Properties.Select(property => property.Name));
        Assert.Equal(
            "status IN ('Pending', 'Processing')",
            unresolvedIndex.GetFilter());

        var refundPendingIndex = entityType.GetIndexes().Single(
            index => index.GetDatabaseName() == "ix_payments_refund_pending");

        Assert.Equal(
            [
                nameof(Payment.NextRefundAttemptAt),
            nameof(Payment.Id)
            ],
            refundPendingIndex.Properties.Select(property => property.Name));
        Assert.Equal(
            "status = 'RefundPending' AND manual_review_required_at IS NULL",
            refundPendingIndex.GetFilter());
    }

    [Fact]
    public void Model_ProtectsPaymentRefundLifecycle()
    {
        using var dbContext = CreateDbContext();

        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entityType = model.FindEntityType(typeof(Payment))
            ?? throw new InvalidOperationException("Payment is missing from the EF Core model.");

        var constraint = entityType.GetCheckConstraints().Single(
            candidate => candidate.Name == "ck_payments_refund_lifecycle");

        Assert.Contains(
            "status IN ('Pending', 'Processing', 'Succeeded', 'Failed')",
            constraint.Sql);
        Assert.Contains("status = 'RefundPending'", constraint.Sql);
        Assert.Contains("status = 'Refunded'", constraint.Sql);

        Assert.Contains("refund_idempotency_key IS NOT NULL", constraint.Sql);
        Assert.Contains("refund_requested_at IS NOT NULL", constraint.Sql);
        Assert.Contains("provider_refund_id IS NOT NULL", constraint.Sql);
        Assert.Contains("refunded_at >= refund_requested_at", constraint.Sql);
        Assert.Contains("next_refund_attempt_at IS NULL", constraint.Sql);
    }

    private static OrderSystemDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql("Host=localhost;Database=oims_model_tests;Username=oims;Password=unused")
            .Options;

        return new OrderSystemDbContext(options);
    }

    private static void AssertProperty(
        IEntityType entityType,
        StoreObjectIdentifier table,
        string propertyName,
        string columnName,
        bool nullable,
        int? maximumLength = null)
    {
        var property = entityType.FindProperty(propertyName);

        Assert.NotNull(property);
        Assert.Equal(columnName, property.GetColumnName(table));
        Assert.Equal(nullable, property.IsNullable);
        Assert.Equal(maximumLength, property.GetMaxLength());
    }

    private static void AssertIndex(
    IEntityType entityType,
    string name,
    string[] propertyNames,
    bool unique)
    {
        var index = entityType.GetIndexes().Single(
            candidate => candidate.GetDatabaseName() == name);

        Assert.Equal(propertyNames, index.Properties.Select(property => property.Name));
        Assert.Equal(unique, index.IsUnique);
    }
}
