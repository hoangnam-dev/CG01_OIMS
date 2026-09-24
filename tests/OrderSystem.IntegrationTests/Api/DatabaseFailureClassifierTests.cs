using Npgsql;
using OrderSystem.Api.Errors;

namespace OrderSystem.IntegrationTests.Api;

public sealed class DatabaseFailureClassifierTests
{
    [Fact]
    public void Classify_UnclassifiedCheckViolation_RemainsAnInternalFailure()
    {
        var exception = new PostgresException("database detail must not escape", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation);

        var result = DatabaseFailureClassifier.Classify(exception);

        Assert.Null(result);
    }

    [Fact]
    public void Classify_NpgsqlConnectivityFailure_ReturnsSanitizedDependencyUnavailable()
    {
        var result = DatabaseFailureClassifier.Classify(new NpgsqlException("Host=database Password=secret"));

        Assert.NotNull(result);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("DEPENDENCY_UNAVAILABLE", result.Code);
        Assert.DoesNotContain("secret", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.DeadlockDetected)]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    public void IsContention_ExpectedPostgreSqlRace_ReturnsTrue(string sqlState)
    {
        var exception = new PostgresException("contention", "ERROR", "ERROR", sqlState);

        Assert.True(DatabaseFailureClassifier.IsContention(exception));
        Assert.Null(DatabaseFailureClassifier.Classify(exception));
    }
}
