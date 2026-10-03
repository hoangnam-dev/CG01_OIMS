using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.Infrastructure.Payments.FakeProvider;
using OrderSystem.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using OrderSystem.Infrastructure;
using OrderSystem.Application.Common.Clock;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderSystem.Domain.Payments;

namespace OrderSystem.IntegrationTests.Payments;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class FakePaymentGatewayTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task CreatePaymentAsync_SameKeyAndEquivalentRequestAcrossScopes_ReturnsSameProviderIdentityAndPersistsOneOperation()
    {
        await using var factory = CreateFactory();
        await MigrateAsync(factory);

        var paymentId = Guid.NewGuid();
        var gatewayKey = $"fake-key-{Guid.NewGuid():N}";
        var providerPaymentId = $"fake-pay-{paymentId:D}";

        var request = new CreatePaymentRequest(
            paymentId,
            gatewayKey,
            providerPaymentId,
            125_000m,
            PaymentScenario.Success);

        CreatePaymentResult firstResult;

        using (var firstScope = factory.Services.CreateScope())
        {
            var gateway = firstScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            firstResult = await gateway.CreatePaymentAsync(
                request,
                CancellationToken.None);
        }

        CreatePaymentResult retryResult;

        using (var retryScope = factory.Services.CreateScope())
        {
            var gateway = retryScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            retryResult = await gateway.CreatePaymentAsync(
                request,
                CancellationToken.None);
        }

        Assert.Equal(providerPaymentId, firstResult.ProviderPaymentId);
        Assert.Equal(
            PaymentGatewayStatus.Succeeded,
            firstResult.Status);

        Assert.Equal(
            firstResult.ProviderPaymentId,
            retryResult.ProviderPaymentId);

        Assert.Equal(firstResult.Status, retryResult.Status);

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var rows = await db.FakeProviderOperations
            .AsNoTracking()
            .Where(operation =>
                operation.OperationType == FakeProviderOperationType.CreatePayment &&
                operation.IdempotencyKey == gatewayKey)
            .ToListAsync();

        var persisted = Assert.Single(rows);

        Assert.Equal(providerPaymentId, persisted.ProviderResourceId);
        Assert.Equal(PaymentScenario.Success, persisted.Scenario);
        Assert.Equal(FakeProviderOperationStatus.Succeeded, persisted.Status);
        Assert.Equal(125_000m, persisted.Amount);
        Assert.Null(persisted.ParentProviderPaymentId);
    }

    [Fact]
    public async Task CreatePaymentAsync_SameKeyWithDifferentAmount_ThrowsConflictAndPreservesOriginalOperation()
    {
        await using var factory = CreateFactory();
        await MigrateAsync(factory);

        var paymentId = Guid.NewGuid();
        var gatewayKey = $"fake-key-{Guid.NewGuid():N}";
        var providerPaymentId = $"fake-pay-{paymentId:D}";

        var originalRequest = new CreatePaymentRequest(
            paymentId,
            gatewayKey,
            providerPaymentId,
            125_000m,
            PaymentScenario.Success);

        var conflictingRequest = originalRequest with
        {
            Amount = 125_001m
        };

        using (var setupScope = factory.Services.CreateScope())
        {
            var gateway = setupScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            await gateway.CreatePaymentAsync(
                originalRequest,
                CancellationToken.None);
        }

        using (var conflictScope = factory.Services.CreateScope())
        {
            var gateway = conflictScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            await Assert.ThrowsAsync<FakeProviderOperationConflictException>(
                () => gateway.CreatePaymentAsync(
                    conflictingRequest,
                    CancellationToken.None));
        }

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var rows = await db.FakeProviderOperations
            .AsNoTracking()
            .Where(operation =>
                operation.OperationType ==
                    FakeProviderOperationType.CreatePayment &&
                operation.IdempotencyKey == gatewayKey)
            .ToListAsync();

        var persisted = Assert.Single(rows);

        Assert.Equal(125_000m, persisted.Amount);
        Assert.Equal(providerPaymentId, persisted.ProviderResourceId);
        Assert.Equal(PaymentScenario.Success, persisted.Scenario);
    }

    [Fact]
    public async Task CreatePaymentAsync_ConcurrentEquivalentRequests_PersistsOneOperation()
    {
        await using var factory = CreateFactory();
        await MigrateAsync(factory);

        var request = new CreatePaymentRequest(
            PaymentId: Guid.NewGuid(),
            IdempotencyKey: $"payment-{Guid.NewGuid():N}",
            ProviderPaymentId: $"fake-pay-{Guid.NewGuid():D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.Success);

        await using var scope1 = factory.Services.CreateAsyncScope();
        await using var scope2 = factory.Services.CreateAsyncScope();

        var gateway1 = scope1.ServiceProvider.GetRequiredService<IPaymentGateway>();
        var gateway2 = scope2.ServiceProvider.GetRequiredService<IPaymentGateway>();

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var call1 = Task.Run(async () =>
        {
            await start.Task;
            return await gateway1.CreatePaymentAsync(request, CancellationToken.None);
        });

        var call2 = Task.Run(async () =>
        {
            await start.Task;
            return await gateway2.CreatePaymentAsync(request, CancellationToken.None);
        });

        start.SetResult();

        var results = await Task.WhenAll(call1, call2);

        Assert.All(results, result =>
        {
            Assert.Equal(request.ProviderPaymentId, result.ProviderPaymentId);
            Assert.Equal(PaymentGatewayStatus.Succeeded, result.Status);
        });

        await using var assertionScope = factory.Services.CreateAsyncScope();
        var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var operationCount = await dbContext.FakeProviderOperations.CountAsync(
            item => item.OperationType == FakeProviderOperationType.CreatePayment
                && item.IdempotencyKey == request.IdempotencyKey);

        Assert.Equal(1, operationCount);
    }

    [Fact]
    public async Task CreatePaymentAsync_ConcurrentRequestsWithDifferentAmount_OneSucceedsAndOneConflicts()
    {
        await using var factory = CreateFactory();
        await MigrateAsync(factory);

        var paymentId = Guid.NewGuid();

        var winningRequest = new CreatePaymentRequest(
            PaymentId: paymentId,
            IdempotencyKey: $"payment-{Guid.NewGuid():N}",
            ProviderPaymentId: $"fake-pay-{paymentId:D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.Success);

        var conflictingRequest = winningRequest with
        {
            Amount = 125_001m
        };

        await using var scope1 = factory.Services.CreateAsyncScope();
        await using var scope2 = factory.Services.CreateAsyncScope();

        var gateway1 = scope1.ServiceProvider.GetRequiredService<IPaymentGateway>();
        var gateway2 = scope2.ServiceProvider.GetRequiredService<IPaymentGateway>();

        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var firstCall = CaptureCreateAsync(
            gateway1,
            winningRequest,
            start.Task);

        var secondCall = CaptureCreateAsync(
            gateway2,
            conflictingRequest,
            start.Task);

        start.SetResult();

        var outcomes = await Task.WhenAll(firstCall, secondCall);

        var success = Assert.Single(outcomes, outcome => outcome.Result is not null);
        var conflict = Assert.Single(outcomes, outcome =>
            outcome.Exception is FakeProviderOperationConflictException);

        Assert.NotNull(success.Result);
        Assert.Equal(
            winningRequest.ProviderPaymentId,
            success.Result.ProviderPaymentId);
        Assert.Equal(PaymentGatewayStatus.Succeeded, success.Result.Status);

        Assert.Null(conflict.Result);
        Assert.IsType<FakeProviderOperationConflictException>(conflict.Exception);

        await using var assertionScope = factory.Services.CreateAsyncScope();

        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var rows = await db.FakeProviderOperations
            .AsNoTracking()
            .Where(operation =>
                operation.OperationType == FakeProviderOperationType.CreatePayment &&
                operation.IdempotencyKey == winningRequest.IdempotencyKey)
            .ToListAsync();

        var persisted = Assert.Single(rows);

        Assert.Contains(
            persisted.Amount,
            new[] { winningRequest.Amount, conflictingRequest.Amount });

        Assert.Equal(
            winningRequest.ProviderPaymentId,
            persisted.ProviderResourceId);
        Assert.Equal(PaymentScenario.Success, persisted.Scenario);
        Assert.Equal(FakeProviderOperationStatus.Succeeded, persisted.Status);
    }

    [Fact]
    public async Task GetStatusAsync_FromWorkerOwnedProvider_ReturnsApiPersistedProviderStatus()
    {
        await using var apiFactory = CreateFactory();
        await MigrateAsync(apiFactory);

        var paymentId = Guid.NewGuid();

        var request = new CreatePaymentRequest(
            PaymentId: paymentId,
            IdempotencyKey: $"payment-{Guid.NewGuid():N}",
            ProviderPaymentId: $"fake-pay-{paymentId:D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.Success);

        CreatePaymentResult createResult;

        using (var apiScope = apiFactory.Services.CreateScope())
        {
            var apiGateway = apiScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            createResult = await apiGateway.CreatePaymentAsync(
                request,
                CancellationToken.None);
        }

        await using var workerProvider = CreateWorkerOwnedProvider();

        using var workerScope = workerProvider.CreateScope();

        var workerGateway = workerScope.ServiceProvider
            .GetRequiredService<IPaymentGateway>();

        var status = await workerGateway.GetStatusAsync(
            createResult.ProviderPaymentId,
            CancellationToken.None);

        Assert.Equal(PaymentStatusQueryOutcome.Found, status.Outcome);
        Assert.Equal(createResult.ProviderPaymentId, status.ProviderPaymentId);
        Assert.Equal(PaymentGatewayStatus.Succeeded, status.Status);
        Assert.Null(status.FailureCode);
    }

    [Fact]
    public async Task CreatePaymentAsync_AfterHostRestartWithSameKey_ReturnsExistingProviderOperation()
    {
        var paymentId = Guid.NewGuid();

        var request = new CreatePaymentRequest(
            PaymentId: paymentId,
            IdempotencyKey: $"payment-{Guid.NewGuid():N}",
            ProviderPaymentId: $"fake-pay-{paymentId:D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.Success);

        CreatePaymentResult initialResult;

        await using (var initialFactory = CreateFactory())
        {
            await MigrateAsync(initialFactory);

            using var initialScope = initialFactory.Services.CreateScope();

            var initialGateway = initialScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            initialResult = await initialGateway.CreatePaymentAsync(
                request,
                CancellationToken.None);
        }

        await using var restartedFactory = CreateFactory();
        await MigrateAsync(restartedFactory);

        CreatePaymentResult retryResult;

        using (var retryScope = restartedFactory.Services.CreateScope())
        {
            var restartedGateway = retryScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            retryResult = await restartedGateway.CreatePaymentAsync(
                request,
                CancellationToken.None);
        }

        Assert.Equal(
            initialResult.ProviderPaymentId,
            retryResult.ProviderPaymentId);
        Assert.Equal(
            PaymentGatewayStatus.Succeeded,
            retryResult.Status);

        using var assertionScope = restartedFactory.Services.CreateScope();

        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var rows = await db.FakeProviderOperations
            .AsNoTracking()
            .Where(operation =>
                operation.OperationType == FakeProviderOperationType.CreatePayment &&
                operation.IdempotencyKey == request.IdempotencyKey)
            .ToListAsync();

        var persisted = Assert.Single(rows);

        Assert.Equal(
            initialResult.ProviderPaymentId,
            persisted.ProviderResourceId);
        Assert.Equal(
            FakeProviderOperationStatus.Succeeded,
            persisted.Status);
    }

    [Fact]
    public async Task CreatePaymentAsync_AfterHostRestartWithSameKey_ReturnsExistingProviderOperationAndStatus()
    {
        var paymentId = Guid.NewGuid();

        var request = new CreatePaymentRequest(
            PaymentId: paymentId,
            IdempotencyKey: $"payment-{Guid.NewGuid():N}",
            ProviderPaymentId: $"fake-pay-{paymentId:D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.Success);

        CreatePaymentResult initialResult;

        await using (var initialFactory = CreateFactory())
        {
            await MigrateAsync(initialFactory);

            using var initialScope = initialFactory.Services.CreateScope();

            var initialGateway = initialScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            initialResult = await initialGateway.CreatePaymentAsync(
                request,
                CancellationToken.None);
        }

        await using var restartedFactory = CreateFactory();
        await MigrateAsync(restartedFactory);

        CreatePaymentResult retryResult;
        PaymentStatusResult statusResult;

        using (var restartedScope = restartedFactory.Services.CreateScope())
        {
            var restartedGateway = restartedScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            retryResult = await restartedGateway.CreatePaymentAsync(
                request,
                CancellationToken.None);

            statusResult = await restartedGateway.GetStatusAsync(
                initialResult.ProviderPaymentId,
                CancellationToken.None);
        }

        Assert.Equal(
            initialResult.ProviderPaymentId,
            retryResult.ProviderPaymentId);
        Assert.Equal(
            PaymentGatewayStatus.Succeeded,
            retryResult.Status);

        Assert.Equal(PaymentStatusQueryOutcome.Found, statusResult.Outcome);
        Assert.Equal(
            initialResult.ProviderPaymentId,
            statusResult.ProviderPaymentId);
        Assert.Equal(PaymentGatewayStatus.Succeeded, statusResult.Status);
        Assert.Null(statusResult.FailureCode);

        using var assertionScope = restartedFactory.Services.CreateScope();

        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var rows = await db.FakeProviderOperations
            .AsNoTracking()
            .Where(operation =>
                operation.OperationType == FakeProviderOperationType.CreatePayment &&
                operation.IdempotencyKey == request.IdempotencyKey)
            .ToListAsync();

        var persisted = Assert.Single(rows);

        Assert.Equal(
            initialResult.ProviderPaymentId,
            persisted.ProviderResourceId);
        Assert.Equal(
            FakeProviderOperationStatus.Succeeded,
            persisted.Status);
    }

    [Fact]
    public async Task RefundAsync_AfterHostRestartWithSameKey_ReturnsExistingProviderOperation()
    {
        var paymentId = Guid.NewGuid();
        var refundId = Guid.NewGuid();

        var createRequest = new CreatePaymentRequest(
            PaymentId: paymentId,
            IdempotencyKey: $"payment-{Guid.NewGuid():N}",
            ProviderPaymentId: $"fake-pay-{paymentId:D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.Success);

        var refundRequest = new RefundPaymentRequest(
            IdempotencyKey: $"refund-{Guid.NewGuid():N}",
            ProviderRefundId: $"fake-refund-{refundId:D}",
            ParentProviderPaymentId: createRequest.ProviderPaymentId,
            Amount: createRequest.Amount,
            Scenario: PaymentScenario.Success);

        RefundPaymentResult initialRefund;

        await using (var initialFactory = CreateFactory())
        {
            await MigrateAsync(initialFactory);

            using var initialScope = initialFactory.Services.CreateScope();

            var gateway = initialScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            await gateway.CreatePaymentAsync(
                createRequest,
                CancellationToken.None);

            initialRefund = await gateway.RefundAsync(
                refundRequest,
                CancellationToken.None);
        }

        await using var restartedFactory = CreateFactory();
        await MigrateAsync(restartedFactory);

        RefundPaymentResult retryRefund;

        using (var restartedScope = restartedFactory.Services.CreateScope())
        {
            var gateway = restartedScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            retryRefund = await gateway.RefundAsync(
                refundRequest,
                CancellationToken.None);
        }

        Assert.Equal(
            initialRefund.ProviderRefundId,
            retryRefund.ProviderRefundId);
        Assert.Equal(PaymentGatewayStatus.Succeeded, retryRefund.Status);

        using var assertionScope = restartedFactory.Services.CreateScope();

        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var rows = await db.FakeProviderOperations
            .AsNoTracking()
            .Where(operation =>
                operation.OperationType == FakeProviderOperationType.RefundPayment &&
                operation.IdempotencyKey == refundRequest.IdempotencyKey)
            .ToListAsync();

        var persisted = Assert.Single(rows);

        Assert.Equal(refundRequest.ProviderRefundId, persisted.ProviderResourceId);
        Assert.Equal(
            refundRequest.ParentProviderPaymentId,
            persisted.ParentProviderPaymentId);
        Assert.Equal(refundRequest.Amount, persisted.Amount);
        Assert.Equal(PaymentScenario.Success, persisted.Scenario);
        Assert.Equal(FakeProviderOperationStatus.Succeeded, persisted.Status);
    }

    [Fact]
    public async Task CreatePaymentAsync_FailedScenario_PersistsFailedOperationAndReturnsFailureCode()
    {
        const string ExpectedFailureCode = "DECLINED";

        await using var factory = CreateFactory();
        await MigrateAsync(factory);

        var paymentId = Guid.NewGuid();

        var request = new CreatePaymentRequest(
            PaymentId: paymentId,
            IdempotencyKey: $"payment-{Guid.NewGuid():N}",
            ProviderPaymentId: $"fake-pay-{paymentId:D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.Failed);

        CreatePaymentResult result;

        using (var requestScope = factory.Services.CreateScope())
        {
            var gateway = requestScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            result = await gateway.CreatePaymentAsync(
                request,
                CancellationToken.None);
        }

        Assert.Equal(request.ProviderPaymentId, result.ProviderPaymentId);
        Assert.Equal(PaymentGatewayStatus.Failed, result.Status);
        Assert.Equal(ExpectedFailureCode, result.FailureCode);

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var rows = await db.FakeProviderOperations
            .AsNoTracking()
            .Where(operation =>
                operation.OperationType == FakeProviderOperationType.CreatePayment &&
                operation.IdempotencyKey == request.IdempotencyKey)
            .ToListAsync();

        var persisted = Assert.Single(rows);

        Assert.Equal(request.ProviderPaymentId, persisted.ProviderResourceId);
        Assert.Equal(PaymentScenario.Failed, persisted.Scenario);
        Assert.Equal(FakeProviderOperationStatus.Failed, persisted.Status);
        Assert.Equal(request.Amount, persisted.Amount);
        Assert.Null(persisted.ParentProviderPaymentId);
    }

    [Fact]
    public async Task GetStatusAsync_AfterHostRestartForFailedPayment_ReturnsFailureCode()
    {
        const string ExpectedFailureCode = "DECLINED";

        var paymentId = Guid.NewGuid();

        var request = new CreatePaymentRequest(
            PaymentId: paymentId,
            IdempotencyKey: $"payment-{Guid.NewGuid():N}",
            ProviderPaymentId: $"fake-pay-{paymentId:D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.Failed);

        await using (var initialFactory = CreateFactory())
        {
            await MigrateAsync(initialFactory);

            using var initialScope = initialFactory.Services.CreateScope();

            var gateway = initialScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            var createResult = await gateway.CreatePaymentAsync(
                request,
                CancellationToken.None);

            Assert.Equal(PaymentGatewayStatus.Failed, createResult.Status);
            Assert.Equal(ExpectedFailureCode, createResult.FailureCode);
        }

        await using var restartedFactory = CreateFactory();
        await MigrateAsync(restartedFactory);

        PaymentStatusResult statusResult;

        using (var restartedScope = restartedFactory.Services.CreateScope())
        {
            var gateway = restartedScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            statusResult = await gateway.GetStatusAsync(
                request.ProviderPaymentId,
                CancellationToken.None);
        }

        Assert.Equal(PaymentStatusQueryOutcome.Found, statusResult.Outcome);
        Assert.Equal(request.ProviderPaymentId, statusResult.ProviderPaymentId);
        Assert.Equal(PaymentGatewayStatus.Failed, statusResult.Status);
        Assert.Equal(ExpectedFailureCode, statusResult.FailureCode);
    }

    [Fact]
    public async Task RefundAsync_FailedScenario_PersistsFailedOperationAndReturnsFailureCode()
    {
        const string ExpectedFailureCode = "DECLINED";

        await using var factory = CreateFactory();
        await MigrateAsync(factory);

        var paymentId = Guid.NewGuid();
        var refundId = Guid.NewGuid();

        var createRequest = new CreatePaymentRequest(
            PaymentId: paymentId,
            IdempotencyKey: $"payment-{Guid.NewGuid():N}",
            ProviderPaymentId: $"fake-pay-{paymentId:D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.Success);

        var refundRequest = new RefundPaymentRequest(
            IdempotencyKey: $"refund-{Guid.NewGuid():N}",
            ProviderRefundId: $"fake-refund-{refundId:D}",
            ParentProviderPaymentId: createRequest.ProviderPaymentId,
            Amount: createRequest.Amount,
            Scenario: PaymentScenario.Failed);

        RefundPaymentResult result;

        using (var requestScope = factory.Services.CreateScope())
        {
            var gateway = requestScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            await gateway.CreatePaymentAsync(
                createRequest,
                CancellationToken.None);

            result = await gateway.RefundAsync(
                refundRequest,
                CancellationToken.None);
        }

        Assert.Equal(refundRequest.ProviderRefundId, result.ProviderRefundId);
        Assert.Equal(PaymentGatewayStatus.Failed, result.Status);
        Assert.Equal(ExpectedFailureCode, result.FailureCode);

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var rows = await db.FakeProviderOperations
            .AsNoTracking()
            .Where(operation =>
                operation.OperationType == FakeProviderOperationType.RefundPayment &&
                operation.IdempotencyKey == refundRequest.IdempotencyKey)
            .ToListAsync();

        var persisted = Assert.Single(rows);

        Assert.Equal(refundRequest.ProviderRefundId, persisted.ProviderResourceId);
        Assert.Equal(
            refundRequest.ParentProviderPaymentId,
            persisted.ParentProviderPaymentId);
        Assert.Equal(PaymentScenario.Failed, persisted.Scenario);
        Assert.Equal(FakeProviderOperationStatus.Failed, persisted.Status);
        Assert.Equal(refundRequest.Amount, persisted.Amount);
    }

    [Fact]
    public async Task CreatePaymentAsync_ResponseLostAfterProviderCommit_RetryReturnsPersistedSuccess()
    {
        await using var factory = CreateFactory();
        await MigrateAsync(factory);

        var paymentId = Guid.NewGuid();

        var request = new CreatePaymentRequest(
            PaymentId: paymentId,
            IdempotencyKey: $"payment-{Guid.NewGuid():N}",
            ProviderPaymentId: $"fake-pay-{paymentId:D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.SuccessButResponseLost);

        using (var initialScope = factory.Services.CreateScope())
        {
            var gateway = initialScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            await Assert.ThrowsAsync<PaymentGatewayResponseLostException>(
                () => gateway.CreatePaymentAsync(
                    request,
                    CancellationToken.None));
        }

        using (var committedStateScope = factory.Services.CreateScope())
        {
            var db = committedStateScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var committedRows = await db.FakeProviderOperations
                .AsNoTracking()
                .Where(operation =>
                    operation.OperationType ==
                        FakeProviderOperationType.CreatePayment &&
                    operation.IdempotencyKey == request.IdempotencyKey)
                .ToListAsync();

            var committed = Assert.Single(committedRows);

            Assert.Equal(request.ProviderPaymentId, committed.ProviderResourceId);
            Assert.Equal(
                PaymentScenario.SuccessButResponseLost,
                committed.Scenario);
            Assert.Equal(
                FakeProviderOperationStatus.Succeeded,
                committed.Status);
        }

        CreatePaymentResult retryResult;

        using (var retryScope = factory.Services.CreateScope())
        {
            var gateway = retryScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            retryResult = await gateway.CreatePaymentAsync(
                request,
                CancellationToken.None);
        }

        Assert.Equal(request.ProviderPaymentId, retryResult.ProviderPaymentId);
        Assert.Equal(PaymentGatewayStatus.Succeeded, retryResult.Status);
        Assert.Null(retryResult.FailureCode);

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var operationCount = await assertionDb.FakeProviderOperations
            .AsNoTracking()
            .CountAsync(operation =>
                operation.OperationType ==
                    FakeProviderOperationType.CreatePayment &&
                operation.IdempotencyKey == request.IdempotencyKey);

        Assert.Equal(1, operationCount);
    }

    [Fact]
    public async Task RefundAsync_ResponseLostAfterProviderCommit_RetryReturnsPersistedSuccess()
    {
        await using var factory = CreateFactory();
        await MigrateAsync(factory);

        var paymentId = Guid.NewGuid();
        var refundId = Guid.NewGuid();

        var createRequest = new CreatePaymentRequest(
            PaymentId: paymentId,
            IdempotencyKey: $"payment-{Guid.NewGuid():N}",
            ProviderPaymentId: $"fake-pay-{paymentId:D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.Success);

        var refundRequest = new RefundPaymentRequest(
            IdempotencyKey: $"refund-{Guid.NewGuid():N}",
            ProviderRefundId: $"fake-refund-{refundId:D}",
            ParentProviderPaymentId: createRequest.ProviderPaymentId,
            Amount: createRequest.Amount,
            Scenario: PaymentScenario.SuccessButResponseLost);

        using (var initialScope = factory.Services.CreateScope())
        {
            var gateway = initialScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            await gateway.CreatePaymentAsync(
                createRequest,
                CancellationToken.None);

            await Assert.ThrowsAsync<PaymentGatewayResponseLostException>(
                () => gateway.RefundAsync(
                    refundRequest,
                    CancellationToken.None));
        }

        using (var committedStateScope = factory.Services.CreateScope())
        {
            var db = committedStateScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var committedRows = await db.FakeProviderOperations
                .AsNoTracking()
                .Where(operation =>
                    operation.OperationType ==
                        FakeProviderOperationType.RefundPayment &&
                    operation.IdempotencyKey == refundRequest.IdempotencyKey)
                .ToListAsync();

            var committed = Assert.Single(committedRows);

            Assert.Equal(
                refundRequest.ProviderRefundId,
                committed.ProviderResourceId);
            Assert.Equal(
                refundRequest.ParentProviderPaymentId,
                committed.ParentProviderPaymentId);
            Assert.Equal(
                PaymentScenario.SuccessButResponseLost,
                committed.Scenario);
            Assert.Equal(
                FakeProviderOperationStatus.Succeeded,
                committed.Status);
        }

        RefundPaymentResult retryResult;

        using (var retryScope = factory.Services.CreateScope())
        {
            var gateway = retryScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            retryResult = await gateway.RefundAsync(
                refundRequest,
                CancellationToken.None);
        }

        Assert.Equal(
            refundRequest.ProviderRefundId,
            retryResult.ProviderRefundId);
        Assert.Equal(PaymentGatewayStatus.Succeeded, retryResult.Status);
        Assert.Null(retryResult.FailureCode);

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var operationCount = await assertionDb.FakeProviderOperations
            .AsNoTracking()
            .CountAsync(operation =>
                operation.OperationType ==
                    FakeProviderOperationType.RefundPayment &&
                operation.IdempotencyKey == refundRequest.IdempotencyKey);

        Assert.Equal(1, operationCount);
    }

    [Fact]
    public async Task CreatePaymentAsync_DelayedSuccess_RemainsProcessingUntilAvailableThenSucceeds()
    {
        var now = new DateTimeOffset(
            2026,
            10,
            1,
            12,
            0,
            0,
            TimeSpan.Zero);

        var clock = new FakeClock(now);

        await using var factory = CreateFactory(clock);
        await MigrateAsync(factory);

        var paymentId = Guid.NewGuid();

        var request = new CreatePaymentRequest(
            PaymentId: paymentId,
            IdempotencyKey: $"payment-{Guid.NewGuid():N}",
            ProviderPaymentId: $"fake-pay-{paymentId:D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.DelayedSuccess);

        CreatePaymentResult initialResult;

        using (var createScope = factory.Services.CreateScope())
        {
            var gateway = createScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            initialResult = await gateway.CreatePaymentAsync(
                request,
                CancellationToken.None);
        }

        Assert.Equal(request.ProviderPaymentId, initialResult.ProviderPaymentId);
        Assert.Equal(PaymentGatewayStatus.Processing, initialResult.Status);
        Assert.Null(initialResult.FailureCode);

        DateTimeOffset availableAt;

        using (var persistedStateScope = factory.Services.CreateScope())
        {
            var db = persistedStateScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var persisted = await db.FakeProviderOperations
                .AsNoTracking()
                .SingleAsync(operation =>
                    operation.OperationType ==
                        FakeProviderOperationType.CreatePayment &&
                    operation.IdempotencyKey == request.IdempotencyKey);

            Assert.Equal(PaymentScenario.DelayedSuccess, persisted.Scenario);
            Assert.Equal(FakeProviderOperationStatus.Processing, persisted.Status);

            availableAt = Assert.IsType<DateTimeOffset>(persisted.AvailableAt);

            Assert.True(availableAt > now);
        }

        PaymentStatusResult beforeAvailableResult;

        using (var beforeAvailableScope = factory.Services.CreateScope())
        {
            var gateway = beforeAvailableScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            beforeAvailableResult = await gateway.GetStatusAsync(
                request.ProviderPaymentId,
                CancellationToken.None);
        }

        Assert.Equal(PaymentStatusQueryOutcome.Found, beforeAvailableResult.Outcome);
        Assert.Equal(PaymentGatewayStatus.Processing, beforeAvailableResult.Status);
        Assert.Null(beforeAvailableResult.FailureCode);

        clock.UtcNow = availableAt;

        PaymentStatusResult availableResult;

        using (var availableScope = factory.Services.CreateScope())
        {
            var gateway = availableScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            availableResult = await gateway.GetStatusAsync(
                request.ProviderPaymentId,
                CancellationToken.None);
        }

        Assert.Equal(PaymentStatusQueryOutcome.Found, availableResult.Outcome);
        Assert.Equal(PaymentGatewayStatus.Succeeded, availableResult.Status);
        Assert.Null(availableResult.FailureCode);

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var completed = await assertionDb.FakeProviderOperations
            .AsNoTracking()
            .SingleAsync(operation =>
                operation.OperationType ==
                    FakeProviderOperationType.CreatePayment &&
                operation.IdempotencyKey == request.IdempotencyKey);

        Assert.Equal(FakeProviderOperationStatus.Succeeded, completed.Status);
        Assert.Equal(availableAt, completed.UpdatedAt);
    }

    [Fact]
    public async Task GetStatusAsync_AfterHostRestartForDelayedPayment_TransitionsAtPersistedAvailableAt()
    {
        var now = new DateTimeOffset(
            2026,
            10,
            1,
            12,
            0,
            0,
            TimeSpan.Zero);

        var clock = new FakeClock(now);
        var paymentId = Guid.NewGuid();

        var request = new CreatePaymentRequest(
            PaymentId: paymentId,
            IdempotencyKey: $"payment-{Guid.NewGuid():N}",
            ProviderPaymentId: $"fake-pay-{paymentId:D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.DelayedSuccess);

        DateTimeOffset availableAt;

        await using (var initialFactory = CreateFactory(clock))
        {
            await MigrateAsync(initialFactory);

            using (var createScope = initialFactory.Services.CreateScope())
            {
                var gateway = createScope.ServiceProvider
                    .GetRequiredService<IPaymentGateway>();

                var createResult = await gateway.CreatePaymentAsync(
                    request,
                    CancellationToken.None);

                Assert.Equal(PaymentGatewayStatus.Processing, createResult.Status);
            }

            using var persistedStateScope = initialFactory.Services.CreateScope();

            var db = persistedStateScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var operation = await db.FakeProviderOperations
                .AsNoTracking()
                .SingleAsync(item =>
                    item.OperationType == FakeProviderOperationType.CreatePayment &&
                    item.IdempotencyKey == request.IdempotencyKey);

            availableAt = Assert.IsType<DateTimeOffset>(operation.AvailableAt);
            Assert.Equal(FakeProviderOperationStatus.Processing, operation.Status);
        }

        clock.UtcNow = availableAt;

        await using var restartedFactory = CreateFactory(clock);

        using (var restartedScope = restartedFactory.Services.CreateScope())
        {
            var gateway = restartedScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            var statusResult = await gateway.GetStatusAsync(
                request.ProviderPaymentId,
                CancellationToken.None);

            Assert.Equal(PaymentStatusQueryOutcome.Found, statusResult.Outcome);
            Assert.Equal(PaymentGatewayStatus.Succeeded, statusResult.Status);
            Assert.Null(statusResult.FailureCode);
        }

        using var assertionScope = restartedFactory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var completed = await assertionDb.FakeProviderOperations
            .AsNoTracking()
            .SingleAsync(item =>
                item.OperationType == FakeProviderOperationType.CreatePayment &&
                item.IdempotencyKey == request.IdempotencyKey);

        Assert.Equal(FakeProviderOperationStatus.Succeeded, completed.Status);
        Assert.Equal(availableAt, completed.UpdatedAt);
    }

    [Fact]
    public async Task RefundAsync_DelayedSuccess_PersistsProcessingOperation()
    {
        var now = new DateTimeOffset(
            2026,
            10,
            1,
            12,
            0,
            0,
            TimeSpan.Zero);

        var clock = new FakeClock(now);

        await using var factory = CreateFactory(clock);
        await MigrateAsync(factory);

        var paymentId = Guid.NewGuid();
        var refundId = Guid.NewGuid();

        var createRequest = new CreatePaymentRequest(
            PaymentId: paymentId,
            IdempotencyKey: $"payment-{Guid.NewGuid():N}",
            ProviderPaymentId: $"fake-pay-{paymentId:D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.Success);

        var refundRequest = new RefundPaymentRequest(
            IdempotencyKey: $"refund-{Guid.NewGuid():N}",
            ProviderRefundId: $"fake-refund-{refundId:D}",
            ParentProviderPaymentId: createRequest.ProviderPaymentId,
            Amount: createRequest.Amount,
            Scenario: PaymentScenario.DelayedSuccess);

        RefundPaymentResult result;

        using (var requestScope = factory.Services.CreateScope())
        {
            var gateway = requestScope.ServiceProvider
                .GetRequiredService<IPaymentGateway>();

            await gateway.CreatePaymentAsync(
                createRequest,
                CancellationToken.None);

            result = await gateway.RefundAsync(
                refundRequest,
                CancellationToken.None);
        }

        Assert.Equal(refundRequest.ProviderRefundId, result.ProviderRefundId);
        Assert.Equal(PaymentGatewayStatus.Processing, result.Status);
        Assert.Null(result.FailureCode);

        using var assertionScope = factory.Services.CreateScope();

        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var operation = await db.FakeProviderOperations
            .AsNoTracking()
            .SingleAsync(item =>
                item.OperationType == FakeProviderOperationType.RefundPayment &&
                item.IdempotencyKey == refundRequest.IdempotencyKey);

        Assert.Equal(refundRequest.ProviderRefundId, operation.ProviderResourceId);
        Assert.Equal(
            refundRequest.ParentProviderPaymentId,
            operation.ParentProviderPaymentId);
        Assert.Equal(PaymentScenario.DelayedSuccess, operation.Scenario);
        Assert.Equal(FakeProviderOperationStatus.Processing, operation.Status);
        Assert.Equal(refundRequest.Amount, operation.Amount);

        var availableAt = Assert.IsType<DateTimeOffset>(operation.AvailableAt);

        Assert.True(availableAt > now);
    }

    private sealed record GatewayCallOutcome(CreatePaymentResult? Result, Exception? Exception);

    private ServiceProvider CreateWorkerOwnedProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:ConnectionString"] = postgres.ConnectionString,
                    ["Payment:ReconciliationInterval"] = "00:01:00",
                    ["Payment:ReconciliationBatchSize"] = "100"
                })
            .Build();

        return new ServiceCollection()
            .AddOrderSystemPersistence(configuration)
            .AddOrderSystemCommonInfrastructure()
            .AddOrderSystemPaymentInfrastructure(configuration)
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true
            });
    }

    private WebApplicationFactory<Program> CreateFactory(
    IClock? clock = null) =>
    new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>(
                        "Database:ConnectionString",
                        postgres.ConnectionString)));

            if (clock is not null)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IClock>();
                    services.AddSingleton(clock);
                });
            }
        });

    private static async Task MigrateAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        await db.Database.MigrateAsync();
    }

    private static async Task<GatewayCallOutcome> CaptureCreateAsync(
        IPaymentGateway gateway,
        CreatePaymentRequest request,
        Task startSignal
    )
    {
        try
        {
            await startSignal;

            var result = await gateway.CreatePaymentAsync(
                request,
                CancellationToken.None);

            return new GatewayCallOutcome(result, null);
        }
        catch (Exception exception)
        {
            return new GatewayCallOutcome(null, exception);
        }
    }
}
