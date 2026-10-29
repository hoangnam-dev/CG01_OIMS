using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Notifications;
using OrderSystem.Application.Notifications.Contracts;
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

    public async Task<PagedResult<MessageFailureSummaryDto>> ListAsync(MessageFailureListRequest request, CancellationToken cancellationToken)
    {
        var messages = dbContext.FailedMessages.AsNoTracking();
        if (request.Status is { } status)
        {
            messages = messages.Where(message => message.Status == status);
        }

        var totalCount = await messages.LongCountAsync(cancellationToken);
        var totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);
        var offset = (long)(request.Page - 1) * request.PageSize;

        IReadOnlyList<MessageFailureSummaryDto> items;
        if (offset >= totalCount)
        {
            items = [];
        }
        else
        {
            items = await messages
                .OrderByDescending(message => message.CapturedAt)
                .ThenByDescending(message => message.Id)
                .Skip((int)offset)
                .Take(request.PageSize)
                .Select(message => new MessageFailureSummaryDto(
                    message.Id,
                    message.MessageId,
                    message.ConsumerName,
                    message.FailureKind,
                    message.FailureCode,
                    message.AttemptCount,
                    message.Status,
                    message.OccurredAt,
                    message.CapturedAt,
                    message.TerminalAt
                ))
                .ToListAsync(cancellationToken);
        }

        return new(items, request.Page, request.PageSize, totalCount, totalPages);
    }

    public async Task<byte[]?> GetPayloadAndRecordAccessAsync(
        Guid failedMessageId,
        Guid accessId,
        Guid adminUserId,
        string reason,
        DateTimeOffset accessedAt,
        CancellationToken cancellationToken
    )
    {
        if (failedMessageId == Guid.Empty)
        {
            throw new ArgumentException("Failed Message ID cannot be empty", nameof(failedMessageId));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var failedMessage = await dbContext.FailedMessages
            .FromSqlInterpolated($"SELECT * FROM failed_messages WHERE id = {failedMessageId} FOR SHARE")
            .SingleOrDefaultAsync(cancellationToken);

        if (failedMessage is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var payload = failedMessage.Body.ToArray();
        dbContext.FailedMessagePayloadAccesses.Add(
            new FailedMessagePayloadAccess(
                accessId,
                failedMessageId,
                adminUserId,
                reason,
                accessedAt
            )
        );
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return payload;
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
