using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;

namespace OrderSystem.Application.Payments;

public sealed class PaymentCommandService(
    IPaymentInitiationStore store,
    IPaymentGateway paymentGateway,
    PaymentResultApplicationService paymentResultApplicationService,
    ICurrentUser currentUser,
    IClock clock,
    IIdGenerator idGenerator,
    IOperationHook operationHook,
    TimeSpan idempotencyReplayWindow,
    TimeSpan idempotencyRetentionWindow
)
{
    public async Task<ApplicationResult<PaymentInitiationResult>> InitiateAsync(InitiatePaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not { } userId ||
            userId == Guid.Empty ||
            currentUser.Role is null)
        {
            return ApplicationResult.Failure<PaymentInitiationResult>(ApplicationErrors.Unauthorized.Create());
        }

        if (request.OrderId == Guid.Empty ||
            request.IdempotencyKey == Guid.Empty ||
            !Enum.IsDefined(request.Scenario))
        {
            return ApplicationResult.Failure<PaymentInitiationResult>(
                    ApplicationErrors.ValidationFailed.Create(
                        validationErrors:
                            new Dictionary<string, string[]>
                            {
                                ["orderId"] = request.OrderId == Guid.Empty
                                        ? ["A valid Order ID is required."]
                                        : [],
                                ["idempotencyKey"] = request.IdempotencyKey == Guid.Empty
                                        ? ["A valid Idempotency Key is required."]
                                        : [],
                                ["scenario"] = !Enum.IsDefined(request.Scenario)
                                        ? ["A supported Payment scenario is required."]
                                        : []
                            }
                            .Where(item => item.Value.Length > 0)
                            .ToDictionary(item => item.Key, item => item.Value)));
        }

        var now = clock.UtcNow;

        var requestHash = PaymentIntentHasher.Hash(request.OrderId, request.Scenario);

        Payment payment;

        await using (var transaction = await store.BeginTransactionAsync(cancellationToken))
        {
            await operationHook.ReachAsync(PaymentOperationCheckpoints.BeforeIdempotencyClaim, cancellationToken);

            var claimResult = await store.TryClaimAsync(
                    new PaymentInitiationClaim(
                        Id: idGenerator.NewId(),
                        UserId: userId,
                        IdempotencyKey: request.IdempotencyKey,
                        RequestHash: requestHash,
                        CreatedAt: now,
                        ExpiresAt: now.Add(idempotencyReplayWindow),
                        DeleteAfter: now.Add(idempotencyRetentionWindow)),
                    cancellationToken);

            switch (claimResult.Outcome)
            {
                case PaymentInitiationClaimOutcome.CompletedReplay:
                    {
                        var replayedPaymentId = claimResult.PaymentId
                            ?? throw new InvalidOperationException("A completed Payment initiation replay is missing its Payment ID.");

                        var replayedPayment = await store.GetPaymentForOwnerAsync(replayedPaymentId, userId, cancellationToken);

                        if (replayedPayment is null)
                        {
                            throw new InvalidOperationException("A completed Payment initiation replay references a missing Payment.");
                        }

                        return ApplicationResult.Success(
                            new PaymentInitiationResult(
                                replayedPayment.Id,
                                replayedPayment.Status,
                                IsReplay: true));
                    }

                case PaymentInitiationClaimOutcome.HashConflict:
                    return ApplicationResult.Failure<PaymentInitiationResult>(ApplicationErrors.Idempotency.KeyReused.Create());

                case PaymentInitiationClaimOutcome.Processing:
                    return ApplicationResult.Failure<PaymentInitiationResult>(ApplicationErrors.Idempotency.RequestProcessing.Create());

                case PaymentInitiationClaimOutcome.Expired:
                    return ApplicationResult.Failure<PaymentInitiationResult>(ApplicationErrors.Idempotency.KeyExpired.Create());

                case PaymentInitiationClaimOutcome.Claimed:
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported Payment initiation claim outcome '{claimResult.Outcome}'.");
            }

            var order = await store.GetOwnedOrderForUpdateAsync(request.OrderId, userId, cancellationToken);

            if (order is null)
            {
                return ApplicationResult.Failure<PaymentInitiationResult>(ApplicationErrors.Orders.NotFound.Create());
            }

            var paymentExists = await store.PaymentExistsForOrderAsync(order.Id, cancellationToken);

            if (paymentExists)
            {
                return ApplicationResult.Failure<PaymentInitiationResult>(ApplicationErrors.Payments.AlreadyExists.Create());
            }

            if (order.Status != OrderStatus.PendingPayment)
            {
                return ApplicationResult.Failure<PaymentInitiationResult>(ApplicationErrors.Orders.InvalidStatus.Create());
            }

            if (now >= order.ReservationExpiresAt)
            {
                return ApplicationResult.Failure<PaymentInitiationResult>(ApplicationErrors.Orders.ReservationExpired.Create());
            }

            var paymentId = idGenerator.NewId();

            payment = new Payment(
                id: paymentId,
                orderId: order.Id,
                amount: order.TotalAmount,
                provider: PaymentProviderCodes.Fake,
                providerPaymentId: $"fake-pay-{paymentId:D}",
                gatewayIdempotencyKey: $"fake-gateway-{paymentId:D}",
                createdAt: now,
                scenario: request.Scenario
            );

            store.AddPayment(payment);

            await store.SaveChangesAsync(cancellationToken);

            var idempotencyRequestId = claimResult.IdempotencyRequestId
                ?? throw new InvalidOperationException("A claimed Payment initiation is missing its idempotency request ID.");

            var bound = await store.TryBindPaymentIntentAsync(idempotencyRequestId, payment.Id, now, cancellationToken);

            if (!bound)
            {
                throw new InvalidOperationException("The claimed Payment initiation could not be bound to its Payment.");
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await operationHook.ReachAsync(PaymentOperationCheckpoints.AfterLocalCommit, cancellationToken);

        var providerCommitKnown = false;
        CreatePaymentResult? gatewayResult = null;
        DateTimeOffset? gatewayResultReceivedAt = null;

        try
        {
            gatewayResult = await paymentGateway.CreatePaymentAsync(
                new CreatePaymentRequest(
                    PaymentId: payment.Id,
                    IdempotencyKey: payment.GatewayIdempotencyKey,
                    ProviderPaymentId: payment.ProviderPaymentId,
                    Amount: payment.Amount,
                    Scenario: request.Scenario),
                cancellationToken);
            gatewayResultReceivedAt = clock.UtcNow;
            providerCommitKnown = true;
        }
        catch (PaymentGatewayResponseLostException)
        {
            providerCommitKnown = true;
        }
        catch (TimeoutException)
        {
        }

        if (providerCommitKnown)
        {
            await operationHook.ReachAsync(PaymentOperationCheckpoints.AfterGatewayCreate, cancellationToken);
        }

        if (gatewayResult is not null &&
            gatewayResultReceivedAt is { } occurredAt &&
            PaymentResultClassifier.TryClassify(gatewayResult.Status, out var outcome)
        )
        {
            await paymentResultApplicationService.ApplyAsync(
                new ApplyPaymentResultCommand(
                    gatewayResult.ProviderPaymentId,
                    outcome,
                    gatewayResult.FailureCode,
                    PaymentResultSource.SynchronousResponse,
                    providerEvent: null,
                    occurredAt
                ),
                cancellationToken
            );

            if (request.Scenario == PaymentScenario.SuccessButClientResponseLost)
            {
                await operationHook.ReachAsync(
                    PaymentOperationCheckpoints.AfterResultApplication,
                    cancellationToken);
            }
        }

        return ApplicationResult.Success(
            new PaymentInitiationResult(
                payment.Id,
                payment.Status,
                IsReplay: false
            )
        );
    }
}
