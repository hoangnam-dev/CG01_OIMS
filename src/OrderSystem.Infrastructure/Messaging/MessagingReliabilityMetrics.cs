using System.Diagnostics.Metrics;

namespace OrderSystem.Infrastructure.Messaging;

internal enum MessagingOperation { Process, Transfer, Acknowledge, DeadLetterIntake, Redrive, Cleanup }
internal enum MessagingOutcome { Success, Duplicate, PermanentFailure, TransientFailure, Failed }
internal enum MessagingRoute { Main, Retry, DeadLetter, FailureLedger }
internal enum MessagingAgeStage { Consumed, RetryTransfer, DeadLetterIntake, Redrive }
internal enum MessagingCleanupRecordType { ProcessedMessage, TerminalFailure }

internal static class MessagingReliabilityMetrics
{
    public const string MeterName = "OrderSystem.Infrastructure.Messaging";
    public const string OperationCounterName = "oims.messaging.operations";
    public const string MessageAgeHistogramName = "oims.messaging.message_age_seconds";
    public const string CleanupDeletedCounterName = "oims.messaging.cleanup.deleted";
    public const string BacklogHistogramName = "oims.messaging.backlog";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> OperationCounter = Meter.CreateCounter<long>(
        OperationCounterName,
        unit: "{operation}",
        description: "Messaging reliability operation outcomes");

    private static readonly Histogram<double> MessageAgeHistogram = Meter.CreateHistogram<double>(
        MessageAgeHistogramName,
        unit: "s",
        description: "Observed message age at messaging reliability boundaries");

    private static readonly Counter<long> CleanupDeletedCounter = Meter.CreateCounter<long>(
        CleanupDeletedCounterName,
        unit: "{record}",
        description: "Records removed by bounded messaging cleanup");

    private static readonly Histogram<long> BacklogHistogram = Meter.CreateHistogram<long>(
        BacklogHistogramName,
        unit: "{message}",
        description: "Broker-reported queue backlog snapshots");

    public static void RecordOperation(MessagingOperation operation, MessagingOutcome outcome, MessagingRoute route, int? attempt = null)
    {
        if (attempt is not null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(attempt.Value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(attempt.Value, 3);
            OperationCounter.Add(
                1,
                new KeyValuePair<string, object?>("operation", MapOperation(operation)),
                new KeyValuePair<string, object?>("outcome", MapOutcome(outcome)),
                new KeyValuePair<string, object?>("route", MapRoute(route)),
                new KeyValuePair<string, object?>("attempt", attempt.Value));
            return;
        }

        OperationCounter.Add(
            1,
            new KeyValuePair<string, object?>("operation", MapOperation(operation)),
            new KeyValuePair<string, object?>("outcome", MapOutcome(outcome)),
            new KeyValuePair<string, object?>("route", MapRoute(route)));
    }

    public static void RecordMessageAge(MessagingAgeStage stage, DateTimeOffset occurredAt, DateTimeOffset observedAt)
    {
        MessageAgeHistogram.Record(
            Math.Max(0, (observedAt - occurredAt).TotalSeconds),
            new KeyValuePair<string, object?>("stage", MapAgeStage(stage)));
    }

    public static void RecordCleanup(MessagingCleanupRecordType recordType, int deletedCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deletedCount);
        if (deletedCount > 0)
        {
            CleanupDeletedCounter.Add(
                deletedCount,
                new KeyValuePair<string, object?>("record_type", MapCleanupRecordType(recordType)));
        }
    }

    public static void RecordBacklog(MessagingRoute route, uint messageCount)
    {
        BacklogHistogram.Record(
            messageCount,
            new KeyValuePair<string, object?>("route", MapRoute(route)));
    }

    private static string MapOperation(MessagingOperation value) => value switch
    {
        MessagingOperation.Process => "process",
        MessagingOperation.Transfer => "transfer",
        MessagingOperation.Acknowledge => "acknowledge",
        MessagingOperation.DeadLetterIntake => "dead_letter_intake",
        MessagingOperation.Redrive => "redrive",
        MessagingOperation.Cleanup => "cleanup",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string MapOutcome(MessagingOutcome value) => value switch
    {
        MessagingOutcome.Success => "success",
        MessagingOutcome.Duplicate => "duplicate",
        MessagingOutcome.PermanentFailure => "permanent_failure",
        MessagingOutcome.TransientFailure => "transient_failure",
        MessagingOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string MapRoute(MessagingRoute value) => value switch
    {
        MessagingRoute.Main => "main",
        MessagingRoute.Retry => "retry",
        MessagingRoute.DeadLetter => "dead_letter",
        MessagingRoute.FailureLedger => "failure_ledger",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string MapAgeStage(MessagingAgeStage value) => value switch
    {
        MessagingAgeStage.Consumed => "consumed",
        MessagingAgeStage.RetryTransfer => "retry_transfer",
        MessagingAgeStage.DeadLetterIntake => "dead_letter_intake",
        MessagingAgeStage.Redrive => "redrive",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string MapCleanupRecordType(MessagingCleanupRecordType value) => value switch
    {
        MessagingCleanupRecordType.ProcessedMessage => "processed_message",
        MessagingCleanupRecordType.TerminalFailure => "terminal_failure",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
