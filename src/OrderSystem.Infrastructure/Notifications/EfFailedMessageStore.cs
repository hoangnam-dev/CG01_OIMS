using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Notifications;
using OrderSystem.Domain.Notifications;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Notifications;

internal sealed class EfFailedMessageStore(OrderSystemDbContext dbContext) : IFailedMessageStore
{
    public async Task CaptureAsync(FailedMessage failedMessage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failedMessage);

        dbContext.FailedMessages.Add(failedMessage);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<FailedMessage?> GetAsync(Guid failedMessageId, CancellationToken cancellationToken)
    {
        if (failedMessageId == Guid.Empty)
        {
            throw new ArgumentException("Failed Message ID cannot be empty", nameof(failedMessageId));
        }

        return dbContext.FailedMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(message => message.Id == failedMessageId, cancellationToken
        );
    }

    public async Task<FailedMessageRedriveStoreResult> RequestRedriveAsync(
        Guid failedMessageId,
        Guid adminUserId,
        string reason,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken
    )
    {
        if (failedMessageId == Guid.Empty)
        {
            throw new ArgumentException("Failed Message ID cannot be empty", nameof(failedMessageId));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var failedMessage = await dbContext.FailedMessages
            .FromSqlInterpolated($"SELECT * FROM failed_messages WHERE id = {failedMessageId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        if (failedMessage is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(FailedMessageRedriveStoreStatus.NotFound, null);
        }

        var alreadyRequested = failedMessage.Status == FailedMessageStatus.RedriveRequested;
        if (!failedMessage.TryRequestRedrive(adminUserId, reason, requestedAt))
        {
            await transaction.CommitAsync(cancellationToken);
            return new(FailedMessageRedriveStoreStatus.NotRedrivable, failedMessage);
        }

        if (!alreadyRequested)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(
            alreadyRequested
                ? FailedMessageRedriveStoreStatus.AlreadyRequested
                : FailedMessageRedriveStoreStatus.Requested,
            failedMessage);
    }

    public async Task<FailedMessageCloseStoreResult> CloseAsync(
        Guid failedMessageId,
        Guid adminUserId,
        string reason,
        DateTimeOffset closedAt,
        CancellationToken cancellationToken
    )
    {
        if (failedMessageId == Guid.Empty)
        {
            throw new ArgumentException("Failed Message ID cannot be empty", nameof(failedMessageId));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var failedMessage = await dbContext.FailedMessages
            .FromSqlInterpolated($"SELECT * FROM failed_messages WHERE id = {failedMessageId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        if (failedMessage is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(FailedMessageCloseStoreStatus.NotFound, null);
        }

        var alreadyClosed = failedMessage.Status == FailedMessageStatus.Closed;
        if (!failedMessage.TryClose(adminUserId, reason, closedAt))
        {
            await transaction.CommitAsync(cancellationToken);
            return new(FailedMessageCloseStoreStatus.StateConflict, failedMessage);
        }

        if (!alreadyClosed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(
            alreadyClosed
                ? FailedMessageCloseStoreStatus.AlreadyClosed
                : FailedMessageCloseStoreStatus.Closed,
            failedMessage
        );
    }
}
