using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Payments.FakeProvider;

internal sealed class FakeProviderOperationStore(OrderSystemDbContext dbContext)
{
    private const string ProviderOperationIdempotencyConstraint = "uq_fake_provider_operations_type_key";

    public Task<FakeProviderOperationResolution> CreateOrGetPaymentAsync(
        CreatePaymentRequest request,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return CreateOrGetAsync(
            FakeProviderOperationType.CreatePayment,
            request.IdempotencyKey,
            request.ProviderPaymentId,
            parentProviderPaymentId: null,
            request.Scenario,
            request.Amount,
            operationId,
            now,
            cancellationToken
        );
    }

    public Task<FakeProviderOperationResolution> CreateOrGetRefundAsync(
        RefundPaymentRequest request,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return CreateOrGetAsync(
            FakeProviderOperationType.RefundPayment,
            request.IdempotencyKey,
            request.ProviderRefundId,
            request.ParentProviderPaymentId,
            request.Scenario,
            request.Amount,
            operationId,
            now,
            cancellationToken
        );
    }

    public async Task<FakeProviderOperation?> GetPaymentStatusAsync(
        string providerPaymentId,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerPaymentId);

        var operation = await dbContext.FakeProviderOperations
            .SingleOrDefaultAsync(candidate =>
                candidate.OperationType == FakeProviderOperationType.CreatePayment &&
                candidate.ProviderResourceId == providerPaymentId,
                cancellationToken
            );

        if (operation is null)
        {
            return null;
        }

        var isAwaitingAvailability = operation.Status == FakeProviderOperationStatus.Pending || operation.Status == FakeProviderOperationStatus.Processing;

        if (isAwaitingAvailability && operation.IsAvailable(now))
        {
            operation.MarkSucceeded(now);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return operation;
    }

    private async Task<FakeProviderOperationResolution> CreateOrGetAsync(
        FakeProviderOperationType operationType,
        string idempotencyKey,
        string providerResourceId,
        string? parentProviderPaymentId,
        PaymentScenario scenario,
        decimal amount,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var existing = await dbContext.FakeProviderOperations
            .SingleOrDefaultAsync(operation =>
                operation.OperationType == operationType &&
                operation.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existing is not null)
        {
            EnsureEquivalent(
                existing,
                providerResourceId,
                parentProviderPaymentId,
                scenario,
                amount
            );
            return new FakeProviderOperationResolution(existing, WasCreated: false);
        }

        var initialStatus = FakeProviderScenarioPolicy.GetInitialStatus(scenario);
        var availableAt = FakeProviderScenarioPolicy.GetAvailableAt(scenario, now);

        var operation = new FakeProviderOperation(
            operationId,
            operationType,
            idempotencyKey,
            providerResourceId,
            parentProviderPaymentId,
            scenario,
            status: initialStatus,
            amount,
            availableAt: availableAt,
            createdAt: now,
            updatedAt: now
        );

        dbContext.FakeProviderOperations.Add(operation);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new FakeProviderOperationResolution(operation, WasCreated: true);
        }
        catch (DbUpdateException ex)
            when (IsProviderIdempotencyConflict(ex))
        {
            dbContext.Entry(operation).State = EntityState.Detached;

            var winner = await dbContext.FakeProviderOperations
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate =>
                    candidate.OperationType == operationType &&
                    candidate.IdempotencyKey == idempotencyKey,
                    cancellationToken);

            if (winner is null)
            {
                throw;
            }

            EnsureEquivalent(
                winner,
                providerResourceId,
                parentProviderPaymentId,
                scenario,
                amount
            );
            return new FakeProviderOperationResolution(winner, WasCreated: false);
        }
    }

    private static bool IsProviderIdempotencyConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ProviderOperationIdempotencyConstraint
        };

    private static void EnsureEquivalent(
        FakeProviderOperation operation,
        string providerResourceId,
        string? parentProviderPaymentId,
        PaymentScenario scenario,
        decimal amount
    )
    {
        if (operation.ProviderResourceId != providerResourceId ||
            operation.ParentProviderPaymentId != parentProviderPaymentId ||
            operation.Scenario != scenario ||
            operation.Amount != amount)
        {
            throw new FakeProviderOperationConflictException("Provider idempotency key was reused with different semantics.");
        }
    }
}
