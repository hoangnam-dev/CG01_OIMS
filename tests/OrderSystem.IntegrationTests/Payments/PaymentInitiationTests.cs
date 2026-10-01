using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Idempotency;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
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
        Assert.Equal(PaymentStatus.Pending, firstResult.Status);
        Assert.Equal(PaymentStatus.Pending, replayResult.Status);
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
        Assert.Equal(PaymentStatus.Pending, payment.Status);
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
        Assert.Equal(PaymentStatus.Pending, payment.Status);

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
            PaymentStatus.Pending,
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
        Assert.Equal(PaymentStatus.Pending, payment.Status);

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
            PaymentStatus.Pending,
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
        Assert.Equal(PaymentStatus.Pending, payment.Status);

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
            PaymentStatus.Pending,
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
    IPaymentGateway? paymentGateway = null) =>
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
            });
        });

    private static async Task<ApplicationResult<PaymentInitiationResult>>
    ExecuteInitiationAsync(
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

    private sealed class BlockingPaymentGateway
    : IPaymentGateway
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

        public int CallCount => Volatile.Read(ref callCount);

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
            throw new NotSupportedException();
        }

        public Task<RefundPaymentResult> RefundAsync(
            RefundPaymentRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
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
}
