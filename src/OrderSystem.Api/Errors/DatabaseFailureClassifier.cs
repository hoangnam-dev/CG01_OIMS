using Npgsql;

namespace OrderSystem.Api.Errors;

public static class DatabaseFailureClassifier
{
    public static DatabaseFailure? Classify(Exception exception) => exception switch
    {
        PostgresException => null,
        NpgsqlException =>
            new(StatusCodes.Status503ServiceUnavailable, "DEPENDENCY_UNAVAILABLE", "A required dependency is currently unavailable.", IsContention: false),
        _ => null
    };

    public static bool IsContention(Exception exception) => exception is PostgresException
    {
        SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure
    };
}

public sealed record DatabaseFailure(int StatusCode, string Code, string Message, bool IsContention);
