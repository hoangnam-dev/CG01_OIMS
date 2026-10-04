using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Orders;
using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Idempotency;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Domain.Products;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Payments.FakeProvider;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Payments;

[Collection(PostgreSqlCollectionDefinition.Name)]

public sealed class PaymentInitiationTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task InitiateAsync_SameKeyAndEquivalentIntent_CreatesOnePaymentWithStableGatewayKey()
    {
        var now = new DateTimeOffset(
            2026,
            10,
            1,
            12,
            0,
            0,
            TimeSpan.Zero);

        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var clock = new FakeClock(now);

        await using var factory = CreateFactory(userId, clock);
        await MigrateAsync(factory);

        await SeedPendingPaymentOrderAsync(
            factory,
            userId,
            orderId,
            now);

        var request = new InitiatePaymentRequest(
            OrderId: orderId,
            IdempotencyKey: idempotencyKey,
            Scenario: PaymentScenario.Success);

        ApplicationResult<PaymentInitiationResult> firstHandling;
        ApplicationResult<PaymentInitiationResult> replayHandling;

        using (var firstScope = factory.Services.CreateScope())
        {
            var service = firstScope.ServiceProvider
                .GetRequiredService<PaymentCommandService>();

            firstHandling = await service.InitiateAsync(
                request,
                CancellationToken.None);
        }

        using (var replayScope = factory.Services.CreateScope())
        {
            var service = replayScope.ServiceProvider
                .GetRequiredService<PaymentCommandService>();

            replayHandling = await service.InitiateAsync(
                request,
                CancellationToken.None);
        }

        Assert.True(firstHandling.IsSuccess);
        Assert.True(replayHandling.IsSuccess);

        var firstResult = Assert.IsType<PaymentInitiationResult>(
            firstHandling.Value);

        var replayResult = Assert.IsType<PaymentInitiationResult>(
            replayHandling.Value);

        Assert.False(firstResult.IsReplay);
        Assert.True(replayResult.IsReplay);
        Assert.Equal(firstResult.PaymentId, replayResult.PaymentId);
        Assert.Equal(PaymentStatus.Succeeded, firstResult.Status);
        Assert.Equal(PaymentStatus.Succeeded, replayResult.Status);
        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var payments = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == orderId)
            .ToListAsync();

        var payment = Assert.Single(payments);

        Assert.Equal(firstResult.PaymentId, payment.Id);
        Assert.Equal(orderId, payment.OrderId);
        Assert.Equal(125_000m, payment.Amount);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.False(string.IsNullOrWhiteSpace(payment.GatewayIdempotencyKey));
        Assert.Equal($"fake-pay-{payment.Id:D}", payment.ProviderPaymentId);

        var idempotencyRequest = await db.IdempotencyRequests
            .AsNoTracking()
            .SingleAsync(item =>
                item.UserId == userId &&
                item.Operation == IdempotencyOperation.InitiatePayment &&
                item.IdempotencyKey == idempotencyKey);

        Assert.Equal(IdempotencyRequestStatus.Completed, idempotencyRequest.Status);
        Assert.Equal(payment.Id, idempotencyRequest.ResourceId);
        Assert.Null(idempotencyRequest.HttpStatusCode);
        Assert.Null(idempotencyRequest.ResponseBodyJson);
        Assert.Equal(
            PaymentIntentHasher.Hash(orderId, PaymentScenario.Success),
            idempotencyRequest.RequestHash);

        await AssertSingleSucceededCreateOperationAsync(db, payment);
    }

    [Fact]
    public async Task InitiateAsync_SameKeyWithDifferentIntent_ReturnsConflictAndPreservesOriginalPayment()
    {
        var now = new DateTimeOffset(
            2026,
            10,
            1,
            12,
            0,
            0,
            TimeSpan.Zero);

        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var clock = new FakeClock(now);

        await using var factory = CreateFactory(userId, clock);
        await MigrateAsync(factory);

        await SeedPendingPaymentOrderAsync(
            factory,
            userId,
            orderId,
            now);

        var originalRequest = new InitiatePaymentRequest(
            OrderId: orderId,
            IdempotencyKey: idempotencyKey,
            Scenario: PaymentScenario.Success);

        var conflictingRequest = new InitiatePaymentRequest(
            OrderId: orderId,
            IdempotencyKey: idempotencyKey,
            Scenario: PaymentScenario.Failed);

        ApplicationResult<PaymentInitiationResult> originalHandling;
        ApplicationResult<PaymentInitiationResult> conflictHandling;

        using (var originalScope = factory.Services.CreateScope())
        {
            var service = originalScope.ServiceProvider
                .GetRequiredService<PaymentCommandService>();

            originalHandling = await service.InitiateAsync(
                originalRequest,
                CancellationToken.None);
        }

        using (var conflictScope = factory.Services.CreateScope())
        {
            var service = conflictScope.ServiceProvider
                .GetRequiredService<PaymentCommandService>();

            conflictHandling = await service.InitiateAsync(
                conflictingRequest,
                CancellationToken.None);
        }

        Assert.True(originalHandling.IsSuccess);

        var originalResult = Assert.IsType<PaymentInitiationResult>(
            originalHandling.Value);

        Assert.False(conflictHandling.IsSuccess);
        Assert.NotNull(conflictHandling.Error);
        Assert.Equal(
            "IDEMPOTENCY_KEY_REUSED",
            conflictHandling.Error.Code);

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var payments = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == orderId)
            .ToListAsync();

        var payment = Assert.Single(payments);

        Assert.Equal(originalResult.PaymentId, payment.Id);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);

        var idempotencyRows = await db.IdempotencyRequests
            .AsNoTracking()
            .Where(item =>
                item.UserId == userId &&
                item.Operation == IdempotencyOperation.InitiatePayment &&
                item.IdempotencyKey == idempotencyKey)
            .ToListAsync();

        var idempotencyRequest = Assert.Single(idempotencyRows);

        Assert.Equal(
            PaymentIntentHasher.Hash(
                orderId,
                PaymentScenario.Success),
            idempotencyRequest.RequestHash);

        Assert.Equal(
            IdempotencyRequestStatus.Completed,
            idempotencyRequest.Status);

        Assert.Equal(
            payment.Id,
            idempotencyRequest.ResourceId);

        await AssertSingleSucceededCreateOperationAsync(db, payment);
    }

    [Fact]
    public async Task InitiateAsync_ConcurrentEquivalentRequests_CreatesOnePaymentAndOneBinding()
    {
        var now = new DateTimeOffset(
            2026,
            10,
            1,
            12,
            0,
            0,
            TimeSpan.Zero);

        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var clock = new FakeClock(now);

        var hook = new ControllableOperationHook(
            PaymentOperationCheckpoints.BeforeIdempotencyClaim,
            expectedParticipants: 2);

        await using var factory = CreateFactory(
            userId,
            clock,
            hook);

        await MigrateAsync(factory);

        await SeedPendingPaymentOrderAsync(
            factory,
            userId,
            orderId,
            now);

        var request = new InitiatePaymentRequest(
            OrderId: orderId,
            IdempotencyKey: idempotencyKey,
            Scenario: PaymentScenario.Success);

        var firstTask = ExecuteInitiationAsync(factory, request);
        var secondTask = ExecuteInitiationAsync(factory, request);

        try
        {
            await hook.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            hook.Release();
        }

        var handlingResults = await Task.WhenAll(
            firstTask,
            secondTask);

        Assert.Equal(2, hook.ReachedCount);
        Assert.All(
            handlingResults,
            handling => Assert.True(handling.IsSuccess));

        var results = handlingResults
            .Select(handling =>
                Assert.IsType<PaymentInitiationResult>(handling.Value))
            .ToArray();

        Assert.Single(results, result => !result.IsReplay);
        Assert.Single(results, result => result.IsReplay);
        Assert.Single(results.Select(result => result.PaymentId).Distinct());

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var payments = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == orderId)
            .ToListAsync();

        var payment = Assert.Single(payments);

        Assert.Equal(
            results[0].PaymentId,
            payment.Id);

        Assert.Equal(
            PaymentStatus.Succeeded,
            payment.Status);

        var idempotencyRows = await db.IdempotencyRequests
            .AsNoTracking()
            .Where(item =>
                item.UserId == userId &&
                item.Operation == IdempotencyOperation.InitiatePayment &&
                item.IdempotencyKey == idempotencyKey)
            .ToListAsync();

        var idempotencyRequest = Assert.Single(idempotencyRows);

        Assert.Equal(
            IdempotencyRequestStatus.Completed,
            idempotencyRequest.Status);

        Assert.Equal(
            payment.Id,
            idempotencyRequest.ResourceId);

        await AssertSingleSucceededCreateOperationAsync(db, payment);
    }

    [Fact]
    public async Task InitiateAsync_DifferentKeyForOrderWithPayment_ReturnsPaymentAlreadyExists()
    {
        var now = new DateTimeOffset(
            2026,
            10,
            1,
            12,
            0,
            0,
            TimeSpan.Zero);

        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var firstIdempotencyKey = Guid.NewGuid();
        var secondIdempotencyKey = Guid.NewGuid();
        var clock = new FakeClock(now);

        await using var factory = CreateFactory(userId, clock);
        await MigrateAsync(factory);

        await SeedPendingPaymentOrderAsync(
            factory,
            userId,
            orderId,
            now);

        var firstRequest = new InitiatePaymentRequest(
            OrderId: orderId,
            IdempotencyKey: firstIdempotencyKey,
            Scenario: PaymentScenario.Success);

        var secondRequest = new InitiatePaymentRequest(
            OrderId: orderId,
            IdempotencyKey: secondIdempotencyKey,
            Scenario: PaymentScenario.Success);

        ApplicationResult<PaymentInitiationResult> firstHandling;
        ApplicationResult<PaymentInitiationResult> secondHandling;

        using (var firstScope = factory.Services.CreateScope())
        {
            var service = firstScope.ServiceProvider
                .GetRequiredService<PaymentCommandService>();

            firstHandling = await service.InitiateAsync(
                firstRequest,
                CancellationToken.None);
        }

        using (var secondScope = factory.Services.CreateScope())
        {
            var service = secondScope.ServiceProvider
                .GetRequiredService<PaymentCommandService>();

            secondHandling = await service.InitiateAsync(
                secondRequest,
                CancellationToken.None);
        }

        Assert.True(firstHandling.IsSuccess);

        var firstResult = Assert.IsType<PaymentInitiationResult>(
            firstHandling.Value);

        Assert.False(secondHandling.IsSuccess);
        Assert.NotNull(secondHandling.Error);
        Assert.Equal(
            "PAYMENT_ALREADY_EXISTS",
            secondHandling.Error.Code);

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var payments = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == orderId)
            .ToListAsync();

        var payment = Assert.Single(payments);

        Assert.Equal(firstResult.PaymentId, payment.Id);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);

        var idempotencyRows = await db.IdempotencyRequests
            .AsNoTracking()
            .Where(item =>
                item.UserId == userId &&
                item.Operation == IdempotencyOperation.InitiatePayment &&
                (item.IdempotencyKey == firstIdempotencyKey ||
                 item.IdempotencyKey == secondIdempotencyKey))
            .ToListAsync();

        var originalBinding = Assert.Single(idempotencyRows);

        Assert.Equal(
            firstIdempotencyKey,
            originalBinding.IdempotencyKey);

        Assert.Equal(
            payment.Id,
            originalBinding.ResourceId);

        await AssertSingleSucceededCreateOperationAsync(db, payment);
    }

    [Fact]
    public async Task InitiateAsync_ConcurrentDifferentKeysForSameOrder_ReturnsPaymentAlreadyExistsForLoser()
    {
        var now = new DateTimeOffset(
            2026,
            10,
            1,
            12,
            0,
            0,
            TimeSpan.Zero);

        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var firstIdempotencyKey = Guid.NewGuid();
        var secondIdempotencyKey = Guid.NewGuid();
        var clock = new FakeClock(now);

        var hook = new ControllableOperationHook(
            PaymentOperationCheckpoints.BeforeIdempotencyClaim,
            expectedParticipants: 2);

        await using var factory = CreateFactory(
            userId,
            clock,
            hook);

        await MigrateAsync(factory);

        await SeedPendingPaymentOrderAsync(
            factory,
            userId,
            orderId,
            now);

        var firstRequest = new InitiatePaymentRequest(
            OrderId: orderId,
            IdempotencyKey: firstIdempotencyKey,
            Scenario: PaymentScenario.Success);

        var secondRequest = new InitiatePaymentRequest(
            OrderId: orderId,
            IdempotencyKey: secondIdempotencyKey,
            Scenario: PaymentScenario.Success);

        var firstTask = ExecuteInitiationAsync(
            factory,
            firstRequest);

        var secondTask = ExecuteInitiationAsync(
            factory,
            secondRequest);

        try
        {
            await hook.Reached.WaitAsync(
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            hook.Release();
        }

        var handlingResults = await Task.WhenAll(
            firstTask,
            secondTask);

        Assert.Equal(2, hook.ReachedCount);

        var attempts = new[]
        {
        new
        {
            IdempotencyKey = firstIdempotencyKey,
            Handling = handlingResults[0]
        },
        new
        {
            IdempotencyKey = secondIdempotencyKey,
            Handling = handlingResults[1]
        }
    };

        var winningAttempt = Assert.Single(
            attempts,
            attempt => attempt.Handling.IsSuccess);

        var losingAttempt = Assert.Single(
            attempts,
            attempt => !attempt.Handling.IsSuccess);

        var successfulResult =
            Assert.IsType<PaymentInitiationResult>(
                winningAttempt.Handling.Value);

        Assert.False(successfulResult.IsReplay);

        Assert.NotNull(losingAttempt.Handling.Error);
        Assert.Equal(
            "PAYMENT_ALREADY_EXISTS",
            losingAttempt.Handling.Error.Code);

        using var assertionScope =
            factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var payments = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == orderId)
            .ToListAsync();

        var payment = Assert.Single(payments);

        Assert.Equal(
            successfulResult.PaymentId,
            payment.Id);

        Assert.Equal(
            PaymentStatus.Succeeded,
            payment.Status);

        var idempotencyRows = await db.IdempotencyRequests
            .AsNoTracking()
            .Where(item =>
                item.UserId == userId &&
                item.Operation ==
                    IdempotencyOperation.InitiatePayment &&
                (item.IdempotencyKey == firstIdempotencyKey ||
                 item.IdempotencyKey == secondIdempotencyKey))
            .ToListAsync();

        var completedBinding =
            Assert.Single(idempotencyRows);

        Assert.Equal(
            winningAttempt.IdempotencyKey,
            completedBinding.IdempotencyKey);

        Assert.Equal(
            IdempotencyRequestStatus.Completed,
            completedBinding.Status);

        Assert.Equal(
            payment.Id,
            completedBinding.ResourceId);

        await AssertSingleSucceededCreateOperationAsync(db, payment);
    }

    [Fact]
    public async Task InitiateAsync_KeyAlreadyUsedForCreateOrder_CreatesPaymentInIndependentOperationNamespace()
    {
        var now = new DateTimeOffset(
            2026,
            10,
            2,
            12,
            0,
            0,
            TimeSpan.Zero);

        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var sharedIdempotencyKey = Guid.NewGuid();
        var clock = new FakeClock(now);

        await using var factory = CreateFactory(
            userId,
            clock);

        await MigrateAsync(factory);

        await SeedPendingPaymentOrderAsync(
            factory,
            userId,
            orderId,
            now);

        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var createOrderRequest =
                new IdempotencyRequest(
                    id: Guid.NewGuid(),
                    userId: userId,
                    operation: IdempotencyOperation.CreateOrder,
                    idempotencyKey: sharedIdempotencyKey,
                    requestHash: Enumerable
                        .Range(
                            0,
                            IdempotencyRequest.RequestHashLength)
                        .Select(value => (byte)value)
                        .ToArray(),
                    createdAt: now.AddMinutes(-1),
                    expiresAt: now.AddHours(24),
                    deleteAfter: now.AddHours(72));

            createOrderRequest.Complete(
                resourceId: orderId,
                httpStatusCode:
                    IdempotencyRequest
                        .CreateOrderCompletedStatusCode,
                responseBodyJson: "{}",
                completedAt: now);

            db.IdempotencyRequests.Add(
                createOrderRequest);

            await db.SaveChangesAsync();
        }

        var paymentRequest =
            new InitiatePaymentRequest(
                OrderId: orderId,
                IdempotencyKey: sharedIdempotencyKey,
                Scenario: PaymentScenario.Success);

        var handling = await ExecuteInitiationAsync(
            factory,
            paymentRequest);

        Assert.True(handling.IsSuccess);

        var result =
            Assert.IsType<PaymentInitiationResult>(
                handling.Value);

        Assert.False(result.IsReplay);

        using var assertionScope =
            factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var matchingRequests =
            await assertionDb.IdempotencyRequests
                .AsNoTracking()
                .Where(request =>
                    request.UserId == userId &&
                    request.IdempotencyKey ==
                        sharedIdempotencyKey)
                .OrderBy(request => request.Operation)
                .ToListAsync();

        Assert.Equal(2, matchingRequests.Count);

        var createOrderBinding = Assert.Single(
            matchingRequests,
            request =>
                request.Operation ==
                IdempotencyOperation.CreateOrder);

        var paymentBinding = Assert.Single(
            matchingRequests,
            request =>
                request.Operation ==
                IdempotencyOperation.InitiatePayment);

        Assert.Equal(
            orderId,
            createOrderBinding.ResourceId);

        Assert.Equal(
            IdempotencyRequestStatus.Completed,
            createOrderBinding.Status);

        Assert.Equal(
            result.PaymentId,
            paymentBinding.ResourceId);

        Assert.Equal(
            IdempotencyRequestStatus.Completed,
            paymentBinding.Status);

        var payment = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(
                payment => payment.Id == result.PaymentId);

        Assert.Equal(orderId, payment.OrderId);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);

        await AssertSingleSucceededCreateOperationAsync(assertionDb, payment);
    }

    [Fact]
    [Trait("Requirement", "PAY-TXN-001")]
    public async Task InitiateAsync_BlockedGateway_ExposesCommittedLocalIntent()
    {
        var now = new DateTimeOffset(
            2026,
            10,
            2,
            12,
            0,
            0,
            TimeSpan.Zero);

        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var clock = new FakeClock(now);
        var gateway = new BlockingPaymentGateway();

        await using var factory = CreateFactory(
            userId,
            clock,
            paymentGateway: gateway);

        await MigrateAsync(factory);

        await SeedPendingPaymentOrderAsync(
            factory,
            userId,
            orderId,
            now);

        var request = new InitiatePaymentRequest(
            OrderId: orderId,
            IdempotencyKey: idempotencyKey,
            Scenario: PaymentScenario.Success);

        var initiationTask = ExecuteInitiationAsync(
            factory,
            request);

        try
        {
            await gateway.Reached.WaitAsync(
                TimeSpan.FromSeconds(10));

            using var assertionScope =
                factory.Services.CreateScope();

            var db = assertionScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var payment = await db.Payments
                .AsNoTracking()
                .SingleAsync(
                    payment => payment.OrderId == orderId);

            var idempotencyRequest =
                await db.IdempotencyRequests
                    .AsNoTracking()
                    .SingleAsync(item =>
                        item.UserId == userId &&
                        item.Operation ==
                            IdempotencyOperation.InitiatePayment &&
                        item.IdempotencyKey ==
                            idempotencyKey);

            Assert.Equal(
                IdempotencyRequestStatus.Completed,
                idempotencyRequest.Status);

            Assert.Equal(
                payment.Id,
                idempotencyRequest.ResourceId);

            var gatewayRequest =
                Assert.IsType<CreatePaymentRequest>(
                    gateway.Request);

            Assert.Equal(
                payment.Id,
                gatewayRequest.PaymentId);

            Assert.Equal(
                payment.GatewayIdempotencyKey,
                gatewayRequest.IdempotencyKey);

            Assert.Equal(
                payment.ProviderPaymentId,
                gatewayRequest.ProviderPaymentId);

            Assert.Equal(
                payment.Amount,
                gatewayRequest.Amount);

            Assert.Equal(
                PaymentScenario.Success,
                gatewayRequest.Scenario);
        }
        finally
        {
            gateway.Release();
        }

        var handling = await initiationTask;

        Assert.True(handling.IsSuccess);

        var result =
            Assert.IsType<PaymentInitiationResult>(
                handling.Value);

        Assert.Equal(orderId, request.OrderId);

        using var resultScope =
            factory.Services.CreateScope();

        var resultDb = resultScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var persistedPayment =
            await resultDb.Payments
                .AsNoTracking()
                .SingleAsync(
                    payment =>
                        payment.Id == result.PaymentId);

        Assert.Equal(
            PaymentStatus.Succeeded,
            persistedPayment.Status);
    }

    [Fact]
    public async Task InitiateAsync_ProviderResponseLostAfterCommit_ReturnsUnresolvedPayment()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var clock = new FakeClock(now);

        await using var factory = CreateFactory(userId, clock);

        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, now);

        var request = new InitiatePaymentRequest(
            OrderId: orderId,
            IdempotencyKey: idempotencyKey,
            Scenario: PaymentScenario.SuccessButResponseLost);

        var handling = await ExecuteInitiationAsync(factory, request);

        Assert.True(handling.IsSuccess);

        var result = Assert.IsType<PaymentInitiationResult>(handling.Value);

        Assert.False(result.IsReplay);
        Assert.Equal(PaymentStatus.Pending, result.Status);

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var payment = await db.Payments
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == result.PaymentId);

        Assert.Equal(orderId, payment.OrderId);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.FailureCode);

        var order = await db.Orders
            .AsNoTracking()
            .SingleAsync(order => order.Id == orderId);

        Assert.Equal(OrderStatus.PendingPayment, order.Status);

        var idempotencyRequest = await db.IdempotencyRequests
            .AsNoTracking()
            .SingleAsync(item =>
                item.UserId == userId &&
                item.Operation == IdempotencyOperation.InitiatePayment &&
                item.IdempotencyKey == idempotencyKey);

        Assert.Equal(IdempotencyRequestStatus.Completed, idempotencyRequest.Status);
        Assert.Equal(payment.Id, idempotencyRequest.ResourceId);

        var providerOperations = await db.FakeProviderOperations
            .AsNoTracking()
            .Where(operation =>
                operation.OperationType == FakeProviderOperationType.CreatePayment &&
                operation.IdempotencyKey == payment.GatewayIdempotencyKey)
            .ToListAsync();

        var providerOperation = Assert.Single(providerOperations);

        Assert.Equal(payment.ProviderPaymentId, providerOperation.ProviderResourceId);
        Assert.Equal(PaymentScenario.SuccessButResponseLost, providerOperation.Scenario);
        Assert.Equal(FakeProviderOperationStatus.Succeeded, providerOperation.Status);
        Assert.Equal(payment.Amount, providerOperation.Amount);
    }

    [Fact]
    public async Task InitiateAsync_CrashAfterProviderCommit_PreservesUnresolvedPaymentAndProviderSuccess()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var clock = new FakeClock(now);
        var hook = new ThrowingCheckpointHook(PaymentOperationCheckpoints.AfterGatewayCreate);

        await using var factory = CreateFactory(userId, clock, operationHook: hook);

        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, now);

        var request = new InitiatePaymentRequest(
            OrderId: orderId,
            IdempotencyKey: idempotencyKey,
            Scenario: PaymentScenario.Success);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExecuteInitiationAsync(factory, request));

        Assert.Equal(
            $"Injected failure at '{PaymentOperationCheckpoints.AfterGatewayCreate}'",
            exception.Message);

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var payment = await db.Payments
            .AsNoTracking()
            .SingleAsync(payment => payment.OrderId == orderId);

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.FailureCode);

        var order = await db.Orders
            .AsNoTracking()
            .SingleAsync(order => order.Id == orderId);

        Assert.Equal(OrderStatus.PendingPayment, order.Status);

        var idempotencyRequest = await db.IdempotencyRequests
            .AsNoTracking()
            .SingleAsync(item =>
                item.UserId == userId &&
                item.Operation == IdempotencyOperation.InitiatePayment &&
                item.IdempotencyKey == idempotencyKey);

        Assert.Equal(IdempotencyRequestStatus.Completed, idempotencyRequest.Status);
        Assert.Equal(payment.Id, idempotencyRequest.ResourceId);

        var providerOperations = await db.FakeProviderOperations
            .AsNoTracking()
            .Where(operation =>
                operation.OperationType == FakeProviderOperationType.CreatePayment &&
                operation.IdempotencyKey == payment.GatewayIdempotencyKey)
            .ToListAsync();

        var providerOperation = Assert.Single(providerOperations);

        Assert.Equal(payment.ProviderPaymentId, providerOperation.ProviderResourceId);
        Assert.Equal(PaymentScenario.Success, providerOperation.Scenario);
        Assert.Equal(FakeProviderOperationStatus.Succeeded, providerOperation.Status);
        Assert.Equal(payment.Amount, providerOperation.Amount);
    }

    [Fact]
    public async Task InitiateAsync_ProviderResponseLostThenSameKeyRetry_ReturnsSamePaymentWithoutSecondProviderOperation()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var clock = new FakeClock(now);

        await using var factory = CreateFactory(userId, clock);

        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, now);

        var request = new InitiatePaymentRequest(
            OrderId: orderId,
            IdempotencyKey: idempotencyKey,
            Scenario: PaymentScenario.SuccessButResponseLost);

        var firstHandling = await ExecuteInitiationAsync(factory, request);
        var retryHandling = await ExecuteInitiationAsync(factory, request);

        Assert.True(firstHandling.IsSuccess);
        Assert.True(retryHandling.IsSuccess);

        var firstResult = Assert.IsType<PaymentInitiationResult>(firstHandling.Value);
        var retryResult = Assert.IsType<PaymentInitiationResult>(retryHandling.Value);

        Assert.False(firstResult.IsReplay);
        Assert.True(retryResult.IsReplay);
        Assert.Equal(firstResult.PaymentId, retryResult.PaymentId);
        Assert.Equal(PaymentStatus.Pending, firstResult.Status);
        Assert.Equal(PaymentStatus.Pending, retryResult.Status);

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var payments = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == orderId)
            .ToListAsync();

        var payment = Assert.Single(payments);

        Assert.Equal(firstResult.PaymentId, payment.Id);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.FailureCode);

        var idempotencyRows = await db.IdempotencyRequests
            .AsNoTracking()
            .Where(item =>
                item.UserId == userId &&
                item.Operation == IdempotencyOperation.InitiatePayment &&
                item.IdempotencyKey == idempotencyKey)
            .ToListAsync();

        var idempotencyRequest = Assert.Single(idempotencyRows);

        Assert.Equal(IdempotencyRequestStatus.Completed, idempotencyRequest.Status);
        Assert.Equal(payment.Id, idempotencyRequest.ResourceId);

        var providerOperations = await db.FakeProviderOperations
            .AsNoTracking()
            .Where(operation =>
                operation.OperationType == FakeProviderOperationType.CreatePayment &&
                operation.IdempotencyKey == payment.GatewayIdempotencyKey)
            .ToListAsync();

        var providerOperation = Assert.Single(providerOperations);

        Assert.Equal(payment.ProviderPaymentId, providerOperation.ProviderResourceId);
        Assert.Equal(PaymentScenario.SuccessButResponseLost, providerOperation.Scenario);
        Assert.Equal(FakeProviderOperationStatus.Succeeded, providerOperation.Status);
        Assert.Equal(payment.Amount, providerOperation.Amount);
    }

    [Fact]
    public async Task InitiateAsync_GatewayTimeout_ReturnsUnresolvedPaymentWithoutMarkingFailure()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var clock = new FakeClock(now);
        var gateway = new TimeoutPaymentGateway();

        await using var factory = CreateFactory(userId, clock, paymentGateway: gateway);

        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, now);

        var request = new InitiatePaymentRequest(
            OrderId: orderId,
            IdempotencyKey: idempotencyKey,
            Scenario: PaymentScenario.Success);

        var handling = await ExecuteInitiationAsync(factory, request);

        Assert.True(handling.IsSuccess);

        var result = Assert.IsType<PaymentInitiationResult>(handling.Value);

        Assert.False(result.IsReplay);
        Assert.Equal(PaymentStatus.Pending, result.Status);
        Assert.Equal(1, gateway.CallCount);

        var gatewayRequest = Assert.IsType<CreatePaymentRequest>(gateway.Request);

        Assert.Equal(result.PaymentId, gatewayRequest.PaymentId);
        Assert.Equal(PaymentScenario.Success, gatewayRequest.Scenario);

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var payment = await db.Payments
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == result.PaymentId);

        Assert.Equal(orderId, payment.OrderId);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.FailureCode);
        Assert.Equal(payment.GatewayIdempotencyKey, gatewayRequest.IdempotencyKey);
        Assert.Equal(payment.ProviderPaymentId, gatewayRequest.ProviderPaymentId);
        Assert.Equal(payment.Amount, gatewayRequest.Amount);

        var order = await db.Orders
            .AsNoTracking()
            .SingleAsync(order => order.Id == orderId);

        Assert.Equal(OrderStatus.PendingPayment, order.Status);

        var idempotencyRequest = await db.IdempotencyRequests
            .AsNoTracking()
            .SingleAsync(item =>
                item.UserId == userId &&
                item.Operation == IdempotencyOperation.InitiatePayment &&
                item.IdempotencyKey == idempotencyKey);

        Assert.Equal(IdempotencyRequestStatus.Completed, idempotencyRequest.Status);
        Assert.Equal(payment.Id, idempotencyRequest.ResourceId);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-001")]
    public async Task ApplyAsync_AuthoritativeSuccess_MarksPaymentSucceededAndConfirmsOrder()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var clock = new FakeClock(now);
        var gateway = new TimeoutPaymentGateway();

        await using var factory = CreateFactory(userId, clock, paymentGateway: gateway);
        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, now);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(orderId, Guid.NewGuid(), PaymentScenario.Success));

        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);

        string providerPaymentId;

        using (var paymentScope = factory.Services.CreateScope())
        {
            var db = paymentScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            var payment = await db.Payments.SingleAsync(item => item.Id == initiationResult.PaymentId);
            var order = await db.Orders.SingleAsync(item => item.Id == orderId);

            Assert.Equal(PaymentStatus.Pending, payment.Status);
            Assert.Equal(OrderStatus.PendingPayment, order.Status);

            providerPaymentId = payment.ProviderPaymentId;

            providerPaymentId = payment.ProviderPaymentId;
        }

        using (var applicationScope = factory.Services.CreateScope())
        {
            var service = applicationScope.ServiceProvider.GetRequiredService<PaymentResultApplicationService>();

            await service.ApplyAsync(
                new ApplyPaymentResultCommand(
                    providerPaymentId,
                    ProviderPaymentOutcome.Succeeded,
                    null,
                    PaymentResultSource.Reconciliation,
                    providerEvent: null,
                    now.AddMinutes(-1)),
                CancellationToken.None);
        }

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var persistedPayment = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == initiationResult.PaymentId);
        var persistedOrder = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);

        Assert.Equal(PaymentStatus.Succeeded, persistedPayment.Status);
        Assert.Equal(OrderStatus.Confirmed, persistedOrder.Status);
    }

    [Fact]
    public async Task GetPaymentForUpdateAsync_WithoutActiveTransaction_ThrowsInvalidOperationException()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();

        await using var factory = CreateFactory(userId, new FakeClock(now));
        using var scope = factory.Services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IPaymentResultApplicationStore>();

        var exception = async () =>
        {
            _ = await store.GetPaymentForUpdateAsync(
                "fake-pay-without-transaction",
                CancellationToken.None);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(exception);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-002")]
    public async Task ApplyAsync_AuthoritativeFailure_FailsPaymentExpiresOrderAndReleasesReservation()
    {
        var now = new DateTimeOffset(2026, 10, 2, 13, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var clock = new FakeClock(now);
        var gateway = new TimeoutPaymentGateway();

        await using var factory = CreateFactory(userId, clock, paymentGateway: gateway);
        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, now);

        var productVariantId = await SeedReservationForOrderAsync(factory, orderId, now);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(orderId, Guid.NewGuid(), PaymentScenario.Failed));
        Assert.True(initiation.IsSuccess);

        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);

        string providerPaymentId;

        using (var paymentScope = factory.Services.CreateScope())
        {
            var db = paymentScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            var payment = await db.Payments
                .AsNoTracking()
                .SingleAsync(item => item.Id == initiationResult.PaymentId);
            var order = await db.Orders
                .AsNoTracking()
                .SingleAsync(item => item.Id == orderId);
            var inventoryFirst = await db.Inventories
                .AsNoTracking()
                .SingleAsync(item => item.ProductVariantId == productVariantId);

            Assert.Equal(PaymentStatus.Pending, payment.Status);
            Assert.Null(payment.FailureCode);
            Assert.Equal(OrderStatus.PendingPayment, order.Status);
            Assert.Equal(10, inventoryFirst.OnHandQuantity);
            Assert.Equal(2, inventoryFirst.ReservedQuantity);

            providerPaymentId = payment.ProviderPaymentId;
        }

        using (var applicationScope = factory.Services.CreateScope())
        {
            var service = applicationScope.ServiceProvider.GetRequiredService<PaymentResultApplicationService>();

            await service.ApplyAsync(
                new ApplyPaymentResultCommand(
                    providerPaymentId,
                    ProviderPaymentOutcome.Failed,
                    "DECLINED",
                    PaymentResultSource.Reconciliation,
                    providerEvent: null,
                    now.AddMinutes(-1)),
                CancellationToken.None);
        }

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var persistedPayment = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == initiationResult.PaymentId);
        var persistedOrder = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);
        var inventory = await assertionDb.Inventories
            .AsNoTracking()
            .SingleAsync(item => item.ProductVariantId == productVariantId);
        var transactions = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item =>
                item.ReferenceType == InventoryReferenceType.Order &&
                item.ReferenceId == orderId)
            .ToArrayAsync();
        var histories = await assertionDb.OrderStatusHistories
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .ToArrayAsync();

        Assert.Equal(PaymentStatus.Failed, persistedPayment.Status);
        Assert.Equal("DECLINED", persistedPayment.FailureCode);
        Assert.Equal(OrderStatus.Expired, persistedOrder.Status);
        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);

        Assert.Single(transactions, item => item.Type == InventoryTransactionType.Reserve);

        var release = Assert.Single(
            transactions,
            item => item.Type == InventoryTransactionType.Release);

        Assert.Equal(0, release.OnHandQuantityDelta);
        Assert.Equal(-2, release.ReservedQuantityDelta);

        var history = Assert.Single(histories);

        Assert.Equal(OrderStatus.PendingPayment, history.FromStatus);
        Assert.Equal(OrderStatus.Expired, history.ToStatus);
        Assert.Equal(OrderStatusHistoryActorType.System, history.ActorType);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-001")]
    public async Task InitiateAsync_AuthoritativeSuccess_AppliesResultBeforeReturning()
    {
        var now = new DateTimeOffset(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var factory = CreateFactory(userId, new FakeClock(now));
        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, now);

        var handling = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(
                orderId,
                Guid.NewGuid(),
                PaymentScenario.Success));

        Assert.True(handling.IsSuccess);

        var result = Assert.IsType<PaymentInitiationResult>(handling.Value);

        Assert.Equal(PaymentStatus.Succeeded, result.Status);

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var payment = await db.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == result.PaymentId);
        var order = await db.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-002")]
    public async Task InitiateAsync_AuthoritativeFailure_AppliesResultBeforeReturning()
    {
        var now = new DateTimeOffset(2026, 10, 2, 15, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var factory = CreateFactory(userId, new FakeClock(now));
        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, now);

        var productVariantId = await SeedReservationForOrderAsync(factory, orderId, now);

        var handling = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(orderId, Guid.NewGuid(), PaymentScenario.Failed));

        Assert.True(handling.IsSuccess);

        var result = Assert.IsType<PaymentInitiationResult>(handling.Value);

        Assert.Equal(PaymentStatus.Failed, result.Status);

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var payment = await db.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == result.PaymentId);
        var order = await db.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);
        var inventory = await db.Inventories
            .AsNoTracking()
            .SingleAsync(item => item.ProductVariantId == productVariantId);

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("DECLINED", payment.FailureCode);
        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-002")]
    public async Task InitiateAsync_AuthoritativeFailure_WithMultipleItems_ReleasesAllReservationsOnce()
    {
        var now = new DateTimeOffset(2026, 10, 2, 16, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var factory = CreateFactory(userId, new FakeClock(now));
        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, now);

        var firstProductVariantId = await SeedReservationForOrderAsync(factory, orderId, now, quantity: 1);
        var secondProductVariantId = await SeedReservationForOrderAsync(factory, orderId, now, quantity: 1);
        var productVariantIds = new[] { firstProductVariantId, secondProductVariantId };

        var handling = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(orderId, Guid.NewGuid(), PaymentScenario.Failed));

        Assert.True(handling.IsSuccess);

        var result = Assert.IsType<PaymentInitiationResult>(handling.Value);

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var payment = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == result.PaymentId);
        var order = await db.Orders.AsNoTracking().SingleAsync(item => item.Id == orderId);
        var inventories = await db.Inventories
            .AsNoTracking()
            .Where(item => productVariantIds.Contains(item.ProductVariantId))
            .ToArrayAsync();
        var releases = await db.InventoryTransactions
            .AsNoTracking()
            .Where(item =>
                item.ReferenceType == InventoryReferenceType.Order &&
                item.ReferenceId == orderId &&
                item.Type == InventoryTransactionType.Release)
            .ToArrayAsync();
        var histories = await db.OrderStatusHistories
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .ToArrayAsync();

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal(2, inventories.Length);
        Assert.All(inventories, inventory =>
        {
            Assert.Equal(10, inventory.OnHandQuantity);
            Assert.Equal(0, inventory.ReservedQuantity);
        });
        Assert.Equal(2, releases.Length);
        Assert.Single(histories);
    }

    [Fact]
    [Trait("Requirement", "PAY-WEB-003")]
    public async Task ApplyAsync_FailureAfterSuccess_DoesNotRegressOrReleaseReservation()
    {
        var now = new DateTimeOffset(2026, 10, 2, 17, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var factory = CreateFactory(userId, new FakeClock(now));
        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, now);

        var productVariantId = await SeedReservationForOrderAsync(factory, orderId, now);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(orderId, Guid.NewGuid(), PaymentScenario.Success));

        Assert.True(initiation.IsSuccess);

        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);

        Assert.Equal(PaymentStatus.Succeeded, initiationResult.Status);

        string providerPaymentId;

        using (var paymentScope = factory.Services.CreateScope())
        {
            var db = paymentScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            var payment = await db.Payments
                .AsNoTracking()
                .SingleAsync(item => item.Id == initiationResult.PaymentId);

            providerPaymentId = payment.ProviderPaymentId;
        }

        using (var applicationScope = factory.Services.CreateScope())
        {
            var service = applicationScope.ServiceProvider.GetRequiredService<PaymentResultApplicationService>();

            await service.ApplyAsync(
                new ApplyPaymentResultCommand(
                    providerPaymentId,
                    ProviderPaymentOutcome.Failed,
                    "DECLINED",
                    PaymentResultSource.Webhook,
                    new ProviderPaymentEventData(
                        "Fake",
                        "fake-event-failure-after-success-001",
                        "payment.failed",
                        new string('c', 64)),
                    now.AddMinutes(1)),
                CancellationToken.None);
        }

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var persistedPayment = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == initiationResult.PaymentId);
        var persistedOrder = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);
        var inventory = await assertionDb.Inventories
            .AsNoTracking()
            .SingleAsync(item => item.ProductVariantId == productVariantId);
        var releases = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item =>
                item.ReferenceType == InventoryReferenceType.Order &&
                item.ReferenceId == orderId &&
                item.Type == InventoryTransactionType.Release)
            .ToArrayAsync();
        var histories = await assertionDb.OrderStatusHistories
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .ToArrayAsync();

        Assert.Equal(PaymentStatus.Succeeded, persistedPayment.Status);
        Assert.Null(persistedPayment.FailureCode);
        Assert.Equal(OrderStatus.Confirmed, persistedOrder.Status);
        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(2, inventory.ReservedQuantity);
        Assert.Empty(releases);

        var history = Assert.Single(histories);

        Assert.Equal(OrderStatus.PendingPayment, history.FromStatus);
        Assert.Equal(OrderStatus.Confirmed, history.ToStatus);
    }

    [Fact]
    [Trait("Requirement", "PAY-WEB-001")]
    public async Task ApplyAsync_SameWebhookEventTwice_PersistsOneReceiptAndAppliesOnce()
    {
        var now = new DateTimeOffset(2026, 10, 2, 19, 0, 0, TimeSpan.Zero);
        var occurredAt = now.AddMinutes(-1);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var gateway = new TimeoutPaymentGateway();

        await using var factory = CreateFactory(
            userId,
            new FakeClock(now),
            paymentGateway: gateway);

        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, now);

        var productVariantId = await SeedReservationForOrderAsync(factory, orderId, now);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(orderId, Guid.NewGuid(), PaymentScenario.Success));

        Assert.True(initiation.IsSuccess);

        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);

        Assert.Equal(PaymentStatus.Pending, initiationResult.Status);

        string providerPaymentId;

        using (var paymentScope = factory.Services.CreateScope())
        {
            var db = paymentScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            var payment = await db.Payments
                .AsNoTracking()
                .SingleAsync(item => item.Id == initiationResult.PaymentId);

            providerPaymentId = payment.ProviderPaymentId;
        }

        var providerEvent = new ProviderPaymentEventData(
            "Fake",
            "fake-event-duplicate-success-001",
            "payment.succeeded",
            new string('d', 64));

        var command = new ApplyPaymentResultCommand(
            providerPaymentId,
            ProviderPaymentOutcome.Succeeded,
            failureCode: null,
            PaymentResultSource.Webhook,
            providerEvent,
            occurredAt);

        using (var firstDeliveryScope = factory.Services.CreateScope())
        {
            var service = firstDeliveryScope.ServiceProvider.GetRequiredService<PaymentResultApplicationService>();

            await service.ApplyAsync(command, CancellationToken.None);
        }

        using (var duplicateDeliveryScope = factory.Services.CreateScope())
        {
            var service = duplicateDeliveryScope.ServiceProvider.GetRequiredService<PaymentResultApplicationService>();

            await service.ApplyAsync(command, CancellationToken.None);
        }

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var paymentAfterDelivery = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == initiationResult.PaymentId);
        var orderAfterDelivery = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);
        var inventory = await assertionDb.Inventories
            .AsNoTracking()
            .SingleAsync(item => item.ProductVariantId == productVariantId);
        var receipts = await assertionDb.ProviderPaymentEvents
            .AsNoTracking()
            .Where(item =>
                item.Provider == "Fake" &&
                item.ProviderEventId == "fake-event-duplicate-success-001")
            .ToArrayAsync();
        var releases = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item =>
                item.ReferenceType == InventoryReferenceType.Order &&
                item.ReferenceId == orderId &&
                item.Type == InventoryTransactionType.Release)
            .ToArrayAsync();
        var histories = await assertionDb.OrderStatusHistories
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .ToArrayAsync();

        var receipt = Assert.Single(receipts);

        Assert.Equal(initiationResult.PaymentId, receipt.PaymentId);
        Assert.Equal(providerPaymentId, receipt.ProviderPaymentId);
        Assert.Equal("payment.succeeded", receipt.EventType);
        Assert.Equal(new string('d', 64), receipt.PayloadHash);
        Assert.Equal(occurredAt, receipt.OccurredAt);
        Assert.Equal(now, receipt.ReceivedAt);
        Assert.Equal(now, receipt.ProcessedAt);

        Assert.Equal(PaymentStatus.Succeeded, paymentAfterDelivery.Status);
        Assert.Equal(OrderStatus.Confirmed, orderAfterDelivery.Status);
        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(2, inventory.ReservedQuantity);
        Assert.Empty(releases);

        var history = Assert.Single(histories);

        Assert.Equal(OrderStatus.PendingPayment, history.FromStatus);
        Assert.Equal(OrderStatus.Confirmed, history.ToStatus);
    }

    [Fact]
    [Trait("Requirement", "PAY-WEB-001")]
    public async Task ApplyAsync_SameWebhookIdentityWithDifferentHash_ReturnsEventConflict()
    {
        var now = new DateTimeOffset(2026, 10, 2, 20, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var gateway = new TimeoutPaymentGateway();

        await using var factory = CreateFactory(
            userId,
            new FakeClock(now),
            paymentGateway: gateway);

        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, now);

        var productVariantId = await SeedReservationForOrderAsync(factory, orderId, now);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(orderId, Guid.NewGuid(), PaymentScenario.Success));

        Assert.True(initiation.IsSuccess);

        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);

        Assert.Equal(PaymentStatus.Pending, initiationResult.Status);

        string providerPaymentId;

        using (var paymentScope = factory.Services.CreateScope())
        {
            var db = paymentScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            var payment = await db.Payments
                .AsNoTracking()
                .SingleAsync(item => item.Id == initiationResult.PaymentId);

            providerPaymentId = payment.ProviderPaymentId;
        }

        const string providerEventId = "fake-event-conflict-001";
        var originalHash = new string('a', 64);
        var conflictingHash = new string('b', 64);

        var originalCommand = new ApplyPaymentResultCommand(
            providerPaymentId,
            ProviderPaymentOutcome.Succeeded,
            failureCode: null,
            PaymentResultSource.Webhook,
            new ProviderPaymentEventData(
                "Fake",
                providerEventId,
                "payment.succeeded",
                originalHash),
            now.AddMinutes(-2));

        var conflictingCommand = new ApplyPaymentResultCommand(
            providerPaymentId,
            ProviderPaymentOutcome.Failed,
            "DECLINED",
            PaymentResultSource.Webhook,
            new ProviderPaymentEventData(
                "Fake",
                providerEventId,
                "payment.failed",
                conflictingHash),
            now.AddMinutes(-1));

        PaymentResultApplicationOutcome originalOutcome;

        using (var originalScope = factory.Services.CreateScope())
        {
            var service = originalScope.ServiceProvider.GetRequiredService<PaymentResultApplicationService>();

            originalOutcome = await service.ApplyAsync(
                originalCommand,
                CancellationToken.None);
        }

        PaymentResultApplicationOutcome conflictingOutcome;

        using (var conflictingScope = factory.Services.CreateScope())
        {
            var service = conflictingScope.ServiceProvider.GetRequiredService<PaymentResultApplicationService>();

            conflictingOutcome = await service.ApplyAsync(
                conflictingCommand,
                CancellationToken.None);
        }

        Assert.Equal(PaymentResultApplicationStatus.Accepted, originalOutcome.Status);
        Assert.Equal(PaymentResultApplicationStatus.EventConflict, conflictingOutcome.Status);

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var paymentAfterConflict = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == initiationResult.PaymentId);
        var orderAfterConflict = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);
        var inventory = await assertionDb.Inventories
            .AsNoTracking()
            .SingleAsync(item => item.ProductVariantId == productVariantId);
        var receipts = await assertionDb.ProviderPaymentEvents
            .AsNoTracking()
            .Where(item =>
                item.Provider == "Fake" &&
                item.ProviderEventId == providerEventId)
            .ToArrayAsync();
        var releases = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item =>
                item.ReferenceType == InventoryReferenceType.Order &&
                item.ReferenceId == orderId &&
                item.Type == InventoryTransactionType.Release)
            .ToArrayAsync();
        var histories = await assertionDb.OrderStatusHistories
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .ToArrayAsync();

        var receipt = Assert.Single(receipts);

        Assert.Equal(originalHash, receipt.PayloadHash);
        Assert.Equal("payment.succeeded", receipt.EventType);
        Assert.Equal(PaymentStatus.Succeeded, paymentAfterConflict.Status);
        Assert.Null(paymentAfterConflict.FailureCode);
        Assert.Equal(OrderStatus.Confirmed, orderAfterConflict.Status);
        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(2, inventory.ReservedQuantity);
        Assert.Empty(releases);
        Assert.Single(histories);
    }

    [Fact]
    [Trait("Requirement", "PAY-WEB-002")]
    public async Task ApplyAsync_DifferentEventIdsWithSameSuccess_PersistsBothReceiptsAndAppliesOnce()
    {
        var now = new DateTimeOffset(2026, 10, 2, 21, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var gateway = new TimeoutPaymentGateway();

        await using var factory = CreateFactory(
            userId,
            new FakeClock(now),
            paymentGateway: gateway);

        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, now);

        var productVariantId = await SeedReservationForOrderAsync(factory, orderId, now);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(orderId, Guid.NewGuid(), PaymentScenario.Success));

        Assert.True(initiation.IsSuccess);

        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);

        Assert.Equal(PaymentStatus.Pending, initiationResult.Status);

        string providerPaymentId;

        using (var paymentScope = factory.Services.CreateScope())
        {
            var db = paymentScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            var payment = await db.Payments
                .AsNoTracking()
                .SingleAsync(item => item.Id == initiationResult.PaymentId);

            providerPaymentId = payment.ProviderPaymentId;
        }

        const string firstProviderEventId = "fake-event-same-success-001";
        const string secondProviderEventId = "fake-event-same-success-002";
        var payloadHash = new string('e', 64);

        var commands = new[]
        {
        new ApplyPaymentResultCommand(
            providerPaymentId,
            ProviderPaymentOutcome.Succeeded,
            failureCode: null,
            PaymentResultSource.Webhook,
            new ProviderPaymentEventData(
                "Fake",
                firstProviderEventId,
                "payment.succeeded",
                payloadHash),
            now.AddMinutes(-2)),
        new ApplyPaymentResultCommand(
            providerPaymentId,
            ProviderPaymentOutcome.Succeeded,
            failureCode: null,
            PaymentResultSource.Webhook,
            new ProviderPaymentEventData(
                "Fake",
                secondProviderEventId,
                "payment.succeeded",
                payloadHash),
            now.AddMinutes(-1))
    };

        var outcomes = new List<PaymentResultApplicationOutcome>(commands.Length);

        foreach (var command in commands)
        {
            using var applicationScope = factory.Services.CreateScope();

            var service = applicationScope.ServiceProvider
                .GetRequiredService<PaymentResultApplicationService>();

            outcomes.Add(await service.ApplyAsync(command, CancellationToken.None));
        }

        Assert.All(
            outcomes,
            outcome => Assert.Equal(
                PaymentResultApplicationStatus.Accepted,
                outcome.Status));

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var persistedPayment = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == initiationResult.PaymentId);
        var persistedOrder = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);
        var inventory = await assertionDb.Inventories
            .AsNoTracking()
            .SingleAsync(item => item.ProductVariantId == productVariantId);
        var receipts = await assertionDb.ProviderPaymentEvents
            .AsNoTracking()
            .Where(item =>
                item.Provider == "Fake" &&
                (item.ProviderEventId == firstProviderEventId ||
                 item.ProviderEventId == secondProviderEventId))
            .ToArrayAsync();
        var releases = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item =>
                item.ReferenceType == InventoryReferenceType.Order &&
                item.ReferenceId == orderId &&
                item.Type == InventoryTransactionType.Release)
            .ToArrayAsync();
        var histories = await assertionDb.OrderStatusHistories
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .ToArrayAsync();

        Assert.Single(
            receipts,
            item => item.ProviderEventId == firstProviderEventId);
        Assert.Single(
            receipts,
            item => item.ProviderEventId == secondProviderEventId);
        Assert.All(
            receipts,
            item => Assert.Equal(payloadHash, item.PayloadHash));

        Assert.Equal(PaymentStatus.Succeeded, persistedPayment.Status);
        Assert.Null(persistedPayment.FailureCode);
        Assert.Equal(OrderStatus.Confirmed, persistedOrder.Status);
        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(2, inventory.ReservedQuantity);
        Assert.Empty(releases);

        var history = Assert.Single(histories);

        Assert.Equal(OrderStatus.PendingPayment, history.FromStatus);
        Assert.Equal(OrderStatus.Confirmed, history.ToStatus);
    }

    [Fact]
    [Trait("Requirement", "PAY-WEB-001")]
    public async Task ApplyAsync_UnknownProviderPayment_ReturnsNotFoundWithoutPersistingReceipt()
    {
        var now = new DateTimeOffset(2026, 10, 2, 22, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        const string providerPaymentId = "fake-pay-not-found";
        const string providerEventId = "fake-event-payment-not-found-001";

        await using var factory = CreateFactory(userId, new FakeClock(now));
        await MigrateAsync(factory);

        var command = new ApplyPaymentResultCommand(
            providerPaymentId,
            ProviderPaymentOutcome.Succeeded,
            failureCode: null,
            PaymentResultSource.Webhook,
            new ProviderPaymentEventData(
                "Fake",
                providerEventId,
                "payment.succeeded",
                new string('f', 64)),
            now.AddMinutes(-1));

        PaymentResultApplicationOutcome outcome;

        using (var applicationScope = factory.Services.CreateScope())
        {
            var service = applicationScope.ServiceProvider
                .GetRequiredService<PaymentResultApplicationService>();

            outcome = await service.ApplyAsync(command, CancellationToken.None);
        }

        Assert.Equal(
            PaymentResultApplicationStatus.PaymentNotFound,
            outcome.Status);

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();
        var receipts = await db.ProviderPaymentEvents
            .AsNoTracking()
            .Where(item =>
                item.Provider == "Fake" &&
                item.ProviderEventId == providerEventId)
            .ToArrayAsync();

        Assert.Empty(receipts);
    }

    [Fact]
    [Trait("Requirement", "PAY-REC-004")]
    public async Task ReconcileAsync_NotFoundAfterDeadline_FailsPaymentExpiresOrderAndReleasesReservationOnce()
    {
        var createdAt = new DateTimeOffset(2026, 10, 2, 13, 30, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var clock = new FakeClock(createdAt);
        var gateway = new TimeoutPaymentGateway();

        await using var factory = CreateFactory(userId, clock, paymentGateway: gateway);
        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, createdAt);
        var productVariantId = await SeedReservationForOrderAsync(factory, orderId, createdAt);
        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(orderId, Guid.NewGuid(), PaymentScenario.Success));
        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);
        clock.UtcNow = createdAt.AddMinutes(16);

        using (var processorScope = factory.Services.CreateScope())
        {
            var processor = new PaymentReconciliationProcessor(
                clock,
                new PaymentScopedReconciliationStore(
                    processorScope.ServiceProvider.GetRequiredService<IPaymentReconciliationStore>(),
                    initiationResult.PaymentId),
                gateway,
                processorScope.ServiceProvider.GetRequiredService<PaymentResultApplicationService>(),
                batchSize: 100,
                NullLogger<PaymentReconciliationProcessor>.Instance);

            await processor.RunOnceAsync(CancellationToken.None);
        }

        using var assertionScope = factory.Services.CreateScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var payment = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == initiationResult.PaymentId);
        var order = await db.Orders.AsNoTracking().SingleAsync(item => item.Id == orderId);
        var inventory = await db.Inventories.AsNoTracking().SingleAsync(item => item.ProductVariantId == productVariantId);
        var transactions = await db.InventoryTransactions
            .AsNoTracking()
            .Where(item => item.ReferenceType == InventoryReferenceType.Order && item.ReferenceId == orderId)
            .ToArrayAsync();

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(PaymentFailureCodes.OrderNotPayable, payment.FailureCode);
        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Single(transactions, item => item.Type == InventoryTransactionType.Reserve);
        Assert.Single(transactions, item => item.Type == InventoryTransactionType.Release);
        Assert.Equal(1, gateway.CallCount);
        Assert.Equal(1, gateway.StatusQueryCount);
    }

    [Fact]
    [Trait("Requirement", "PAY-REC-005")]
    public async Task ReconcileAsync_ProcessingPaymentNotFound_RemainsUnresolvedWithoutInventoryEffect()
    {
        var createdAt = new DateTimeOffset(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);
        var checkedAt = createdAt.AddMinutes(1);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var clock = new FakeClock(createdAt);
        var gateway = new TimeoutPaymentGateway();

        await using var factory = CreateFactory(userId, clock, paymentGateway: gateway);
        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, createdAt);
        var productVariantId = await SeedReservationForOrderAsync(factory, orderId, createdAt);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(orderId, Guid.NewGuid(), PaymentScenario.Success));

        Assert.True(initiation.IsSuccess);

        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);

        using (var arrangeScope = factory.Services.CreateScope())
        {
            var db = arrangeScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            var paymentArrange = await db.Payments.SingleAsync(item => item.Id == initiationResult.PaymentId);

            paymentArrange.MarkProcessing(createdAt.AddSeconds(1));

            await db.SaveChangesAsync();
        }

        clock.UtcNow = checkedAt;

        using (var processorScope = factory.Services.CreateScope())
        {
            var processor = new PaymentReconciliationProcessor(
                clock,
                new PaymentScopedReconciliationStore(
                    processorScope.ServiceProvider.GetRequiredService<IPaymentReconciliationStore>(),
                    initiationResult.PaymentId),
                gateway,
                processorScope.ServiceProvider.GetRequiredService<PaymentResultApplicationService>(),
                batchSize: 100,
                NullLogger<PaymentReconciliationProcessor>.Instance);

            await processor.RunOnceAsync(CancellationToken.None);
        }

        using var assertionScope = factory.Services.CreateScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var payment = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == initiationResult.PaymentId);
        var order = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);
        var inventory = await assertionDb.Inventories
            .AsNoTracking()
            .SingleAsync(item => item.ProductVariantId == productVariantId);
        var transactions = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item => item.ReferenceType == InventoryReferenceType.Order && item.ReferenceId == orderId)
            .ToArrayAsync();

        Assert.Equal(PaymentStatus.Processing, payment.Status);
        Assert.Null(payment.FailureCode);
        Assert.Equal(checkedAt, payment.LastStatusCheckedAt);
        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(2, inventory.ReservedQuantity);
        Assert.Single(transactions, item => item.Type == InventoryTransactionType.Reserve);
        Assert.Equal(1, gateway.CallCount);
        Assert.Equal(1, gateway.StatusQueryCount);
    }

    [Fact]
    [Trait("Requirement", "PAY-REC-002")]
    public async Task ReconcileAsync_CrashBeforeProviderCreate_RedrivesOriginalGatewayIdentity()
    {
        var createdAt = new DateTimeOffset(2026, 10, 2, 15, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var clock = new FakeClock(createdAt);
        var hook = new ThrowingCheckpointHook(PaymentOperationCheckpoints.AfterLocalCommit);

        await using var factory = CreateFactory(userId, clock, operationHook: hook);
        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, createdAt);

        var request = new InitiatePaymentRequest(
            orderId,
            idempotencyKey,
            PaymentScenario.Success);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExecuteInitiationAsync(factory, request));

        Assert.Equal(
            $"Injected failure at '{PaymentOperationCheckpoints.AfterLocalCommit}'",
            exception.Message);

        (Guid PaymentId, string GatewayKey, string ProviderPaymentId) localIntent;

        using (var beforeRecoveryScope = factory.Services.CreateScope())
        {
            var beforeRecoveryDb = beforeRecoveryScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var paymentBeforeRecovery = await beforeRecoveryDb.Payments
                .AsNoTracking()
                .SingleAsync(item => item.OrderId == orderId);

            localIntent = (
                paymentBeforeRecovery.Id,
                paymentBeforeRecovery.GatewayIdempotencyKey,
                paymentBeforeRecovery.ProviderPaymentId);

            Assert.Equal(PaymentStatus.Pending, paymentBeforeRecovery.Status);

            var targetProviderOperationExists = await beforeRecoveryDb.FakeProviderOperations
                .AsNoTracking()
                .AnyAsync(operation =>
                    operation.OperationType == FakeProviderOperationType.CreatePayment &&
                    operation.IdempotencyKey == localIntent.GatewayKey);

            Assert.False(targetProviderOperationExists);
        }

        clock.UtcNow = createdAt.AddMinutes(1);

        using (var processorScope = factory.Services.CreateScope())
        {
            var processor = new PaymentReconciliationProcessor(
                clock,
                new PaymentScopedReconciliationStore(
                    processorScope.ServiceProvider.GetRequiredService<IPaymentReconciliationStore>(),
                    localIntent.PaymentId),
                processorScope.ServiceProvider.GetRequiredService<IPaymentGateway>(),
                processorScope.ServiceProvider.GetRequiredService<PaymentResultApplicationService>(),
                batchSize: 100,
                NullLogger<PaymentReconciliationProcessor>.Instance);

            await processor.RunOnceAsync(CancellationToken.None);
        }

        using var assertionScope = factory.Services.CreateScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var paymentsAfterRecovery = await assertionDb.Payments
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .ToArrayAsync();
        var paymentAfterRecovery = Assert.Single(paymentsAfterRecovery);
        var orderAfterRecovery = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);
        var providerOperationsAfterRecovery = await assertionDb.FakeProviderOperations
            .AsNoTracking()
            .Where(item =>
                item.OperationType == FakeProviderOperationType.CreatePayment &&
                item.IdempotencyKey == localIntent.GatewayKey)
            .ToArrayAsync();
        var providerOperationAfterRecovery = Assert.Single(providerOperationsAfterRecovery);

        Assert.Equal(localIntent.PaymentId, paymentAfterRecovery.Id);
        Assert.Equal(PaymentStatus.Succeeded, paymentAfterRecovery.Status);
        Assert.Equal(OrderStatus.Confirmed, orderAfterRecovery.Status);
        Assert.Equal(localIntent.GatewayKey, providerOperationAfterRecovery.IdempotencyKey);
        Assert.Equal(localIntent.ProviderPaymentId, providerOperationAfterRecovery.ProviderResourceId);
    }

    [Fact]
    [Trait("Requirement", "PAY-REC-003")]
    public async Task ReconcileAsync_PendingPaymentNotFoundForCancelledOrder_MarksPaymentNotPayableWithoutSideEffects()
    {
        var createdAt = new DateTimeOffset(2026, 10, 2, 16, 0, 0, TimeSpan.Zero);
        var checkedAt = createdAt.AddMinutes(1);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var clock = new FakeClock(createdAt);
        var gateway = new TimeoutPaymentGateway();

        await using var factory = CreateFactory(userId, clock, paymentGateway: gateway);
        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, createdAt);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(
                orderId,
                Guid.NewGuid(),
                PaymentScenario.Success));

        Assert.True(initiation.IsSuccess);

        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);

        using (var arrangeScope = factory.Services.CreateScope())
        {
            var arrangeDb = arrangeScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            var orderArrange = await arrangeDb.Orders.SingleAsync(item => item.Id == orderId);

            orderArrange.Cancel(createdAt.AddSeconds(1));

            await arrangeDb.SaveChangesAsync();
        }

        clock.UtcNow = checkedAt;

        using (var processorScope = factory.Services.CreateScope())
        {
            var processor = new PaymentReconciliationProcessor(
                clock,
                new PaymentScopedReconciliationStore(
                    processorScope.ServiceProvider.GetRequiredService<IPaymentReconciliationStore>(),
                    initiationResult.PaymentId),
                gateway,
                processorScope.ServiceProvider.GetRequiredService<PaymentResultApplicationService>(),
                batchSize: 100,
                NullLogger<PaymentReconciliationProcessor>.Instance);

            await processor.RunOnceAsync(CancellationToken.None);
        }

        using var assertionScope = factory.Services.CreateScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var paymentAfterReconciliation = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == initiationResult.PaymentId);
        var orderAfterReconciliation = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);
        var inventoryTransactionsAfterReconciliation = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item => item.ReferenceType == InventoryReferenceType.Order && item.ReferenceId == orderId)
            .ToArrayAsync();

        Assert.Equal(PaymentStatus.Failed, paymentAfterReconciliation.Status);
        Assert.Equal(PaymentFailureCodes.OrderNotPayable, paymentAfterReconciliation.FailureCode);
        Assert.Equal(OrderStatus.Cancelled, orderAfterReconciliation.Status);
        Assert.Empty(inventoryTransactionsAfterReconciliation);
        Assert.Equal(1, gateway.CallCount);
        Assert.Equal(1, gateway.StatusQueryCount);
    }

    [Fact]
    [Trait("Requirement", "PAY-REC-003")]
    public async Task ReconcileAsync_PendingPaymentNotFoundForExpiredOrder_MarksPaymentNotPayableWithoutDuplicateRelease()
    {
        var createdAt = new DateTimeOffset(2026, 10, 2, 17, 0, 0, TimeSpan.Zero);
        var expiredAt = createdAt.AddMinutes(15);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var clock = new FakeClock(createdAt);
        var gateway = new TimeoutPaymentGateway();

        await using var factory = CreateFactory(userId, clock, paymentGateway: gateway);
        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, createdAt);
        var productVariantId = await SeedReservationForOrderAsync(factory, orderId, createdAt);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(
                orderId,
                Guid.NewGuid(),
                PaymentScenario.Success));

        Assert.True(initiation.IsSuccess);

        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);

        using (var expirationScope = factory.Services.CreateScope())
        {
            var expirationStore = expirationScope.ServiceProvider
                .GetRequiredService<IReservationExpirationStore>();

            var expirationOutcome = await expirationStore.TryExpireAsync(
                orderId,
                expiredAt,
                CancellationToken.None);

            Assert.Equal(ReservationExpirationOutcome.Expired, expirationOutcome);
        }

        clock.UtcNow = expiredAt;

        using (var processorScope = factory.Services.CreateScope())
        {
            var processor = new PaymentReconciliationProcessor(
                clock,
                new PaymentScopedReconciliationStore(
                    processorScope.ServiceProvider.GetRequiredService<IPaymentReconciliationStore>(),
                    initiationResult.PaymentId),
                gateway,
                processorScope.ServiceProvider.GetRequiredService<PaymentResultApplicationService>(),
                batchSize: 100,
                NullLogger<PaymentReconciliationProcessor>.Instance);

            await processor.RunOnceAsync(CancellationToken.None);
        }

        using var assertionScope = factory.Services.CreateScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var paymentAfterReconciliation = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == initiationResult.PaymentId);
        var orderAfterReconciliation = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);
        var inventoryAfterReconciliation = await assertionDb.Inventories
            .AsNoTracking()
            .SingleAsync(item => item.ProductVariantId == productVariantId);
        var transactionsAfterReconciliation = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item => item.ReferenceType == InventoryReferenceType.Order && item.ReferenceId == orderId)
            .ToArrayAsync();
        var historiesAfterReconciliation = await assertionDb.OrderStatusHistories
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .ToArrayAsync();

        Assert.Equal(PaymentStatus.Failed, paymentAfterReconciliation.Status);
        Assert.Equal(PaymentFailureCodes.OrderNotPayable, paymentAfterReconciliation.FailureCode);
        Assert.Equal(OrderStatus.Expired, orderAfterReconciliation.Status);
        Assert.Equal(10, inventoryAfterReconciliation.OnHandQuantity);
        Assert.Equal(0, inventoryAfterReconciliation.ReservedQuantity);
        Assert.Single(
            transactionsAfterReconciliation,
            item => item.Type == InventoryTransactionType.Reserve);
        Assert.Single(
            transactionsAfterReconciliation,
            item => item.Type == InventoryTransactionType.Release);
        Assert.Single(historiesAfterReconciliation);
        Assert.Equal(1, gateway.CallCount);
        Assert.Equal(1, gateway.StatusQueryCount);
    }

    [Fact]
    [Trait("Requirement", "PAY-REC-001")]
    public async Task ReconcileAsync_ProviderCommittedButResponseWasLost_AppliesSuccessOnce()
    {
        var createdAt = new DateTimeOffset(2026, 10, 2, 18, 0, 0, TimeSpan.Zero);
        var reconciledAt = createdAt.AddMinutes(1);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var clock = new FakeClock(createdAt);

        await using var factory = CreateFactory(userId, clock);
        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, createdAt);
        var productVariantId = await SeedReservationForOrderAsync(factory, orderId, createdAt);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(
                orderId,
                Guid.NewGuid(),
                PaymentScenario.SuccessButResponseLost));

        Assert.True(initiation.IsSuccess);

        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);

        Assert.Equal(PaymentStatus.Pending, initiationResult.Status);

        clock.UtcNow = reconciledAt;

        using (var processorScope = factory.Services.CreateScope())
        {
            var processor = new PaymentReconciliationProcessor(
                clock,
                new PaymentScopedReconciliationStore(
                    processorScope.ServiceProvider.GetRequiredService<IPaymentReconciliationStore>(),
                    initiationResult.PaymentId),
                processorScope.ServiceProvider.GetRequiredService<IPaymentGateway>(),
                processorScope.ServiceProvider.GetRequiredService<PaymentResultApplicationService>(),
                batchSize: 100,
                NullLogger<PaymentReconciliationProcessor>.Instance);

            await processor.RunOnceAsync(CancellationToken.None);
        }

        using var assertionScope = factory.Services.CreateScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var paymentsAfterReconciliation = await assertionDb.Payments
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .ToArrayAsync();
        var paymentAfterReconciliation = Assert.Single(paymentsAfterReconciliation);
        var orderAfterReconciliation = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);
        var inventoryAfterReconciliation = await assertionDb.Inventories
            .AsNoTracking()
            .SingleAsync(item => item.ProductVariantId == productVariantId);
        var transactionsAfterReconciliation = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item => item.ReferenceType == InventoryReferenceType.Order && item.ReferenceId == orderId)
            .ToArrayAsync();
        var historiesAfterReconciliation = await assertionDb.OrderStatusHistories
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .ToArrayAsync();
        var providerOperationsAfterReconciliation = await assertionDb.FakeProviderOperations
            .AsNoTracking()
            .Where(item =>
                item.OperationType == FakeProviderOperationType.CreatePayment &&
                item.IdempotencyKey == paymentAfterReconciliation.GatewayIdempotencyKey)
            .ToArrayAsync();

        var providerOperationAfterReconciliation =
            Assert.Single(providerOperationsAfterReconciliation);

        Assert.Equal(initiationResult.PaymentId, paymentAfterReconciliation.Id);
        Assert.Equal(PaymentStatus.Succeeded, paymentAfterReconciliation.Status);
        Assert.Null(paymentAfterReconciliation.FailureCode);
        Assert.Equal(OrderStatus.Confirmed, orderAfterReconciliation.Status);
        Assert.Equal(10, inventoryAfterReconciliation.OnHandQuantity);
        Assert.Equal(2, inventoryAfterReconciliation.ReservedQuantity);
        Assert.Single(
            transactionsAfterReconciliation,
            item => item.Type == InventoryTransactionType.Reserve);
        Assert.Single(historiesAfterReconciliation);
        Assert.Equal(
            paymentAfterReconciliation.ProviderPaymentId,
            providerOperationAfterReconciliation.ProviderResourceId);
        Assert.Equal(
            PaymentScenario.SuccessButResponseLost,
            providerOperationAfterReconciliation.Scenario);
        Assert.Equal(
            FakeProviderOperationStatus.Succeeded,
            providerOperationAfterReconciliation.Status);
    }

    [Fact]
    [Trait("Requirement", "PAY-RACE-003")]
    public async Task ReconcileAsync_OrderCancelledBeforeNotFoundEligibilityCheck_DoesNotRedriveCreate()
    {
        var createdAt = new DateTimeOffset(2026, 10, 2, 19, 0, 0, TimeSpan.Zero);
        var reconciliationAt = createdAt.AddMinutes(1);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var clock = new FakeClock(createdAt);
        var gateway = new BlockingNotFoundPaymentGateway();

        await using var factory = CreateFactory(userId, clock, paymentGateway: gateway);
        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, createdAt);
        var productVariantId = await SeedReservationForOrderAsync(factory, orderId, createdAt);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(
                orderId,
                Guid.NewGuid(),
                PaymentScenario.Success));

        Assert.True(initiation.IsSuccess);

        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);

        Assert.Equal(PaymentStatus.Pending, initiationResult.Status);

        clock.UtcNow = reconciliationAt;

        using var processorScope = factory.Services.CreateScope();

        var processor = new PaymentReconciliationProcessor(
            clock,
            new PaymentScopedReconciliationStore(
                processorScope.ServiceProvider.GetRequiredService<IPaymentReconciliationStore>(),
                initiationResult.PaymentId),
            gateway,
            processorScope.ServiceProvider.GetRequiredService<PaymentResultApplicationService>(),
            batchSize: 100,
            NullLogger<PaymentReconciliationProcessor>.Instance);

        var reconciliationTask = processor.RunOnceAsync(CancellationToken.None);

        await gateway.StatusQueryStarted.WaitAsync(TimeSpan.FromSeconds(2));

        ApplicationResult<OrderDto> cancellationResult;

        using (var cancellationScope = factory.Services.CreateScope())
        {
            var orderCommandService = cancellationScope.ServiceProvider
                .GetRequiredService<OrderCommandService>();

            cancellationResult = await orderCommandService.CancelAsync(
                orderId,
                new CancelOrderRequest(Reason: "Customer requested cancellation"),
                CancellationToken.None);
        }

        gateway.ReleaseStatusQuery();

        await reconciliationTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cancellationResult.IsSuccess);

        using var assertionScope = factory.Services.CreateScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var paymentAfterRace = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == initiationResult.PaymentId);
        var orderAfterRace = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);
        var inventoryAfterRace = await assertionDb.Inventories
            .AsNoTracking()
            .SingleAsync(item => item.ProductVariantId == productVariantId);
        var transactionsAfterRace = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item => item.ReferenceType == InventoryReferenceType.Order && item.ReferenceId == orderId)
            .ToArrayAsync();

        Assert.Equal(PaymentStatus.Failed, paymentAfterRace.Status);
        Assert.Equal(PaymentFailureCodes.OrderNotPayable, paymentAfterRace.FailureCode);
        Assert.Equal(OrderStatus.Cancelled, orderAfterRace.Status);
        Assert.Equal(10, inventoryAfterRace.OnHandQuantity);
        Assert.Equal(0, inventoryAfterRace.ReservedQuantity);
        Assert.Single(
            transactionsAfterRace,
            item => item.Type == InventoryTransactionType.Reserve);
        Assert.Single(
            transactionsAfterRace,
            item => item.Type == InventoryTransactionType.Release);
        Assert.Equal(1, gateway.CreateCallCount);
        Assert.Equal(1, gateway.StatusQueryCount);
    }

    [Fact]
    [Trait("Requirement", "PAY-RACE-003")]
    public async Task ReconcileAsync_OrderExpiredBeforeNotFoundEligibilityCheck_DoesNotRedriveOrReleaseTwice()
    {
        var createdAt = new DateTimeOffset(2026, 10, 2, 20, 0, 0, TimeSpan.Zero);
        var expiredAt = createdAt.AddMinutes(15);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var clock = new FakeClock(createdAt);
        var gateway = new BlockingNotFoundPaymentGateway();

        await using var factory = CreateFactory(userId, clock, paymentGateway: gateway);
        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, createdAt);
        var productVariantId = await SeedReservationForOrderAsync(factory, orderId, createdAt);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(
                orderId,
                Guid.NewGuid(),
                PaymentScenario.Success));

        Assert.True(initiation.IsSuccess);

        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);

        Assert.Equal(PaymentStatus.Pending, initiationResult.Status);

        clock.UtcNow = expiredAt;

        using var processorScope = factory.Services.CreateScope();

        var processor = new PaymentReconciliationProcessor(
            clock,
            new PaymentScopedReconciliationStore(
                processorScope.ServiceProvider.GetRequiredService<IPaymentReconciliationStore>(),
                initiationResult.PaymentId),
            gateway,
            processorScope.ServiceProvider.GetRequiredService<PaymentResultApplicationService>(),
            batchSize: 100,
            NullLogger<PaymentReconciliationProcessor>.Instance);

        var reconciliationTask = processor.RunOnceAsync(CancellationToken.None);

        await gateway.StatusQueryStarted.WaitAsync(TimeSpan.FromSeconds(2));

        ReservationExpirationOutcome expirationOutcome;

        using (var expirationScope = factory.Services.CreateScope())
        {
            var expirationStore = expirationScope.ServiceProvider
                .GetRequiredService<IReservationExpirationStore>();

            expirationOutcome = await expirationStore.TryExpireAsync(
                orderId,
                expiredAt,
                CancellationToken.None);
        }

        gateway.ReleaseStatusQuery();

        await reconciliationTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ReservationExpirationOutcome.Expired, expirationOutcome);

        using var assertionScope = factory.Services.CreateScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var paymentAfterRace = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == initiationResult.PaymentId);
        var orderAfterRace = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);
        var inventoryAfterRace = await assertionDb.Inventories
            .AsNoTracking()
            .SingleAsync(item => item.ProductVariantId == productVariantId);
        var transactionsAfterRace = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item => item.ReferenceType == InventoryReferenceType.Order && item.ReferenceId == orderId)
            .ToArrayAsync();
        var historiesAfterRace = await assertionDb.OrderStatusHistories
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .ToArrayAsync();

        Assert.Equal(PaymentStatus.Failed, paymentAfterRace.Status);
        Assert.Equal(PaymentFailureCodes.OrderNotPayable, paymentAfterRace.FailureCode);
        Assert.Equal(OrderStatus.Expired, orderAfterRace.Status);
        Assert.Equal(10, inventoryAfterRace.OnHandQuantity);
        Assert.Equal(0, inventoryAfterRace.ReservedQuantity);
        Assert.Single(
            transactionsAfterRace,
            item => item.Type == InventoryTransactionType.Reserve);
        Assert.Single(
            transactionsAfterRace,
            item => item.Type == InventoryTransactionType.Release);
        Assert.Single(historiesAfterRace);
        Assert.Equal(1, gateway.CreateCallCount);
        Assert.Equal(1, gateway.StatusQueryCount);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-006")]
    public async Task LateSuccessAfterExpiry_StockAvailable_ReacquiresAndConfirmsAtomically()
    {
        var createdAt = new DateTimeOffset(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);
        var expiredAt = createdAt.AddMinutes(15);
        var succeededAt = expiredAt.AddMinutes(1);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var clock = new FakeClock(createdAt);
        var gateway = new TimeoutPaymentGateway();

        await using var factory = CreateFactory(userId, clock, paymentGateway: gateway);
        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, createdAt);

        var productVariantId = await SeedReservationForOrderAsync(
            factory,
            orderId,
            createdAt,
            quantity: 2);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(
                orderId,
                Guid.NewGuid(),
                PaymentScenario.Success));

        Assert.True(initiation.IsSuccess);

        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);
        string providerPaymentId;

        using (var preparationScope = factory.Services.CreateScope())
        {
            var db = preparationScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            providerPaymentId = await db.Payments
                .Where(item => item.Id == initiationResult.PaymentId)
                .Select(item => item.ProviderPaymentId)
                .SingleAsync();

            var expirationStore = preparationScope.ServiceProvider
                .GetRequiredService<IReservationExpirationStore>();

            var expirationOutcome = await expirationStore.TryExpireAsync(
                orderId,
                expiredAt,
                CancellationToken.None);

            Assert.Equal(ReservationExpirationOutcome.Expired, expirationOutcome);
        }

        clock.UtcNow = succeededAt;

        using (var applicationScope = factory.Services.CreateScope())
        {
            var service = applicationScope.ServiceProvider
                .GetRequiredService<PaymentResultApplicationService>();

            var outcome = await service.ApplyAsync(
                new ApplyPaymentResultCommand(
                    providerPaymentId,
                    ProviderPaymentOutcome.Succeeded,
                    failureCode: null,
                    PaymentResultSource.Reconciliation,
                    providerEvent: null,
                    occurredAt: succeededAt),
                CancellationToken.None);

            Assert.Equal(PaymentResultApplicationStatus.Accepted, outcome.Status);
        }

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var payment = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == initiationResult.PaymentId);

        var order = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);

        var inventory = await assertionDb.Inventories
            .AsNoTracking()
            .SingleAsync(item => item.ProductVariantId == productVariantId);

        var inventoryTransactions = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item =>
                item.ReferenceType == InventoryReferenceType.Order &&
                item.ReferenceId == orderId)
            .ToArrayAsync();

        var histories = await assertionDb.OrderStatusHistories
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .ToArrayAsync();

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(2, inventory.ReservedQuantity);

        var recoveryReserve = Assert.Single(
            inventoryTransactions,
            item =>
                item.Type == InventoryTransactionType.Reserve &&
                item.CreatedAt == succeededAt);

        Assert.Equal(0, recoveryReserve.OnHandQuantityDelta);
        Assert.Equal(2, recoveryReserve.ReservedQuantityDelta);

        Assert.Single(
            histories,
            item =>
                item.FromStatus == OrderStatus.Expired &&
                item.ToStatus == OrderStatus.Confirmed);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-007")]
    public async Task LateSuccessAfterExpiry_PartialReacquisitionRollsBackBeforeRefundPending()
    {
        var createdAt = new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
        var expiredAt = createdAt.AddMinutes(15);
        var stockChangedAt = expiredAt.AddSeconds(1);
        var succeededAt = expiredAt.AddMinutes(1);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var clock = new FakeClock(createdAt);
        var gateway = new TimeoutPaymentGateway();

        var variantSeed = Guid.NewGuid().ToString("N");
        var availableVariantId = Guid.ParseExact(
            $"{variantSeed[..30]}01",
            "N");
        var unavailableVariantId = Guid.ParseExact(
            $"{variantSeed[..30]}02",
            "N");

        await using var factory = CreateFactory(
            userId,
            clock,
            paymentGateway: gateway);

        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(
            factory,
            userId,
            orderId,
            createdAt);

        await SeedReservationForOrderAsync(
            factory,
            orderId,
            createdAt,
            quantity: 2,
            productVariantId: availableVariantId,
            initialOnHand: 2);

        await SeedReservationForOrderAsync(
            factory,
            orderId,
            createdAt,
            quantity: 2,
            productVariantId: unavailableVariantId,
            initialOnHand: 2);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(
                orderId,
                Guid.NewGuid(),
                PaymentScenario.Success));

        Assert.True(initiation.IsSuccess);

        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);

        string providerPaymentId;

        using (var paymentScope = factory.Services.CreateScope())
        {
            var db = paymentScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            providerPaymentId = await db.Payments
                .Where(item => item.Id == initiationResult.PaymentId)
                .Select(item => item.ProviderPaymentId)
                .SingleAsync();
        }

        using (var expirationScope = factory.Services.CreateScope())
        {
            var expirationStore = expirationScope.ServiceProvider
                .GetRequiredService<IReservationExpirationStore>();

            var expirationOutcome = await expirationStore.TryExpireAsync(
                orderId,
                expiredAt,
                CancellationToken.None);

            Assert.Equal(
                ReservationExpirationOutcome.Expired,
                expirationOutcome);
        }

        using (var adjustmentScope = factory.Services.CreateScope())
        {
            var db = adjustmentScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var unavailableInventory = await db.Inventories
                .SingleAsync(
                    item =>
                        item.ProductVariantId ==
                        unavailableVariantId);

            unavailableInventory.AdjustOnHand(
                quantityDelta: -2,
                updatedAt: stockChangedAt);

            db.InventoryTransactions.Add(
                new InventoryTransaction(
                    Guid.NewGuid(),
                    unavailableVariantId,
                    InventoryTransactionType.Adjustment,
                    onHandQuantityDelta: -2,
                    reservedQuantityDelta: 0,
                    referenceType: null,
                    referenceId: null,
                    reason: "Simulate stock becoming unavailable",
                    createdAt: stockChangedAt));

            await db.SaveChangesAsync();
        }

        clock.UtcNow = succeededAt;

        using (var applicationScope = factory.Services.CreateScope())
        {
            var service = applicationScope.ServiceProvider
                .GetRequiredService<PaymentResultApplicationService>();

            var outcome = await service.ApplyAsync(
                new ApplyPaymentResultCommand(
                    providerPaymentId,
                    ProviderPaymentOutcome.Succeeded,
                    failureCode: null,
                    PaymentResultSource.Reconciliation,
                    providerEvent: null,
                    occurredAt: succeededAt),
                CancellationToken.None);

            Assert.Equal(
                PaymentResultApplicationStatus.Accepted,
                outcome.Status);
        }

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var payment = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(
                item => item.Id == initiationResult.PaymentId);

        var order = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);

        var inventories = await assertionDb.Inventories
            .AsNoTracking()
            .Where(item =>
                item.ProductVariantId == availableVariantId ||
                item.ProductVariantId == unavailableVariantId)
            .ToDictionaryAsync(
                item => item.ProductVariantId);

        var recoveryReserves = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item =>
                item.ReferenceType == InventoryReferenceType.Order &&
                item.ReferenceId == orderId &&
                item.Type == InventoryTransactionType.Reserve &&
                item.CreatedAt == succeededAt)
            .ToArrayAsync();

        var recoveryHistories = await assertionDb.OrderStatusHistories
            .AsNoTracking()
            .Where(item =>
                item.OrderId == orderId &&
                item.FromStatus == OrderStatus.Expired &&
                item.ToStatus == OrderStatus.Confirmed)
            .ToArrayAsync();

        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
        Assert.Equal($"fake-refund-{payment.Id:D}", payment.RefundIdempotencyKey);
        Assert.Equal(succeededAt, payment.RefundRequestedAt);
        Assert.Equal(succeededAt, payment.NextRefundAttemptAt);

        Assert.Equal(OrderStatus.Expired, order.Status);

        Assert.Equal(0, inventories[availableVariantId].ReservedQuantity);

        Assert.Equal(0, inventories[unavailableVariantId].ReservedQuantity);

        Assert.Empty(recoveryReserves);
        Assert.Empty(recoveryHistories);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-008")]
    public async Task LateSuccessAfterCancellation_RefundsAndNeverRecoversOrder()
    {
        var createdAt = new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
        var cancelledAt = createdAt.AddMinutes(1);
        var succeededAt = createdAt.AddMinutes(2);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var clock = new FakeClock(createdAt);
        var gateway = new TimeoutPaymentGateway();

        await using var factory = CreateFactory(
            userId,
            clock,
            paymentGateway: gateway);

        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(
            factory,
            userId,
            orderId,
            createdAt);

        var productVariantId = await SeedReservationForOrderAsync(
            factory,
            orderId,
            createdAt,
            quantity: 2);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(
                orderId,
                Guid.NewGuid(),
                PaymentScenario.Success));

        Assert.True(initiation.IsSuccess);

        var initiationResult =
            Assert.IsType<PaymentInitiationResult>(initiation.Value);

        string providerPaymentId;

        using (var paymentScope = factory.Services.CreateScope())
        {
            var db = paymentScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            providerPaymentId = await db.Payments
                .Where(item => item.Id == initiationResult.PaymentId)
                .Select(item => item.ProviderPaymentId)
                .SingleAsync();
        }

        clock.UtcNow = cancelledAt;

        using (var cancellationScope = factory.Services.CreateScope())
        {
            var orderService = cancellationScope.ServiceProvider
                .GetRequiredService<OrderCommandService>();

            var cancellation = await orderService.CancelAsync(
                orderId,
                new CancelOrderRequest(
                    Reason: "Customer requested cancellation"),
                CancellationToken.None);

            Assert.True(cancellation.IsSuccess);
        }

        clock.UtcNow = succeededAt;

        using (var applicationScope = factory.Services.CreateScope())
        {
            var service = applicationScope.ServiceProvider
                .GetRequiredService<PaymentResultApplicationService>();

            var outcome = await service.ApplyAsync(
                new ApplyPaymentResultCommand(
                    providerPaymentId,
                    ProviderPaymentOutcome.Succeeded,
                    failureCode: null,
                    PaymentResultSource.Reconciliation,
                    providerEvent: null,
                    occurredAt: succeededAt),
                CancellationToken.None);

            Assert.Equal(
                PaymentResultApplicationStatus.Accepted,
                outcome.Status);
        }

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var payment = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == initiationResult.PaymentId);

        var order = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);

        var inventory = await assertionDb.Inventories
            .AsNoTracking()
            .SingleAsync(
                item => item.ProductVariantId == productVariantId);

        var inventoryTransactions = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item =>
                item.ReferenceType == InventoryReferenceType.Order &&
                item.ReferenceId == orderId)
            .ToArrayAsync();

        var recoveryHistories = await assertionDb.OrderStatusHistories
            .AsNoTracking()
            .Where(item =>
                item.OrderId == orderId &&
                item.ToStatus == OrderStatus.Confirmed)
            .ToArrayAsync();

        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
        Assert.NotNull(payment.RefundIdempotencyKey);
        Assert.Equal(succeededAt, payment.RefundRequestedAt);
        Assert.Equal(succeededAt, payment.NextRefundAttemptAt);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);

        Assert.Single(
            inventoryTransactions,
            item => item.Type == InventoryTransactionType.Reserve);

        Assert.Single(
            inventoryTransactions,
            item => item.Type == InventoryTransactionType.Release);

        Assert.DoesNotContain(
            inventoryTransactions,
            item =>
                item.Type == InventoryTransactionType.Reserve &&
                item.CreatedAt == succeededAt);

        Assert.Empty(recoveryHistories);
    }

    [Fact]
    [Trait("Requirement", "PAY-WEB-004")]
    public async Task LateSuccessWebhook_ReacquisitionRollback_ReclaimsReceiptWithCompensationAtomically()
    {
        var createdAt = new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
        var expiredAt = createdAt.AddMinutes(15);
        var stockChangedAt = expiredAt.AddSeconds(1);
        var succeededAt = expiredAt.AddMinutes(1);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var clock = new FakeClock(createdAt);
        var gateway = new TimeoutPaymentGateway();
        var providerEventId = $"late-success-{Guid.NewGuid():N}";

        var variantSeed = Guid.NewGuid().ToString("N");
        var availableVariantId = Guid.ParseExact(
            $"{variantSeed[..30]}01",
            "N");
        var unavailableVariantId = Guid.ParseExact(
            $"{variantSeed[..30]}02",
            "N");

        await using var factory = CreateFactory(
            userId,
            clock,
            paymentGateway: gateway);

        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(
            factory,
            userId,
            orderId,
            createdAt);

        await SeedReservationForOrderAsync(
            factory,
            orderId,
            createdAt,
            quantity: 2,
            productVariantId: availableVariantId,
            initialOnHand: 2);

        await SeedReservationForOrderAsync(
            factory,
            orderId,
            createdAt,
            quantity: 2,
            productVariantId: unavailableVariantId,
            initialOnHand: 2);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(
                orderId,
                Guid.NewGuid(),
                PaymentScenario.Success));

        Assert.True(initiation.IsSuccess);

        var initiationResult =
            Assert.IsType<PaymentInitiationResult>(initiation.Value);

        string providerPaymentId;

        using (var paymentScope = factory.Services.CreateScope())
        {
            var db = paymentScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            providerPaymentId = await db.Payments
                .Where(item => item.Id == initiationResult.PaymentId)
                .Select(item => item.ProviderPaymentId)
                .SingleAsync();
        }

        using (var expirationScope = factory.Services.CreateScope())
        {
            var expirationStore = expirationScope.ServiceProvider
                .GetRequiredService<IReservationExpirationStore>();

            var expirationOutcome = await expirationStore.TryExpireAsync(
                orderId,
                expiredAt,
                CancellationToken.None);

            Assert.Equal(
                ReservationExpirationOutcome.Expired,
                expirationOutcome);
        }

        using (var adjustmentScope = factory.Services.CreateScope())
        {
            var db = adjustmentScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var unavailableInventory = await db.Inventories
                .SingleAsync(
                    item =>
                        item.ProductVariantId ==
                        unavailableVariantId);

            unavailableInventory.AdjustOnHand(
                quantityDelta: -2,
                updatedAt: stockChangedAt);

            db.InventoryTransactions.Add(
                new InventoryTransaction(
                    Guid.NewGuid(),
                    unavailableVariantId,
                    InventoryTransactionType.Adjustment,
                    onHandQuantityDelta: -2,
                    reservedQuantityDelta: 0,
                    referenceType: null,
                    referenceId: null,
                    reason: "Simulate stock becoming unavailable",
                    createdAt: stockChangedAt));

            await db.SaveChangesAsync();
        }

        clock.UtcNow = succeededAt;

        using (var applicationScope = factory.Services.CreateScope())
        {
            var service = applicationScope.ServiceProvider
                .GetRequiredService<PaymentResultApplicationService>();

            var providerEvent = new ProviderPaymentEventData(
                PaymentProviderCodes.Fake,
                providerEventId,
                "payment.succeeded",
                new string('a', 64)
            );

            var outcome = await service.ApplyAsync(
                new ApplyPaymentResultCommand(
                    providerPaymentId,
                    ProviderPaymentOutcome.Succeeded,
                    failureCode: null,
                    PaymentResultSource.Webhook,
                    providerEvent,
                    occurredAt: succeededAt),
                CancellationToken.None);

            Assert.Equal(PaymentResultApplicationStatus.Accepted, outcome.Status);
        }

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var payment = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(
                item => item.Id == initiationResult.PaymentId);

        var order = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);

        var inventories = await assertionDb.Inventories
            .AsNoTracking()
            .Where(item =>
                item.ProductVariantId == availableVariantId ||
                item.ProductVariantId == unavailableVariantId)
            .ToDictionaryAsync(
                item => item.ProductVariantId);

        var recoveryReserves = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item =>
                item.ReferenceType == InventoryReferenceType.Order &&
                item.ReferenceId == orderId &&
                item.Type == InventoryTransactionType.Reserve &&
                item.CreatedAt == succeededAt)
            .ToArrayAsync();

        var recoveryHistories = await assertionDb.OrderStatusHistories
            .AsNoTracking()
            .Where(item =>
                item.OrderId == orderId &&
                item.FromStatus == OrderStatus.Expired &&
                item.ToStatus == OrderStatus.Confirmed)
            .ToArrayAsync();

        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
        Assert.NotNull(payment.RefundIdempotencyKey);
        Assert.Equal(succeededAt, payment.RefundRequestedAt);
        Assert.Equal(succeededAt, payment.NextRefundAttemptAt);

        Assert.Equal(OrderStatus.Expired, order.Status);

        Assert.Equal(
            0,
            inventories[availableVariantId].ReservedQuantity);

        Assert.Equal(
            0,
            inventories[unavailableVariantId].ReservedQuantity);

        Assert.Empty(recoveryReserves);
        Assert.Empty(recoveryHistories);



        var receipts = await assertionDb.ProviderPaymentEvents
            .AsNoTracking()
            .Where(item =>
                item.Provider == PaymentProviderCodes.Fake &&
                item.ProviderEventId == providerEventId)
            .ToArrayAsync();

        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal(0, inventories[availableVariantId].ReservedQuantity);
        Assert.Equal(0, inventories[unavailableVariantId].ReservedQuantity);
        Assert.Empty(recoveryReserves);
        Assert.Empty(recoveryHistories);
    }

    [Fact]
    [Trait("Requirement", "PAY-RACE-004")]
    public async Task LateSuccessCompensation_AfterConcurrentRecovery_DoesNotRefundConfirmedOrder()
    {
        var createdAt = new DateTimeOffset(2026, 10, 3, 11, 0, 0, TimeSpan.Zero);
        var expiredAt = createdAt.AddMinutes(15);
        var stockChangedAt = expiredAt.AddSeconds(1);
        var succeededAt = expiredAt.AddMinutes(1);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var clock = new FakeClock(createdAt);
        var gateway = new TimeoutPaymentGateway();

        var variantSeed = Guid.NewGuid().ToString("N");
        var availableVariantId = Guid.ParseExact($"{variantSeed[..30]}01", "N");
        var unavailableVariantId = Guid.ParseExact($"{variantSeed[..30]}02", "N");

        await using var factory = CreateFactory(
            userId,
            clock,
            paymentGateway: gateway,
            blockFreshCompensation: true);

        await MigrateAsync(factory);
        await SeedPendingPaymentOrderAsync(factory, userId, orderId, createdAt);

        await SeedReservationForOrderAsync(
            factory,
            orderId,
            createdAt,
            quantity: 2,
            productVariantId: availableVariantId,
            initialOnHand: 2);

        await SeedReservationForOrderAsync(
            factory,
            orderId,
            createdAt,
            quantity: 2,
            productVariantId: unavailableVariantId,
            initialOnHand: 2);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(
                orderId,
                Guid.NewGuid(),
                PaymentScenario.Success));

        Assert.True(initiation.IsSuccess);

        var initiationResult = Assert.IsType<PaymentInitiationResult>(initiation.Value);

        string providerPaymentId;

        using (var paymentScope = factory.Services.CreateScope())
        {
            var db = paymentScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            providerPaymentId = await db.Payments
                .Where(item => item.Id == initiationResult.PaymentId)
                .Select(item => item.ProviderPaymentId)
                .SingleAsync();
        }

        using (var expirationScope = factory.Services.CreateScope())
        {
            var expirationStore = expirationScope.ServiceProvider
                .GetRequiredService<IReservationExpirationStore>();

            var expirationOutcome = await expirationStore.TryExpireAsync(
                orderId,
                expiredAt,
                CancellationToken.None);

            Assert.Equal(
                ReservationExpirationOutcome.Expired,
                expirationOutcome);
        }

        using (var adjustmentScope = factory.Services.CreateScope())
        {
            var db = adjustmentScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var unavailableInventory = await db.Inventories
                .SingleAsync(
                    item => item.ProductVariantId == unavailableVariantId);

            unavailableInventory.AdjustOnHand(
                quantityDelta: -2,
                updatedAt: stockChangedAt);

            db.InventoryTransactions.Add(
                new InventoryTransaction(
                    Guid.NewGuid(),
                    unavailableVariantId,
                    InventoryTransactionType.Adjustment,
                    onHandQuantityDelta: -2,
                    reservedQuantityDelta: 0,
                    referenceType: null,
                    referenceId: null,
                    reason: "Simulate stock becoming unavailable",
                    createdAt: stockChangedAt));

            await db.SaveChangesAsync();
        }

        clock.UtcNow = succeededAt;

        var command = new ApplyPaymentResultCommand(
            providerPaymentId,
            ProviderPaymentOutcome.Succeeded,
            failureCode: null,
            PaymentResultSource.Reconciliation,
            providerEvent: null,
            occurredAt: succeededAt);

        var compensationGate = factory.Services
            .GetRequiredService<BlockingPaymentResultApplicationScopeFactory>();

        var firstActor = ApplyPaymentResultFromIndependentScopeAsync(
            factory,
            command);

        await compensationGate.Reached.WaitAsync(TimeSpan.FromSeconds(10));

        PaymentResultApplicationOutcome secondOutcome;

        try
        {
            using (var restockScope = factory.Services.CreateScope())
            {
                var db = restockScope.ServiceProvider
                    .GetRequiredService<OrderSystemDbContext>();

                var unavailableInventory = await db.Inventories
                    .SingleAsync(
                        item => item.ProductVariantId == unavailableVariantId);

                unavailableInventory.AdjustOnHand(
                    quantityDelta: 2,
                    updatedAt: succeededAt);

                db.InventoryTransactions.Add(
                    new InventoryTransaction(
                        Guid.NewGuid(),
                        unavailableVariantId,
                        InventoryTransactionType.Adjustment,
                        onHandQuantityDelta: 2,
                        reservedQuantityDelta: 0,
                        referenceType: null,
                        referenceId: null,
                        reason: "Restore stock before concurrent recovery",
                        createdAt: succeededAt));

                await db.SaveChangesAsync();
            }

            secondOutcome = await ApplyPaymentResultFromIndependentScopeAsync(
                factory,
                command);
        }
        finally
        {
            compensationGate.Release();
        }

        var firstOutcome = await firstActor.WaitAsync(
            TimeSpan.FromSeconds(15));

        Assert.Equal(
            PaymentResultApplicationStatus.Accepted,
            firstOutcome.Status);

        Assert.Equal(
            PaymentResultApplicationStatus.Accepted,
            secondOutcome.Status);

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var payment = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == initiationResult.PaymentId);

        var order = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == orderId);

        var inventories = await assertionDb.Inventories
            .AsNoTracking()
            .Where(item =>
                item.ProductVariantId == availableVariantId ||
                item.ProductVariantId == unavailableVariantId)
            .ToDictionaryAsync(item => item.ProductVariantId);

        var recoveryReserves = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item =>
                item.ReferenceType == InventoryReferenceType.Order &&
                item.ReferenceId == orderId &&
                item.Type == InventoryTransactionType.Reserve &&
                item.CreatedAt == succeededAt)
            .ToArrayAsync();

        var recoveryHistories = await assertionDb.OrderStatusHistories
            .AsNoTracking()
            .Where(item =>
                item.OrderId == orderId &&
                item.FromStatus == OrderStatus.Expired &&
                item.ToStatus == OrderStatus.Confirmed)
            .ToArrayAsync();

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Null(payment.RefundIdempotencyKey);
        Assert.Null(payment.RefundRequestedAt);
        Assert.Null(payment.NextRefundAttemptAt);

        Assert.Equal(OrderStatus.Confirmed, order.Status);

        Assert.Equal(2, inventories[availableVariantId].OnHandQuantity);
        Assert.Equal(2, inventories[availableVariantId].ReservedQuantity);
        Assert.Equal(0, inventories[availableVariantId].AvailableQuantity);

        Assert.Equal(2, inventories[unavailableVariantId].OnHandQuantity);
        Assert.Equal(2, inventories[unavailableVariantId].ReservedQuantity);
        Assert.Equal(0, inventories[unavailableVariantId].AvailableQuantity);

        Assert.Equal(2, recoveryReserves.Length);
        Assert.Single(recoveryHistories);
    }

    [Fact]
    [Trait("Requirement", "PAY-RACE-002")]
    public async Task LateSuccessAndCompetingOrder_ForLastStock_OnlyOneAllocationSucceeds()
    {
        var createdAt = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var expiredAt = createdAt.AddMinutes(15);
        var raceAt = expiredAt.AddMinutes(1);
        var userId = Guid.NewGuid();
        var expiredOrderId = Guid.NewGuid();
        var competingOrderId = Guid.NewGuid();
        var clock = new FakeClock(createdAt);
        var gateway = new TimeoutPaymentGateway();

        await using var factory = CreateFactory(
            userId,
            clock,
            paymentGateway: gateway);

        await MigrateAsync(factory);

        await SeedPendingPaymentOrderAsync(
            factory,
            userId,
            expiredOrderId,
            createdAt);

        var productVariantId = await SeedReservationForOrderAsync(
            factory,
            expiredOrderId,
            createdAt,
            quantity: 1,
            initialOnHand: 1);

        var initiation = await ExecuteInitiationAsync(
            factory,
            new InitiatePaymentRequest(
                expiredOrderId,
                Guid.NewGuid(),
                PaymentScenario.Success));

        Assert.True(initiation.IsSuccess);

        var initiationResult =
            Assert.IsType<PaymentInitiationResult>(initiation.Value);

        string providerPaymentId;

        using (var paymentScope = factory.Services.CreateScope())
        {
            var db = paymentScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            providerPaymentId = await db.Payments
                .Where(item => item.Id == initiationResult.PaymentId)
                .Select(item => item.ProviderPaymentId)
                .SingleAsync();
        }

        using (var expirationScope = factory.Services.CreateScope())
        {
            var expirationStore = expirationScope.ServiceProvider
                .GetRequiredService<IReservationExpirationStore>();

            var expirationOutcome = await expirationStore.TryExpireAsync(
                expiredOrderId,
                expiredAt,
                CancellationToken.None);

            Assert.Equal(
                ReservationExpirationOutcome.Expired,
                expirationOutcome);
        }

        clock.UtcNow = raceAt;

        var command = new ApplyPaymentResultCommand(
            providerPaymentId,
            ProviderPaymentOutcome.Succeeded,
            failureCode: null,
            PaymentResultSource.Reconciliation,
            providerEvent: null,
            occurredAt: raceAt);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var lateSuccessActor = ApplyPaymentResultAfterSignalAsync(
            factory,
            command,
            start.Task
        );

        var competingOrderActor = TryCreateCompetingOrderAfterSignalAsync(
            factory,
            start.Task,
            userId,
            competingOrderId,
            productVariantId,
            raceAt
        );

        start.TrySetResult();

        await Task.WhenAll(
                lateSuccessActor,
                competingOrderActor)
            .WaitAsync(TimeSpan.FromSeconds(15));

        var lateSuccessOutcome = await lateSuccessActor;
        var competingReservationResult = await competingOrderActor;

        Assert.Equal(
            PaymentResultApplicationStatus.Accepted,
            lateSuccessOutcome.Status);

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var payment = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == initiationResult.PaymentId);

        var expiredOrder = await assertionDb.Orders
            .AsNoTracking()
            .SingleAsync(item => item.Id == expiredOrderId);

        var competingOrderExists = await assertionDb.Orders
            .AsNoTracking()
            .AnyAsync(item => item.Id == competingOrderId);

        var inventory = await assertionDb.Inventories
            .AsNoTracking()
            .SingleAsync(
                item => item.ProductVariantId == productVariantId);

        var raceReservations = await assertionDb.InventoryTransactions
            .AsNoTracking()
            .Where(item =>
                item.ProductVariantId == productVariantId &&
                item.Type == InventoryTransactionType.Reserve &&
                item.CreatedAt == raceAt)
            .ToArrayAsync();

        var lateSuccessRecovered =
            payment.Status == PaymentStatus.Succeeded;

        var competingOrderReserved =
            competingReservationResult ==
            InventoryReservationResult.Reserved;

        Assert.NotEqual(
            lateSuccessRecovered,
            competingOrderReserved);

        Assert.Equal(
            competingOrderReserved,
            competingOrderExists);

        Assert.Equal(1, inventory.OnHandQuantity);
        Assert.Equal(1, inventory.ReservedQuantity);
        Assert.Equal(0, inventory.AvailableQuantity);

        Assert.Single(raceReservations);

        if (lateSuccessRecovered)
        {
            Assert.Equal(
                InventoryReservationResult.InsufficientStock,
                competingReservationResult);

            Assert.Equal(
                OrderStatus.Confirmed,
                expiredOrder.Status);

            Assert.Null(payment.RefundIdempotencyKey);
            Assert.Null(payment.RefundRequestedAt);
            Assert.Null(payment.NextRefundAttemptAt);

            Assert.Equal(
                expiredOrderId,
                raceReservations[0].ReferenceId);
        }
        else
        {
            Assert.Equal(
                InventoryReservationResult.Reserved,
                competingReservationResult);

            Assert.Equal(
                PaymentStatus.RefundPending,
                payment.Status);

            Assert.Equal(
                OrderStatus.Expired,
                expiredOrder.Status);

            Assert.NotNull(payment.RefundIdempotencyKey);
            Assert.Equal(raceAt, payment.RefundRequestedAt);
            Assert.Equal(raceAt, payment.NextRefundAttemptAt);

            Assert.Equal(
                competingOrderId,
                raceReservations[0].ReferenceId);
        }
    }

    private static async Task AssertSingleSucceededCreateOperationAsync(
        OrderSystemDbContext db,
        Payment payment)
    {
        var operations = await db.FakeProviderOperations
            .AsNoTracking()
            .Where(operation =>
                operation.OperationType == FakeProviderOperationType.CreatePayment &&
                operation.IdempotencyKey == payment.GatewayIdempotencyKey)
            .ToListAsync();

        var operation = Assert.Single(operations);

        Assert.Equal(payment.ProviderPaymentId, operation.ProviderResourceId);
        Assert.Equal(PaymentScenario.Success, operation.Scenario);
        Assert.Equal(FakeProviderOperationStatus.Succeeded, operation.Status);
        Assert.Equal(payment.Amount, operation.Amount);
    }

    private static async Task SeedPendingPaymentOrderAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        Guid orderId,
        DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var email = $"payment-initiation-{userId:N}@example.com";

        db.AddRange(
            new User(
                userId,
                email,
                email,
                "test-password-hash",
                UserRole.Customer,
                now),
            new Order(
                orderId,
                userId,
                totalAmount: 125_000m,
                reservationExpiresAt: now.AddMinutes(15),
                createdAt: now));

        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedReservationForOrderAsync(
        WebApplicationFactory<Program> factory,
        Guid orderId,
        DateTimeOffset now,
        int quantity = 2,
        Guid? productVariantId = null,
        int initialOnHand = 10)
    {
        var productId = Guid.NewGuid();
        var resolvedProductVariantId = productVariantId ?? Guid.NewGuid();

        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

        await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

        var product = new Product(
            productId,
            "Payment failure product",
            "Payment failure integration test product",
            CatalogStatus.Active,
            now);

        var variant = new ProductVariant(
            resolvedProductVariantId,
            productId,
            $"PAY-{Guid.NewGuid():N}"[..16],
            "Payment failure test variant",
            currentPrice: 62_500m,
            CatalogStatus.Active,
            now);

        var inventory = new Inventory(
            Guid.NewGuid(),
            resolvedProductVariantId,
            initialOnHand,
            now);

        db.AddRange(product, variant, inventory);
        await db.SaveChangesAsync();

        var reservation = await store.TryReserveAsync(
            resolvedProductVariantId,
            quantity: quantity,
            updatedAt: now,
            CancellationToken.None);

        Assert.Equal(InventoryReservationResult.Reserved, reservation);

        store.AddOrderItems(
        [
            new OrderItem(
            Guid.NewGuid(),
            orderId,
            resolvedProductVariantId,
            quantity: quantity,
            unitPrice: 62_500m)
        ]);

        store.AddInventoryTransactions(
        [
            new InventoryTransaction(
            Guid.NewGuid(),
            resolvedProductVariantId,
            InventoryTransactionType.Reserve,
            onHandQuantityDelta: 0,
                reservedQuantityDelta: quantity,
                InventoryReferenceType.Order,
                orderId,
                reason: null,
                createdAt: now)
        ]);

        await store.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        return resolvedProductVariantId;
    }

    private static async Task<PaymentResultApplicationOutcome> ApplyPaymentResultAfterSignalAsync(
        WebApplicationFactory<Program> factory,
        ApplyPaymentResultCommand command,
        Task start)
    {
        await start;

        return await ApplyPaymentResultFromIndependentScopeAsync(
            factory,
            command);
    }

    private static async Task<InventoryReservationResult>
    TryCreateCompetingOrderAfterSignalAsync(
        WebApplicationFactory<Program> factory,
        Task start,
        Guid userId,
        Guid orderId,
        Guid productVariantId,
        DateTimeOffset now)
    {
        await start;

        using var scope = factory.Services.CreateScope();

        var store = scope.ServiceProvider
            .GetRequiredService<IOrderCommandStore>();

        await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

        var reservationResult = await store.TryReserveAsync(
            productVariantId,
            quantity: 1,
            updatedAt: now,
            CancellationToken.None);

        if (reservationResult != InventoryReservationResult.Reserved)
        {
            await transaction.RollbackAsync(CancellationToken.None);

            return reservationResult;
        }

        store.AddOrder(
            new Order(
                orderId,
                userId,
                totalAmount: 62_500m,
                reservationExpiresAt: now.AddMinutes(15),
                createdAt: now));

        store.AddOrderItems(
        [
            new OrderItem(
            Guid.NewGuid(),
            orderId,
            productVariantId,
            quantity: 1,
            unitPrice: 62_500m)
        ]);

        store.AddInventoryTransactions(
        [
            new InventoryTransaction(
            Guid.NewGuid(),
            productVariantId,
            InventoryTransactionType.Reserve,
            onHandQuantityDelta: 0,
            reservedQuantityDelta: 1,
            InventoryReferenceType.Order,
            orderId,
            reason: null,
            createdAt: now)
        ]);

        await store.SaveChangesAsync(CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        return reservationResult;
    }

    private static async Task MigrateAsync(
        WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        await db.Database.MigrateAsync();
    }

    private WebApplicationFactory<Program> CreateFactory(
        Guid userId,
        IClock clock,
        IOperationHook? operationHook = null,
        IPaymentGateway? paymentGateway = null,
        bool blockFreshCompensation = false) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>(
                            "Database:ConnectionString",
                            postgres.ConnectionString)));

                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ICurrentUser>();
                    services.AddScoped<ICurrentUser>(
                        _ => new TestCurrentUser(userId));

                    services.RemoveAll<IClock>();
                    services.AddSingleton(clock);

                    if (operationHook is not null)
                    {
                        services.RemoveAll<IOperationHook>();
                        services.AddSingleton<IOperationHook>(operationHook);
                    }
                    if (paymentGateway is not null)
                    {
                        services.RemoveAll<IPaymentGateway>();
                        services.AddSingleton(paymentGateway);
                    }
                    if (blockFreshCompensation)
                    {
                        services.RemoveAll<IPaymentResultApplicationScopeFactory>();

                        services.AddSingleton<
                            BlockingPaymentResultApplicationScopeFactory>();

                        services.AddSingleton<
                            IPaymentResultApplicationScopeFactory>(
                            provider => provider.GetRequiredService<
                                BlockingPaymentResultApplicationScopeFactory>());
                    }
                });
            });

    private static async Task<ApplicationResult<PaymentInitiationResult>> ExecuteInitiationAsync(
        WebApplicationFactory<Program> factory,
        InitiatePaymentRequest request)
    {
        using var scope = factory.Services.CreateScope();

        var service = scope.ServiceProvider
            .GetRequiredService<PaymentCommandService>();

        return await service.InitiateAsync(
            request,
            CancellationToken.None);
    }

    private sealed class PaymentScopedReconciliationStore(
        IPaymentReconciliationStore innerStore,
        Guid paymentId) : IPaymentReconciliationStore
    {
        public async Task<IReadOnlyList<PaymentReconciliationCandidate>> ListDueAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken)
        {
            var candidates = await innerStore.ListDueAsync(now, batchSize, cancellationToken);
            var targetCandidates = candidates
                .Where(candidate => candidate.PaymentId == paymentId)
                .ToArray();

            if (targetCandidates.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one due reconciliation candidate for Payment '{paymentId}', but found {targetCandidates.Length}");
            }

            return targetCandidates;
        }

        public Task RecordStatusCheckAsync(
            Guid candidatePaymentId,
            DateTimeOffset checkedAt,
            CancellationToken cancellationToken) =>
            innerStore.RecordStatusCheckAsync(candidatePaymentId, checkedAt, cancellationToken);

        public Task<bool> IsCreateRedriveEligibleAsync(
            Guid candidatePaymentId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            innerStore.IsCreateRedriveEligibleAsync(candidatePaymentId, now, cancellationToken);
    }

    private sealed class BlockingPaymentGateway : IPaymentGateway
    {
        private readonly TaskCompletionSource reached =
            new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);

        private readonly TaskCompletionSource release =
            new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);

        public Task Reached => reached.Task;

        public CreatePaymentRequest? Request { get; private set; }

        public async Task<CreatePaymentResult>
            CreatePaymentAsync(
                CreatePaymentRequest request,
                CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            Request = request;
            reached.TrySetResult();

            await release.Task.WaitAsync(
                cancellationToken);

            return new CreatePaymentResult(
                ProviderPaymentId:
                    request.ProviderPaymentId,
                Status:
                    PaymentGatewayStatus.Succeeded);
        }

        public Task<PaymentStatusResult> GetStatusAsync(
            string providerPaymentId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<RefundPaymentResult> RefundAsync(
            RefundPaymentRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public void Release()
        {
            release.TrySetResult();
        }
    }

    private sealed class TimeoutPaymentGateway : IPaymentGateway
    {
        private int callCount;
        private int statusQueryCount;

        public int CallCount => Volatile.Read(ref callCount);
        public int StatusQueryCount => Volatile.Read(ref statusQueryCount);

        public CreatePaymentRequest? Request { get; private set; }

        public Task<CreatePaymentResult> CreatePaymentAsync(
            CreatePaymentRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            Request = request;
            Interlocked.Increment(ref callCount);

            throw new TimeoutException("The payment gateway response timed out");
        }

        public Task<PaymentStatusResult> GetStatusAsync(
            string providerPaymentId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref statusQueryCount);
            return Task.FromResult(PaymentStatusResult.NotFound());
        }

        public Task<RefundPaymentResult> RefundAsync(
            RefundPaymentRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class BlockingNotFoundPaymentGateway : IPaymentGateway
    {
        private readonly TaskCompletionSource statusQueryStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseStatusQuery =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int createCallCount;
        private int statusQueryCount;

        public Task StatusQueryStarted => statusQueryStarted.Task;
        public int CreateCallCount => Volatile.Read(ref createCallCount);
        public int StatusQueryCount => Volatile.Read(ref statusQueryCount);

        public Task<CreatePaymentResult> CreatePaymentAsync(
            CreatePaymentRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var currentCallCount = Interlocked.Increment(ref createCallCount);

            if (currentCallCount == 1)
            {
                throw new TimeoutException("The initial provider create timed out");
            }

            throw new InvalidOperationException(
                "Reconciliation must not re-drive provider create after the Order is cancelled");
        }

        public async Task<PaymentStatusResult> GetStatusAsync(
            string providerPaymentId,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(providerPaymentId);
            Interlocked.Increment(ref statusQueryCount);
            statusQueryStarted.TrySetResult();

            await releaseStatusQuery.Task.WaitAsync(cancellationToken);

            return PaymentStatusResult.NotFound();
        }

        public Task<RefundPaymentResult> RefundAsync(
            RefundPaymentRequest request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(
                "Reconciliation must not request a refund before a provider charge exists");
        }

        public void ReleaseStatusQuery()
        {
            releaseStatusQuery.TrySetResult();
        }
    }

    private sealed class ThrowingCheckpointHook(string checkpointToThrow) : IOperationHook
    {
        public Task ReachAsync(string checkpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (checkpoint == checkpointToThrow)
            {
                throw new InvalidOperationException($"Injected failure at '{checkpoint}'");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? UserId => userId;
        public UserRole? Role => UserRole.Customer;
    }

    private static async Task<PaymentResultApplicationOutcome> ApplyPaymentResultFromIndependentScopeAsync(
        WebApplicationFactory<Program> factory,
        ApplyPaymentResultCommand command)
    {
        using var scope = factory.Services.CreateScope();

        var service = scope.ServiceProvider
            .GetRequiredService<PaymentResultApplicationService>();

        return await service.ApplyAsync(
            command,
            CancellationToken.None);
    }

    private sealed class BlockingPaymentResultApplicationScopeFactory(
    IServiceScopeFactory serviceScopeFactory)
    : IPaymentResultApplicationScopeFactory
    {
        private readonly TaskCompletionSource reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int createCount;

        public Task Reached => reached.Task;

        public async ValueTask<IPaymentResultApplicationScope> CreateAsync(
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref createCount) == 1)
            {
                reached.TrySetResult();

                await release.Task.WaitAsync(cancellationToken);
            }

            var serviceScope = serviceScopeFactory.CreateAsyncScope();

            try
            {
                var store = serviceScope.ServiceProvider
                    .GetRequiredService<IPaymentResultApplicationStore>();

                return new PaymentResultApplicationScope(
                    serviceScope,
                    store);
            }
            catch
            {
                await serviceScope.DisposeAsync();
                throw;
            }
        }

        public void Release()
        {
            release.TrySetResult();
        }

        private sealed class PaymentResultApplicationScope(
            AsyncServiceScope serviceScope,
            IPaymentResultApplicationStore store)
            : IPaymentResultApplicationScope
        {
            public IPaymentResultApplicationStore Store { get; } = store;

            public ValueTask DisposeAsync()
            {
                return serviceScope.DisposeAsync();
            }
        }
    }
}
