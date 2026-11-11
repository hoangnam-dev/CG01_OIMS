using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Outbox;

internal sealed class OutboxPublishBatch(
    EfOutboxClaimStore claimStore,
    EfOutboxResultStore resultStore,
    IOutboxMessagePublisher publisher,
    TimeProvider timeProvider) : IOutboxPublishBatch
{
    public async Task<int> RunOnceAsync(int capacity, CancellationToken cancellationToken)
    {
        var claimed = await claimStore.ClaimDueAsync(timeProvider.GetUtcNow(), capacity, cancellationToken);
        var publishTasks = claimed.Select(message => PublishAndClassifyAsync(message, cancellationToken)).ToArray();
        var outcomes = await Task.WhenAll(publishTasks);

        for (var index = 0; index < claimed.Count; index++)
        {
            var message = claimed[index];
            var outcome = outcomes[index];
            var claimToken = message.ClaimToken ?? throw new InvalidOperationException("Claimed Outbox message has no claim token");
            if (outcome is OutboxFailureCategory.BrokerUnavailable
                    or OutboxFailureCategory.ConfirmTimeout
                    or OutboxFailureCategory.NegativeConfirmation
                    or OutboxFailureCategory.OutcomeUnknown)
            {
                var scheduled = await resultStore.TryScheduleRetryAsync(message.MessageId, claimToken, timeProvider.GetUtcNow(), outcome.Value, cancellationToken);
                if (!scheduled)
                {
                    throw new InvalidOperationException("Outbox retry result no longer belongs to the current claim");
                }

                continue;
            }
            if (outcome is OutboxFailureCategory.Unroutable or OutboxFailureCategory.UnsupportedStoredContract)
            {
                var recordedFailure = await resultStore.TryMarkNeedsAttentionAsync(message.MessageId, claimToken, outcome.Value, cancellationToken);
                if (!recordedFailure)
                {
                    throw new InvalidOperationException("Outbox terminal result no longer belongs to the current claim");
                }

                continue;
            }
            if (outcome is not null)
            {
                throw new InvalidOperationException("Outbox publisher returned an unsupported failure category");
            }

            var recorded = await resultStore.TryMarkPublishedAsync(message.MessageId, claimToken, timeProvider.GetUtcNow(), cancellationToken);
            if (!recorded)
            {
                throw new InvalidOperationException("Outbox publish result no longer belongs to the current claim");
            }
        }

        return claimed.Count;
    }

    private async Task<OutboxFailureCategory?> PublishAndClassifyAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await publisher.PublishAsync(message, cancellationToken);
            return null;
        }
        catch (OutboxPublishFailureException exception)
        {
            return exception.Category;
        }
    }
}
