using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;

namespace OrderSystem.Infrastructure.Payments.FakeProvider;

internal sealed class FakePaymentGateway(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IIdGenerator idGenerator
) : IPaymentGateway
{
    public async Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var providerScope = scopeFactory.CreateAsyncScope();

        var store = providerScope.ServiceProvider.GetRequiredService<FakeProviderOperationStore>();

        var resolution = await store.CreateOrGetPaymentAsync(
            request,
            idGenerator.NewId(),
            clock.UtcNow,
            cancellationToken
        );

        var operation = resolution.Operation;
        ThrowIfResponseWasLost(resolution);

        return MapCreatePaymentResult(operation);
    }

    public async Task<PaymentStatusResult> GetStatusAsync(string providerPaymentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerPaymentId);
        await using var providerScope = scopeFactory.CreateAsyncScope();
        var store = providerScope.ServiceProvider.GetRequiredService<FakeProviderOperationStore>();

        var operation = await store.GetPaymentStatusAsync(providerPaymentId, clock.UtcNow, cancellationToken);

        if (operation is null)
        {
            return PaymentStatusResult.NotFound();
        }

        var outcome = MapProviderOutcome(operation);

        return PaymentStatusResult.Found(
            providerPaymentId: operation.ProviderResourceId,
            status: outcome.Status,
            failureCode: outcome.FailureCode
        );
    }

    public async Task<RefundPaymentResult> RefundAsync(RefundPaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var providerScope = scopeFactory.CreateAsyncScope();
        var store = providerScope.ServiceProvider.GetRequiredService<FakeProviderOperationStore>();

        var resolution = await store.CreateOrGetRefundAsync(
            request,
            idGenerator.NewId(),
            clock.UtcNow,
            cancellationToken
        );

        ThrowIfResponseWasLost(resolution);

        var operation = resolution.Operation;
        var outcome = MapProviderOutcome(operation);

        return new RefundPaymentResult(
            ProviderRefundId: operation.ProviderResourceId,
            Status: outcome.Status,
            FailureCode: outcome.FailureCode
        );
    }

    private static CreatePaymentResult MapCreatePaymentResult(FakeProviderOperation operation)
    {
        var outcome = MapProviderOutcome(operation);

        return new CreatePaymentResult(
            ProviderPaymentId: operation.ProviderResourceId,
            Status: outcome.Status,
            FailureCode: outcome.FailureCode
        );
    }

    private static PaymentGatewayStatus MapStatus(FakeProviderOperationStatus status) =>
        status switch
        {
            FakeProviderOperationStatus.Pending => PaymentGatewayStatus.Pending,
            FakeProviderOperationStatus.Processing => PaymentGatewayStatus.Processing,
            FakeProviderOperationStatus.Succeeded => PaymentGatewayStatus.Succeeded,
            FakeProviderOperationStatus.Failed => PaymentGatewayStatus.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported fake provider operation status.")
        };

    private static (PaymentGatewayStatus Status, string? FailureCode) MapProviderOutcome(FakeProviderOperation operation) =>
    (operation.Scenario, operation.Status) switch
    {
        (PaymentScenario.Success, FakeProviderOperationStatus.Succeeded) => (PaymentGatewayStatus.Succeeded, null),
        (PaymentScenario.Failed, FakeProviderOperationStatus.Failed) => (PaymentGatewayStatus.Failed, FakeProviderScenarioPolicy.DeclinedFailureCode),
        (PaymentScenario.SuccessButResponseLost, FakeProviderOperationStatus.Succeeded) => (PaymentGatewayStatus.Succeeded, null),
        (PaymentScenario.DelayedSuccess, FakeProviderOperationStatus.Processing) => (PaymentGatewayStatus.Processing, null),
        (PaymentScenario.DelayedSuccess, FakeProviderOperationStatus.Succeeded) => (PaymentGatewayStatus.Succeeded, null),
        _ => throw new InvalidOperationException($"Provider operation has an invalid scenario/status combination: '{operation.Scenario}/{operation.Status}'.")
    };

    private static void ThrowIfResponseWasLost(FakeProviderOperationResolution resolution)
    {
        if (!resolution.WasCreated ||
        resolution.Operation.Scenario != PaymentScenario.SuccessButResponseLost)
        {
            return;
        }
        throw new PaymentGatewayResponseLostException("The provider committed the operation but its response was lost.");
    }
}
