using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class PaymentDatabaseConstraintTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task Payments_SucceededWithFailureCode_IsRejectedByPostgreSql()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var email = $"payment-constraint-{userId:N}@example.com";

        dbContext.AddRange(
            new User(userId, email, email, "test-password-hash", UserRole.Customer, now),
            new Order(orderId, userId, 10m, now.AddMinutes(15), now));

        await dbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO payments (
                    id,
                    order_id,
                    status,
                    amount,
                    provider,
                    provider_payment_id,
                    gateway_idempotency_key,
                    failure_code,
                    refund_attempt_count)
                VALUES (
                    {Guid.NewGuid()},
                    {orderId},
                    {"Succeeded"},
                    {10m},
                    {"Fake"},
                    {$"fake-pay-{Guid.NewGuid():N}"},
                    {$"fake-key-{Guid.NewGuid():N}"},
                    {"DECLINED"},
                    {0})
                """));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal("ck_payments_failure_code_lifecycle", exception.ConstraintName);
    }

    [Fact]
    public async Task ProviderPaymentEvents_DuplicateProviderEventIdentity_IsRejectedByPostgreSql()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var email = $"provider-event-{userId:N}@example.com";
        var providerPaymentId = $"fake-pay-{paymentId:N}";
        var providerEventId = $"fake-event-{Guid.NewGuid():N}";

        var payment = new Payment(
            paymentId,
            orderId,
            10m,
            "Fake",
            providerPaymentId,
            $"fake-key-{paymentId:N}",
            now);

        dbContext.AddRange(
            new User(userId, email, email, "test-password-hash", UserRole.Customer, now),
            new Order(orderId, userId, 10m, now.AddMinutes(15), now),
            payment);

        await dbContext.SaveChangesAsync();

        await InsertProviderPaymentEventAsync(
            dbContext,
            Guid.NewGuid(),
            paymentId,
            providerEventId,
            providerPaymentId,
            now);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertProviderPaymentEventAsync(
                dbContext,
                Guid.NewGuid(),
                paymentId,
                providerEventId,
                providerPaymentId,
                now.AddSeconds(1)));

        Assert.Equal("23505", exception.SqlState);
        Assert.Equal(
            "uq_provider_payment_events_provider_event",
            exception.ConstraintName);
    }

    [Fact]
    public async Task FakeProviderOperations_DuplicateOperationTypeAndIdempotencyKey_IsRejectedByPostgreSql()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var idempotencyKey = $"fake-key-{Guid.NewGuid():N}";

        await InsertFakeProviderOperationAsync(
            dbContext,
            Guid.NewGuid(),
            operationType: "CreatePayment",
            idempotencyKey,
            $"fake-pay-{Guid.NewGuid():N}",
            now);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertFakeProviderOperationAsync(
                dbContext,
                Guid.NewGuid(),
                operationType: "CreatePayment",
                idempotencyKey,
                $"fake-pay-{Guid.NewGuid():N}",
                now.AddSeconds(1)));

        Assert.Equal("23505", exception.SqlState);
        Assert.Equal(
            "uq_fake_provider_operations_type_key",
            exception.ConstraintName);
    }

    [Fact]
    public async Task FakeProviderOperations_CreatePaymentWithParentIdentity_IsRejectedByPostgreSql()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertFakeProviderOperationAsync(
                dbContext,
                Guid.NewGuid(),
                operationType: "CreatePayment",
                $"fake-key-{Guid.NewGuid():N}",
                $"fake-pay-{Guid.NewGuid():N}",
                DateTimeOffset.UtcNow,
                parentProviderPaymentId: "fake-pay-parent"));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal(
            "ck_fake_provider_operations_parent_identity",
            exception.ConstraintName);
    }

    [Fact]
    public async Task FakeProviderOperations_RefundPaymentWithoutParentIdentity_IsRejectedByPostgreSql()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertFakeProviderOperationAsync(
                dbContext,
                Guid.NewGuid(),
                operationType: "RefundPayment",
                idempotencyKey: $"fake-refund-key-{Guid.NewGuid():N}",
                providerResourceId: $"fake-refund-{Guid.NewGuid():N}",
                now: DateTimeOffset.UtcNow));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal(
            "ck_fake_provider_operations_parent_identity",
            exception.ConstraintName);
    }

    [Fact]
    public async Task FakeProviderOperations_DuplicateOperationTypeAndProviderResourceId_IsRejectedByPostgreSql()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var providerResourceId = $"fake-pay-{Guid.NewGuid():N}";

        await InsertFakeProviderOperationAsync(
            dbContext,
            Guid.NewGuid(),
            operationType: "CreatePayment",
            idempotencyKey: $"fake-key-{Guid.NewGuid():N}",
            providerResourceId: providerResourceId,
            now: now);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertFakeProviderOperationAsync(
                dbContext,
                Guid.NewGuid(),
                operationType: "CreatePayment",
                idempotencyKey: $"fake-key-{Guid.NewGuid():N}",
                providerResourceId: providerResourceId,
                now: now.AddSeconds(1)));

        Assert.Equal("23505", exception.SqlState);
        Assert.Equal(
            "uq_fake_provider_operations_type_resource",
            exception.ConstraintName);
    }

    [Fact]
    public async Task FakeProviderOperations_SameProviderResourceIdAcrossOperationTypes_IsAllowed()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var providerResourceId = $"fake-resource-{Guid.NewGuid():N}";

        var createRows = await InsertFakeProviderOperationAsync(
            dbContext,
            Guid.NewGuid(),
            operationType: "CreatePayment",
            idempotencyKey: $"fake-create-key-{Guid.NewGuid():N}",
            providerResourceId: providerResourceId,
            now: now);

        var refundRows = await InsertFakeProviderOperationAsync(
            dbContext,
            Guid.NewGuid(),
            operationType: "RefundPayment",
            idempotencyKey: $"fake-refund-key-{Guid.NewGuid():N}",
            providerResourceId: providerResourceId,
            now: now.AddSeconds(1),
            parentProviderPaymentId: "fake-pay-parent");

        Assert.Equal(1, createRows);
        Assert.Equal(1, refundRows);
    }

    [Fact]
    public async Task Payments_DuplicateOrderId_IsRejectedByPostgreSql()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var email = $"payment-order-unique-{userId:N}@example.com";

        dbContext.AddRange(
            new User(userId, email, email, "test-password-hash", UserRole.Customer, now),
            new Order(orderId, userId, 10m, now.AddMinutes(15), now),
            new Payment(
                Guid.NewGuid(),
                orderId,
                10m,
                "Fake",
                $"fake-pay-{Guid.NewGuid():N}",
                $"fake-key-{Guid.NewGuid():N}",
                now));

        await dbContext.SaveChangesAsync();

        dbContext.Add(new Payment(
            Guid.NewGuid(),
            orderId,
            10m,
            "Fake",
            $"fake-pay-{Guid.NewGuid():N}",
            $"fake-key-{Guid.NewGuid():N}",
            now.AddSeconds(1)));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal("23505", postgresException.SqlState);
        Assert.Equal("uq_payments_order_id", postgresException.ConstraintName);
    }

    [Fact]
    public async Task Payments_ConcurrentCreationForSameOrder_AllowsExactlyOne()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var email = $"payment-order-race-{userId:N}@example.com";

        await using (var setupDbContext = new OrderSystemDbContext(options))
        {
            await setupDbContext.Database.MigrateAsync();
            setupDbContext.AddRange(
                new User(userId, email, email, "test-password-hash", UserRole.Customer, now),
                new Order(orderId, userId, 10m, now.AddMinutes(15), now));
            await setupDbContext.SaveChangesAsync();
        }

        using var ready = new CountdownEvent(2);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstAttempt = TryInsertPaymentAsync(
            options,
            orderId,
            now,
            ready,
            start.Task);
        var secondAttempt = TryInsertPaymentAsync(
            options,
            orderId,
            now.AddSeconds(1),
            ready,
            start.Task);

        Assert.True(
            ready.Wait(TimeSpan.FromSeconds(5)),
            "Both payment inserts must be ready before the race starts.");
        start.SetResult(true);

        var attempts = await Task.WhenAll(
            firstAttempt,
            secondAttempt);

        Assert.Single(attempts, attempt => attempt is null);

        var failedAttempt = Assert.Single(attempts, attempt => attempt is not null);
        var conflict = Assert.IsType<PostgresException>(failedAttempt);

        Assert.Equal("23505", conflict.SqlState);
        Assert.Equal("uq_payments_order_id", conflict.ConstraintName);
    }

    [Fact]
    public async Task PaymentAndProviderEventForeignKeys_AreRestrictedByPostgreSql()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var providerPaymentId = $"fake-pay-{paymentId:N}";
        var email = $"payment-fk-{userId:N}@example.com";

        dbContext.AddRange(
            new User(userId, email, email, "test-password-hash", UserRole.Customer, now),
            new Order(orderId, userId, 10m, now.AddMinutes(15), now),
            new Payment(
                paymentId,
                orderId,
                10m,
                "Fake",
                providerPaymentId,
                $"fake-key-{paymentId:N}",
                now));
        await dbContext.SaveChangesAsync();

        await InsertProviderPaymentEventAsync(
            dbContext,
            Guid.NewGuid(),
            paymentId,
            $"fake-event-{Guid.NewGuid():N}",
            providerPaymentId,
            now);

        var orderException = await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM orders WHERE id = {orderId}"));

        Assert.Equal("23001", orderException.SqlState);
        Assert.Equal("fk_payments_orders_order_id", orderException.ConstraintName);

        var paymentException = await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM payments WHERE id = {paymentId}"));

        Assert.Equal("23001", paymentException.SqlState);
        Assert.Equal(
            "fk_provider_payment_events_payments_payment_id",
            paymentException.ConstraintName);
    }

    [Fact]
    public async Task Payments_RefundedWithoutRequiredRefundEvidence_IsRejectedByPostgreSql()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var email = $"payment-refund-constraint-{userId:N}@example.com";

        dbContext.AddRange(
            new User(userId, email, email, "test-password-hash", UserRole.Customer, now),
            new Order(orderId, userId, 10m, now.AddMinutes(15), now));
        await dbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertPaymentAsync(
                dbContext,
                Guid.NewGuid(),
                orderId,
                $"fake-pay-{Guid.NewGuid():N}",
                $"fake-key-{Guid.NewGuid():N}",
                "Refunded",
                now));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal("ck_payments_refund_lifecycle", exception.ConstraintName);
    }

    [Fact]
    public async Task Payments_DuplicateGatewayIdempotencyKey_IsRejectedByPostgreSql()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var firstOrderId = Guid.NewGuid();
        var secondOrderId = Guid.NewGuid();
        var email = $"payment-gateway-key-{userId:N}@example.com";
        var gatewayIdempotencyKey = $"fake-key-{Guid.NewGuid():N}";

        dbContext.AddRange(
            new User(userId, email, email, "test-password-hash", UserRole.Customer, now),
            new Order(firstOrderId, userId, 10m, now.AddMinutes(15), now),
            new Order(secondOrderId, userId, 10m, now.AddMinutes(15), now));
        await dbContext.SaveChangesAsync();

        await InsertPaymentAsync(
            dbContext,
            Guid.NewGuid(),
            firstOrderId,
            $"fake-pay-{Guid.NewGuid():N}",
            gatewayIdempotencyKey,
            "Pending",
            now);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertPaymentAsync(
                dbContext,
                Guid.NewGuid(),
                secondOrderId,
                $"fake-pay-{Guid.NewGuid():N}",
                gatewayIdempotencyKey,
                "Pending",
                now.AddSeconds(1)));

        Assert.Equal("23505", exception.SqlState);
        Assert.Equal("uq_payments_gateway_idempotency_key", exception.ConstraintName);
    }

    [Fact]
    public async Task FakeProviderOperations_AvailableAtBeforeCreatedAt_IsRejectedByPostgreSql()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        var createdAt = DateTimeOffset.UtcNow;

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertFakeProviderOperationAsync(
                dbContext,
                Guid.NewGuid(),
                operationType: "CreatePayment",
                idempotencyKey: $"fake-key-{Guid.NewGuid():N}",
                providerResourceId: $"fake-pay-{Guid.NewGuid():N}",
                now: createdAt,
                availableAt: createdAt.AddMilliseconds(-1)));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal(
            "ck_fake_provider_operations_available_after_created",
            exception.ConstraintName);
    }

    private static Task<int> InsertProviderPaymentEventAsync(
    OrderSystemDbContext dbContext,
    Guid id,
    Guid paymentId,
    string providerEventId,
    string providerPaymentId,
    DateTimeOffset now) =>
    dbContext.Database.ExecuteSqlInterpolatedAsync(
        $"""
        INSERT INTO provider_payment_events (
            id,
            payment_id,
            provider,
            provider_event_id,
            provider_payment_id,
            event_type,
            payload_hash,
            occurred_at,
            received_at,
            processed_at)
        VALUES (
            {id},
            {paymentId},
            {"Fake"},
            {providerEventId},
            {providerPaymentId},
            {"payment.succeeded"},
            {new string('a', 64)},
            {now},
            {now},
            {now})
        """
    );

    private static Task<int> InsertPaymentAsync(
        OrderSystemDbContext dbContext,
        Guid id,
        Guid orderId,
        string providerPaymentId,
        string gatewayIdempotencyKey,
        string status,
        DateTimeOffset now) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO payments (
                id,
                order_id,
                status,
                amount,
                provider,
                provider_payment_id,
                gateway_idempotency_key,
                refund_attempt_count,
                created_at,
                updated_at)
            VALUES (
                {id},
                {orderId},
                {status},
                {10m},
                {"Fake"},
                {providerPaymentId},
                {gatewayIdempotencyKey},
                {0},
                {now},
                {now})
            """);

    private static async Task<Exception?> TryInsertPaymentAsync(
        DbContextOptions<OrderSystemDbContext> options,
        Guid orderId,
        DateTimeOffset now,
        CountdownEvent ready,
        Task start)
    {
        await using var dbContext = new OrderSystemDbContext(options);
        ready.Signal();
        await start;

        try
        {
            await InsertPaymentAsync(
                dbContext,
                Guid.NewGuid(),
                orderId,
                $"fake-pay-{Guid.NewGuid():N}",
                $"fake-key-{Guid.NewGuid():N}",
                "Pending",
                now);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Task<int> InsertFakeProviderOperationAsync(
    OrderSystemDbContext dbContext,
    Guid id,
    string operationType,
    string idempotencyKey,
    string providerResourceId,
    DateTimeOffset now,
    string? parentProviderPaymentId = null,
    DateTimeOffset? availableAt = null) =>
    dbContext.Database.ExecuteSqlInterpolatedAsync(
        $"""
        INSERT INTO fake_provider.operations (
            id,
            operation_type,
            idempotency_key,
            provider_resource_id,
            parent_provider_payment_id,
            scenario,
            status,
            amount,
            available_at,
            created_at,
            updated_at)
        VALUES (
            {id},
            {operationType},
            {idempotencyKey},
            {providerResourceId},
            {parentProviderPaymentId},
            {"SUCCESS"},
            {"Succeeded"},
            {10m},
            {availableAt},
            {now},
            {now})
        """
    );
}
