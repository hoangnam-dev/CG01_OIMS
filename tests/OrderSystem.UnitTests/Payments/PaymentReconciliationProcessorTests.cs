using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;

namespace OrderSystem.UnitTests.Payments;

public sealed class PaymentReconciliationProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(PaymentGatewayStatus.Pending)]
    [InlineData(PaymentGatewayStatus.Processing)]
    public async Task RunOnceAsync_WhenProviderRemainsUnresolved_RecordsCheckWithoutAuthoritativeTransition(
        PaymentGatewayStatus providerStatus)
    {
        var candidate = new PaymentReconciliationCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PaymentStatus.Pending,
            $"fake-pay-{Guid.NewGuid():N}",
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-2),
            null);
        var store = new RecordingReconciliationStore([candidate]);
        var gateway = new RecordingPaymentGateway(
            PaymentStatusResult.Found(candidate.ProviderPaymentId, providerStatus));
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            store,
            gateway,
            CreateResultApplicationService(),
            batchSize: 100,
            NullLogger<PaymentReconciliationProcessor>.Instance);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal([candidate.ProviderPaymentId], gateway.StatusQueries);
        Assert.Equal([(candidate.PaymentId, Now)], store.RecordedChecks);
    }

    [Fact]
    public async Task RunOnceAsync_WhenProviderResponseIsLost_RecordsCheckWithoutThrowing()
    {
        var candidate = new PaymentReconciliationCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PaymentStatus.Pending,
            $"fake-pay-{Guid.NewGuid():N}",
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-2),
            null);
        var store = new RecordingReconciliationStore([candidate]);
        var gateway = new RecordingPaymentGateway(new PaymentGatewayResponseLostException("Provider response was lost"));
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            store,
            gateway,
            CreateResultApplicationService(),
            batchSize: 100,
            NullLogger<PaymentReconciliationProcessor>.Instance);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal([candidate.ProviderPaymentId], gateway.StatusQueries);
        Assert.Equal([(candidate.PaymentId, Now)], store.RecordedChecks);
    }

    [Fact]
    public async Task RunOnceAsync_WhenProviderStatusQueryTimesOut_RecordsCheckWithoutThrowing()
    {
        var candidate = new PaymentReconciliationCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PaymentStatus.Pending,
            $"fake-pay-{Guid.NewGuid():N}",
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-2),
            null);
        var store = new RecordingReconciliationStore([candidate]);
        var gateway = new RecordingPaymentGateway(new TimeoutException("Provider status query timed out"));
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            store,
            gateway,
            CreateResultApplicationService(),
            batchSize: 100,
            NullLogger<PaymentReconciliationProcessor>.Instance);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal([candidate.ProviderPaymentId], gateway.StatusQueries);
        Assert.Equal([(candidate.PaymentId, Now)], store.RecordedChecks);
    }

    [Fact]
    public async Task RunOnceAsync_WhenProviderSucceeded_AppliesAuthoritativeResultThroughSharedService()
    {
        var paymentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var providerPaymentId = $"fake-pay-{Guid.NewGuid():N}";
        var candidate = new PaymentReconciliationCandidate(
            paymentId,
            orderId,
            PaymentStatus.Pending,
            providerPaymentId,
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-2),
            null);
        var payment = new Payment(
            paymentId,
            orderId,
            100m,
            PaymentProviderCodes.Fake,
            providerPaymentId,
            candidate.GatewayIdempotencyKey,
            candidate.CreatedAt);
        var order = new Order(orderId, Guid.NewGuid(), 100m, Now.AddMinutes(10), candidate.CreatedAt);
        var reconciliationStore = new RecordingReconciliationStore([candidate]);
        var resultStore = new RecordingPaymentResultApplicationStore(payment, order);
        var gateway = new RecordingPaymentGateway(
            PaymentStatusResult.Found(providerPaymentId, PaymentGatewayStatus.Succeeded));
        var resultApplicationService = new PaymentResultApplicationService(resultStore, new FixedClock(Now), new GuidGenerator());
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            reconciliationStore,
            gateway,
            resultApplicationService,
            batchSize: 100,
            NullLogger<PaymentReconciliationProcessor>.Instance);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Empty(reconciliationStore.RecordedChecks);
        Assert.True(resultStore.TransactionCommitted);
    }

    [Fact]
    public async Task RunOnceAsync_WhenProviderFailed_AppliesFailureThroughSharedService()
    {
        var paymentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var providerPaymentId = $"fake-pay-{Guid.NewGuid():N}";
        var candidate = new PaymentReconciliationCandidate(
            paymentId,
            orderId,
            PaymentStatus.Pending,
            providerPaymentId,
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-2),
            null);
        var payment = new Payment(
            paymentId,
            orderId,
            100m,
            PaymentProviderCodes.Fake,
            providerPaymentId,
            candidate.GatewayIdempotencyKey,
            candidate.CreatedAt);
        var order = new Order(orderId, Guid.NewGuid(), 100m, Now.AddMinutes(10), candidate.CreatedAt);
        var reconciliationStore = new RecordingReconciliationStore([candidate]);
        var resultStore = new RecordingPaymentResultApplicationStore(payment, order);
        var gateway = new RecordingPaymentGateway(
            PaymentStatusResult.Found(providerPaymentId, PaymentGatewayStatus.Failed, "DECLINED"));
        var resultApplicationService = new PaymentResultApplicationService(resultStore, new FixedClock(Now), new GuidGenerator());
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            reconciliationStore,
            gateway,
            resultApplicationService,
            batchSize: 100,
            NullLogger<PaymentReconciliationProcessor>.Instance);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("DECLINED", payment.FailureCode);
        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Empty(reconciliationStore.RecordedChecks);
        Assert.True(resultStore.TransactionCommitted);
    }

    [Fact]
    public async Task RunOnceAsync_WhenPendingPaymentIsNotFoundAndStillPayable_RedrivesCreateWithOriginalIntent()
    {
        var candidate = new PaymentReconciliationCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PaymentStatus.Pending,
            $"fake-pay-{Guid.NewGuid():N}",
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-2),
            null,
            PaymentScenario.DelayedSuccess);
        var store = new RecordingReconciliationStore([candidate]) { CreateRedriveEligible = true };
        var gateway = new RecordingPaymentGateway(
            PaymentStatusResult.NotFound(),
            new CreatePaymentResult(candidate.ProviderPaymentId, PaymentGatewayStatus.Processing));
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            store,
            gateway,
            CreateResultApplicationService(),
            batchSize: 100,
            NullLogger<PaymentReconciliationProcessor>.Instance);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, store.CreateRedriveEligibilityCheckCount);
        var request = Assert.Single(gateway.CreateRequests);
        Assert.Equal(candidate.PaymentId, request.PaymentId);
        Assert.Equal(candidate.GatewayIdempotencyKey, request.IdempotencyKey);
        Assert.Equal(candidate.ProviderPaymentId, request.ProviderPaymentId);
        Assert.Equal(candidate.Amount, request.Amount);
        Assert.Equal(candidate.Scenario, request.Scenario);
        Assert.Equal([(candidate.PaymentId, Now)], store.RecordedChecks);
    }

    [Fact]
    public async Task RunOnceAsync_WhenProcessingPaymentIsNotFound_RemainsUnresolvedAndEmitsWarning()
    {
        var candidate = new PaymentReconciliationCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PaymentStatus.Processing,
            $"fake-pay-{Guid.NewGuid():N}",
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-2),
            null,
            PaymentScenario.DelayedSuccess);
        var store = new RecordingReconciliationStore([candidate]) { CreateRedriveEligible = true };
        var logger = new RecordingLogger<PaymentReconciliationProcessor>();
        var gateway = new RecordingPaymentGateway(PaymentStatusResult.NotFound());
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            store,
            gateway,
            CreateResultApplicationService(),
            batchSize: 100,
            logger);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, store.CreateRedriveEligibilityCheckCount);
        Assert.Empty(gateway.CreateRequests);
        Assert.Equal([(candidate.PaymentId, Now)], store.RecordedChecks);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(1, entry.EventId.Id);
        Assert.Equal("ProcessingPaymentNotFound", entry.EventId.Name);
        Assert.Null(entry.Exception);

        Assert.Equal(candidate.PaymentId, entry.Properties["PaymentId"]);
        Assert.Equal(candidate.OrderId, entry.Properties["OrderId"]);
        Assert.Equal(candidate.ProviderPaymentId, entry.Properties["ProviderPaymentId"]);

        Assert.Equal(
            "Provider returned NotFound for Processing Payment {PaymentId}, Order {OrderId}, ProviderPaymentId {ProviderPaymentId}",
            entry.Properties["{OriginalFormat}"]);

        Assert.DoesNotContain(
            candidate.GatewayIdempotencyKey,
            entry.Properties.Values.OfType<string>());
    }

    [Fact]
    public async Task RunOnceAsync_WhenPendingPaymentIsNotFoundAndOrderIsCancelled_MarksPaymentNotPayable()
    {
        var paymentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var providerPaymentId = $"fake-pay-{Guid.NewGuid():N}";
        var createdAt = Now.AddMinutes(-2);
        var candidate = new PaymentReconciliationCandidate(
            paymentId,
            orderId,
            PaymentStatus.Pending,
            providerPaymentId,
            $"gateway-{Guid.NewGuid():N}",
            100m,
            createdAt,
            null,
            PaymentScenario.Success);
        var payment = new Payment(
            paymentId,
            orderId,
            100m,
            PaymentProviderCodes.Fake,
            providerPaymentId,
            candidate.GatewayIdempotencyKey,
            createdAt,
            candidate.Scenario);
        var order = new Order(orderId, Guid.NewGuid(), 100m, Now.AddMinutes(10), createdAt);
        order.Cancel(Now.AddMinutes(-1));
        var reconciliationStore = new RecordingReconciliationStore([candidate]) { CreateRedriveEligible = false };
        var resultStore = new RecordingPaymentResultApplicationStore(payment, order);
        var gateway = new RecordingPaymentGateway(PaymentStatusResult.NotFound());
        var resultApplicationService = new PaymentResultApplicationService(resultStore, new FixedClock(Now), new GuidGenerator());
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            reconciliationStore,
            gateway,
            resultApplicationService,
            batchSize: 100,
            NullLogger<PaymentReconciliationProcessor>.Instance);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("ORDER_NOT_PAYABLE", payment.FailureCode);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Empty(gateway.CreateRequests);
        Assert.Equal(0, resultStore.AddedInventoryTransactionCount);
        Assert.True(resultStore.TransactionCommitted);
    }

    [Fact]
    public async Task RunOnceAsync_WhenPaymentBecomesProcessingBeforeNotFoundResolution_RemainsUnresolved()
    {
        var paymentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var providerPaymentId = $"fake-pay-{Guid.NewGuid():N}";
        var createdAt = Now.AddMinutes(-2);
        var candidate = new PaymentReconciliationCandidate(
            paymentId,
            orderId,
            PaymentStatus.Pending,
            providerPaymentId,
            $"gateway-{Guid.NewGuid():N}",
            100m,
            createdAt,
            null,
            PaymentScenario.Success);
        var payment = new Payment(
            paymentId,
            orderId,
            100m,
            PaymentProviderCodes.Fake,
            providerPaymentId,
            candidate.GatewayIdempotencyKey,
            createdAt,
            candidate.Scenario);
        payment.MarkProcessing(Now.AddMinutes(-1));
        var order = new Order(orderId, Guid.NewGuid(), 100m, Now.AddMinutes(-1), createdAt);
        var reconciliationStore = new RecordingReconciliationStore([candidate]) { CreateRedriveEligible = false };
        var resultStore = new RecordingPaymentResultApplicationStore(payment, order);
        var gateway = new RecordingPaymentGateway(PaymentStatusResult.NotFound());
        var resultApplicationService = new PaymentResultApplicationService(resultStore, new FixedClock(Now), new GuidGenerator());
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            reconciliationStore,
            gateway,
            resultApplicationService,
            batchSize: 100,
            NullLogger<PaymentReconciliationProcessor>.Instance);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PaymentStatus.Processing, payment.Status);
        Assert.Null(payment.FailureCode);
        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(0, resultStore.AddedInventoryTransactionCount);
        Assert.True(resultStore.TransactionCommitted);
    }

    [Fact]
    public async Task RunOnceAsync_WhenPendingPaymentWithoutScenarioIsNotFound_DoesNotEmitProcessingNotFoundWarning()
    {
        var candidate = new PaymentReconciliationCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PaymentStatus.Pending,
            $"fake-pay-{Guid.NewGuid():N}",
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-2),
            null,
            null);
        var store = new RecordingReconciliationStore([candidate]) { CreateRedriveEligible = true };
        var logger = new RecordingLogger<PaymentReconciliationProcessor>();
        var gateway = new RecordingPaymentGateway(PaymentStatusResult.NotFound());
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            store,
            gateway,
            CreateResultApplicationService(),
            batchSize: 100,
            logger);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, store.CreateRedriveEligibilityCheckCount);
        Assert.Empty(gateway.CreateRequests);
        Assert.Equal([(candidate.PaymentId, Now)], store.RecordedChecks);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(PaymentStatus.Pending, PaymentGatewayStatus.Pending)]
    [InlineData(PaymentStatus.Processing, PaymentGatewayStatus.Processing)]
    public async Task RunOnceAsync_WhenPaymentRemainsUnresolvedBeyondThirtyMinutes_EmitsAgedWarning(
    PaymentStatus paymentStatus,
    PaymentGatewayStatus providerStatus)
    {
        var candidate = new PaymentReconciliationCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            paymentStatus,
            $"fake-pay-{Guid.NewGuid():N}",
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-31),
            Now.AddMinutes(-15),
            PaymentScenario.DelayedSuccess);
        var store = new RecordingReconciliationStore([candidate]);
        var logger = new RecordingLogger<PaymentReconciliationProcessor>();
        var gateway = new RecordingPaymentGateway(
            PaymentStatusResult.Found(candidate.ProviderPaymentId, providerStatus));
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            store,
            gateway,
            CreateResultApplicationService(),
            batchSize: 100,
            logger);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal([candidate.ProviderPaymentId], gateway.StatusQueries);
        Assert.Equal([(candidate.PaymentId, Now)], store.RecordedChecks);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(2, entry.EventId.Id);
        Assert.Equal("AgedUnresolvedPayment", entry.EventId.Name);
        Assert.Null(entry.Exception);

        Assert.Equal(candidate.PaymentId, entry.Properties["PaymentId"]);
        Assert.Equal(candidate.OrderId, entry.Properties["OrderId"]);
        Assert.Equal(candidate.Status, entry.Properties["PaymentStatus"]);
        Assert.Equal(candidate.ProviderPaymentId, entry.Properties["ProviderPaymentId"]);

        Assert.Equal(
            "Payment {PaymentId} for Order {OrderId} remains {PaymentStatus} after 30 minutes, ProviderPaymentId {ProviderPaymentId}",
            entry.Properties["{OriginalFormat}"]);

        Assert.DoesNotContain(
            candidate.GatewayIdempotencyKey,
            entry.Properties.Values.OfType<string>());
    }

    [Fact]
    public async Task RunOnceAsync_WhenAgedProviderResponseIsLost_EmitsAgedWarning()
    {
        var candidate = new PaymentReconciliationCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PaymentStatus.Pending,
            $"fake-pay-{Guid.NewGuid():N}",
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-31),
            Now.AddMinutes(-15),
            PaymentScenario.DelayedSuccess);
        var store = new RecordingReconciliationStore([candidate]);
        var logger = new RecordingLogger<PaymentReconciliationProcessor>();
        var gateway = new RecordingPaymentGateway(
            new PaymentGatewayResponseLostException("Provider response was lost"));
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            store,
            gateway,
            CreateResultApplicationService(),
            batchSize: 100,
            logger);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal([candidate.ProviderPaymentId], gateway.StatusQueries);
        Assert.Equal([(candidate.PaymentId, Now)], store.RecordedChecks);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(2, entry.EventId.Id);
        Assert.Equal("AgedUnresolvedPayment", entry.EventId.Name);
        Assert.Null(entry.Exception);

        Assert.Equal(candidate.PaymentId, entry.Properties["PaymentId"]);
        Assert.Equal(candidate.OrderId, entry.Properties["OrderId"]);
        Assert.Equal(candidate.Status, entry.Properties["PaymentStatus"]);
        Assert.Equal(candidate.ProviderPaymentId, entry.Properties["ProviderPaymentId"]);

        Assert.DoesNotContain(
            candidate.GatewayIdempotencyKey,
            entry.Properties.Values.OfType<string>());
    }

    [Fact]
    public async Task RunOnceAsync_WhenAgedProviderStatusQueryTimesOut_EmitsAgedWarning()
    {
        var candidate = new PaymentReconciliationCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PaymentStatus.Pending,
            $"fake-pay-{Guid.NewGuid():N}",
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-31),
            Now.AddMinutes(-15),
            PaymentScenario.DelayedSuccess);
        var store = new RecordingReconciliationStore([candidate]);
        var logger = new RecordingLogger<PaymentReconciliationProcessor>();
        var gateway = new RecordingPaymentGateway(
            new TimeoutException("Provider status query timed out"));
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            store,
            gateway,
            CreateResultApplicationService(),
            batchSize: 100,
            logger);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal([candidate.ProviderPaymentId], gateway.StatusQueries);
        Assert.Equal([(candidate.PaymentId, Now)], store.RecordedChecks);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(2, entry.EventId.Id);
        Assert.Equal("AgedUnresolvedPayment", entry.EventId.Name);
        Assert.Null(entry.Exception);

        Assert.Equal(candidate.PaymentId, entry.Properties["PaymentId"]);
        Assert.Equal(candidate.OrderId, entry.Properties["OrderId"]);
        Assert.Equal(candidate.Status, entry.Properties["PaymentStatus"]);
        Assert.Equal(candidate.ProviderPaymentId, entry.Properties["ProviderPaymentId"]);

        Assert.DoesNotContain(
            candidate.GatewayIdempotencyKey,
            entry.Properties.Values.OfType<string>());
    }

    [Fact]
    public async Task RunOnceAsync_WhenAgedProcessingPaymentIsNotFound_EmitsInconsistencyAndAgedWarnings()
    {
        var candidate = new PaymentReconciliationCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PaymentStatus.Processing,
            $"fake-pay-{Guid.NewGuid():N}",
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-31),
            Now.AddMinutes(-15),
            PaymentScenario.DelayedSuccess);
        var store = new RecordingReconciliationStore([candidate]) { CreateRedriveEligible = true };
        var logger = new RecordingLogger<PaymentReconciliationProcessor>();
        var gateway = new RecordingPaymentGateway(PaymentStatusResult.NotFound());
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            store,
            gateway,
            CreateResultApplicationService(),
            batchSize: 100,
            logger);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, store.CreateRedriveEligibilityCheckCount);
        Assert.Empty(gateway.CreateRequests);
        Assert.Equal([(candidate.PaymentId, Now)], store.RecordedChecks);

        Assert.Equal(2, logger.Entries.Count);

        var inconsistency = Assert.Single(
            logger.Entries,
            entry => entry.EventId.Name == "ProcessingPaymentNotFound");
        var aged = Assert.Single(
            logger.Entries,
            entry => entry.EventId.Name == "AgedUnresolvedPayment");

        Assert.Equal(LogLevel.Warning, inconsistency.Level);
        Assert.Equal(1, inconsistency.EventId.Id);
        Assert.Null(inconsistency.Exception);

        Assert.Equal(LogLevel.Warning, aged.Level);
        Assert.Equal(2, aged.EventId.Id);
        Assert.Null(aged.Exception);

        Assert.Equal(candidate.PaymentId, aged.Properties["PaymentId"]);
        Assert.Equal(candidate.OrderId, aged.Properties["OrderId"]);
        Assert.Equal(candidate.Status, aged.Properties["PaymentStatus"]);
        Assert.Equal(candidate.ProviderPaymentId, aged.Properties["ProviderPaymentId"]);

        Assert.DoesNotContain(
            candidate.GatewayIdempotencyKey,
            logger.Entries
                .SelectMany(entry => entry.Properties.Values)
                .OfType<string>());
    }

    [Fact]
    public async Task RunOnceAsync_WhenAgedPendingPaymentWithoutScenarioIsNotFound_EmitsOnlyAgedWarning()
    {
        var candidate = new PaymentReconciliationCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PaymentStatus.Pending,
            $"fake-pay-{Guid.NewGuid():N}",
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-31),
            Now.AddMinutes(-15),
            null);
        var store = new RecordingReconciliationStore([candidate]) { CreateRedriveEligible = true };
        var logger = new RecordingLogger<PaymentReconciliationProcessor>();
        var gateway = new RecordingPaymentGateway(PaymentStatusResult.NotFound());
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            store,
            gateway,
            CreateResultApplicationService(),
            batchSize: 100,
            logger);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, store.CreateRedriveEligibilityCheckCount);
        Assert.Empty(gateway.CreateRequests);
        Assert.Equal([(candidate.PaymentId, Now)], store.RecordedChecks);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(2, entry.EventId.Id);
        Assert.Equal("AgedUnresolvedPayment", entry.EventId.Name);
        Assert.Null(entry.Exception);

        Assert.Equal(candidate.PaymentId, entry.Properties["PaymentId"]);
        Assert.Equal(candidate.OrderId, entry.Properties["OrderId"]);
        Assert.Equal(PaymentStatus.Pending, entry.Properties["PaymentStatus"]);
        Assert.Equal(candidate.ProviderPaymentId, entry.Properties["ProviderPaymentId"]);

        Assert.DoesNotContain(
            logger.Entries,
            item => item.EventId.Name == "ProcessingPaymentNotFound");

        Assert.DoesNotContain(
            candidate.GatewayIdempotencyKey,
            entry.Properties.Values.OfType<string>());
    }

    [Fact]
    public async Task RunOnceAsync_WhenAgedCreateRedriveResponseIsLost_EmitsAgedWarning()
    {
        var candidate = new PaymentReconciliationCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PaymentStatus.Pending,
            $"fake-pay-{Guid.NewGuid():N}",
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-31),
            Now.AddMinutes(-15),
            PaymentScenario.DelayedSuccess);
        var store = new RecordingReconciliationStore([candidate]) { CreateRedriveEligible = true };
        var logger = new RecordingLogger<PaymentReconciliationProcessor>();
        var gateway = new RecordingPaymentGateway(
            PaymentStatusResult.NotFound(),
            new PaymentGatewayResponseLostException("Provider create response was lost"));
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            store,
            gateway,
            CreateResultApplicationService(),
            batchSize: 100,
            logger);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, store.CreateRedriveEligibilityCheckCount);

        var request = Assert.Single(gateway.CreateRequests);
        Assert.Equal(candidate.PaymentId, request.PaymentId);
        Assert.Equal(candidate.GatewayIdempotencyKey, request.IdempotencyKey);
        Assert.Equal(candidate.ProviderPaymentId, request.ProviderPaymentId);

        Assert.Equal([(candidate.PaymentId, Now)], store.RecordedChecks);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(2, entry.EventId.Id);
        Assert.Equal("AgedUnresolvedPayment", entry.EventId.Name);
        Assert.Null(entry.Exception);

        Assert.Equal(candidate.PaymentId, entry.Properties["PaymentId"]);
        Assert.Equal(candidate.OrderId, entry.Properties["OrderId"]);
        Assert.Equal(PaymentStatus.Pending, entry.Properties["PaymentStatus"]);
        Assert.Equal(candidate.ProviderPaymentId, entry.Properties["ProviderPaymentId"]);

        Assert.DoesNotContain(
            candidate.GatewayIdempotencyKey,
            entry.Properties.Values.OfType<string>());
    }

    private static PaymentResultApplicationService CreateResultApplicationService() =>
        new(new RecordingPaymentResultApplicationStore(), new FixedClock(Now), new GuidGenerator());

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;

    private sealed class RecordingReconciliationStore(IReadOnlyList<PaymentReconciliationCandidate> candidates)
        : IPaymentReconciliationStore
    {
        private readonly List<(Guid PaymentId, DateTimeOffset CheckedAt)> recordedChecks = [];

        public IReadOnlyList<(Guid PaymentId, DateTimeOffset CheckedAt)> RecordedChecks => recordedChecks;
        public bool CreateRedriveEligible { get; init; }
        public int CreateRedriveEligibilityCheckCount { get; private set; }

        public Task<IReadOnlyList<PaymentReconciliationCandidate>> ListDueAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken) => Task.FromResult(candidates);

        public Task RecordStatusCheckAsync(Guid paymentId, DateTimeOffset checkedAt, CancellationToken cancellationToken)
        {
            recordedChecks.Add((paymentId, checkedAt));
            return Task.CompletedTask;
        }

        public Task<bool> IsCreateRedriveEligibleAsync(Guid paymentId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            CreateRedriveEligibilityCheckCount++;
            return Task.FromResult(CreateRedriveEligible);
        }
    }

    [Fact]
    public async Task RunOnceAsync_WhenAgedCreateRedriveTimesOut_EmitsAgedWarning()
    {
        var candidate = new PaymentReconciliationCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PaymentStatus.Pending,
            $"fake-pay-{Guid.NewGuid():N}",
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-31),
            Now.AddMinutes(-15),
            PaymentScenario.DelayedSuccess);
        var store = new RecordingReconciliationStore([candidate]) { CreateRedriveEligible = true };
        var logger = new RecordingLogger<PaymentReconciliationProcessor>();
        var gateway = new RecordingPaymentGateway(
            PaymentStatusResult.NotFound(),
            new TimeoutException("Provider create timed out"));
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            store,
            gateway,
            CreateResultApplicationService(),
            batchSize: 100,
            logger);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, store.CreateRedriveEligibilityCheckCount);

        var request = Assert.Single(gateway.CreateRequests);
        Assert.Equal(candidate.PaymentId, request.PaymentId);
        Assert.Equal(candidate.GatewayIdempotencyKey, request.IdempotencyKey);
        Assert.Equal(candidate.ProviderPaymentId, request.ProviderPaymentId);

        Assert.Equal([(candidate.PaymentId, Now)], store.RecordedChecks);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(2, entry.EventId.Id);
        Assert.Equal("AgedUnresolvedPayment", entry.EventId.Name);
        Assert.Null(entry.Exception);

        Assert.Equal(candidate.PaymentId, entry.Properties["PaymentId"]);
        Assert.Equal(candidate.OrderId, entry.Properties["OrderId"]);
        Assert.Equal(PaymentStatus.Pending, entry.Properties["PaymentStatus"]);
        Assert.Equal(candidate.ProviderPaymentId, entry.Properties["ProviderPaymentId"]);

        Assert.DoesNotContain(
            candidate.GatewayIdempotencyKey,
            entry.Properties.Values.OfType<string>());
    }

    [Theory]
    [InlineData(PaymentGatewayStatus.Pending)]
    [InlineData(PaymentGatewayStatus.Processing)]
    public async Task RunOnceAsync_WhenAgedCreateRedriveRemainsUnresolved_EmitsAgedWarning(
    PaymentGatewayStatus createStatus)
    {
        var candidate = new PaymentReconciliationCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PaymentStatus.Pending,
            $"fake-pay-{Guid.NewGuid():N}",
            $"gateway-{Guid.NewGuid():N}",
            100m,
            Now.AddMinutes(-31),
            Now.AddMinutes(-15),
            PaymentScenario.DelayedSuccess);
        var store = new RecordingReconciliationStore([candidate]) { CreateRedriveEligible = true };
        var logger = new RecordingLogger<PaymentReconciliationProcessor>();
        var gateway = new RecordingPaymentGateway(
            PaymentStatusResult.NotFound(),
            new CreatePaymentResult(candidate.ProviderPaymentId, createStatus));
        var processor = new PaymentReconciliationProcessor(
            new FixedClock(Now),
            store,
            gateway,
            CreateResultApplicationService(),
            batchSize: 100,
            logger);

        await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, store.CreateRedriveEligibilityCheckCount);

        var request = Assert.Single(gateway.CreateRequests);
        Assert.Equal(candidate.PaymentId, request.PaymentId);
        Assert.Equal(candidate.GatewayIdempotencyKey, request.IdempotencyKey);
        Assert.Equal(candidate.ProviderPaymentId, request.ProviderPaymentId);
        Assert.Equal(candidate.Amount, request.Amount);
        Assert.Equal(candidate.Scenario, request.Scenario);

        Assert.Equal([(candidate.PaymentId, Now)], store.RecordedChecks);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(2, entry.EventId.Id);
        Assert.Equal("AgedUnresolvedPayment", entry.EventId.Name);
        Assert.Null(entry.Exception);

        Assert.Equal(candidate.PaymentId, entry.Properties["PaymentId"]);
        Assert.Equal(candidate.OrderId, entry.Properties["OrderId"]);
        Assert.Equal(PaymentStatus.Pending, entry.Properties["PaymentStatus"]);
        Assert.Equal(candidate.ProviderPaymentId, entry.Properties["ProviderPaymentId"]);

        Assert.DoesNotContain(
            candidate.GatewayIdempotencyKey,
            entry.Properties.Values.OfType<string>());
    }

    private sealed class RecordingPaymentGateway : IPaymentGateway
    {
        private readonly List<string> statusQueries = [];
        private readonly List<CreatePaymentRequest> createRequests = [];
        private readonly PaymentStatusResult? statusResult;
        private readonly Exception? statusException;
        private readonly CreatePaymentResult? createResult;
        private readonly Exception? createException;

        public RecordingPaymentGateway(PaymentStatusResult statusResult)
        {
            this.statusResult = statusResult;
        }

        public RecordingPaymentGateway(PaymentStatusResult statusResult, Exception createException)
        {
            this.statusResult = statusResult;
            this.createException = createException;
        }

        public RecordingPaymentGateway(Exception statusException)
        {
            this.statusException = statusException;
        }

        public RecordingPaymentGateway(PaymentStatusResult statusResult, CreatePaymentResult createResult)
        {
            this.statusResult = statusResult;
            this.createResult = createResult;
        }

        public IReadOnlyList<string> StatusQueries => statusQueries;
        public IReadOnlyList<CreatePaymentRequest> CreateRequests => createRequests;

        public Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
        {
            createRequests.Add(request);

            if (createException is not null)
            {
                return Task.FromException<CreatePaymentResult>(createException);
            }

            return Task.FromResult(createResult ?? throw new InvalidOperationException("Create result is required"));
        }

        public Task<PaymentStatusResult> GetStatusAsync(string providerPaymentId, CancellationToken cancellationToken)
        {
            statusQueries.Add(providerPaymentId);

            if (statusException is not null)
            {
                return Task.FromException<PaymentStatusResult>(statusException);
            }

            return Task.FromResult(statusResult ?? throw new InvalidOperationException("Status result is required"));
        }

        public Task<RefundPaymentResult> RefundAsync(RefundPaymentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingPaymentResultApplicationStore(Payment? payment = null, Order? order = null)
        : IPaymentResultApplicationStore
    {
        public bool TransactionCommitted { get; private set; }
        public int AddedInventoryTransactionCount { get; private set; }

        public Task<IPaymentResultApplicationTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IPaymentResultApplicationTransaction>(new Transaction(this));

        public Task<Payment?> GetPaymentForUpdateAsync(string providerPaymentId, CancellationToken cancellationToken) =>
            Task.FromResult(payment is not null && payment.ProviderPaymentId == providerPaymentId ? payment : null);

        public Task<Order?> GetOrderForUpdateAsync(Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult(order is not null && order.Id == orderId ? order : null);

        public Task<IReadOnlyList<OrderItem>> ListOrderItemsAsync(Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OrderItem>>([]);

        public Task<bool> TryReleaseReservationAsync(
            Guid productVariantId,
            int quantity,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProviderPaymentEventClaimOutcome> ClaimProviderPaymentEventAsync(
            ProviderPaymentEvent providerPaymentEvent,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddInventoryTransactions(IEnumerable<InventoryTransaction> transactions)
        {
            AddedInventoryTransactionCount += transactions.Count();
        }

        public void AddOrderStatusHistory(OrderStatusHistory history)
        {
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private sealed class Transaction(RecordingPaymentResultApplicationStore owner)
            : IPaymentResultApplicationTransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken)
            {
                owner.TransactionCommitted = true;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class GuidGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> entries = [];

        public IReadOnlyList<LogEntry> Entries => entries;

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NoOpScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            IReadOnlyDictionary<string, object?> properties =
                state is IEnumerable<KeyValuePair<string, object?>> values
                    ? values.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value)
                    : new Dictionary<string, object?>();

            entries.Add(
                new LogEntry(
                    logLevel,
                    eventId,
                    exception,
                    properties));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class NoOpScope : IDisposable
    {
        public static NoOpScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
