using System.Reflection;
using OrderSystem.Domain.Notifications;

namespace OrderSystem.UnitTests.Notifications;

public sealed class FailedMessagePayloadAccessTests
{
    [Fact]
    public void Constructor_ValidAccess_TrimsReasonAndPreservesAuditIdentity()
    {
        var id = Guid.NewGuid();
        var failedMessageId = Guid.NewGuid();
        var accessedByUserId = Guid.NewGuid();
        var accessedAt = DateTimeOffset.UtcNow;

        var access = InvokeConstructor(
            id,
            failedMessageId,
            accessedByUserId,
            "  Investigate malformed delivery  ",
            accessedAt);

        Assert.Equal(id, access.Id);
        Assert.Equal(failedMessageId, access.FailedMessageId);
        Assert.Equal(accessedByUserId, access.AccessedByUserId);
        Assert.Equal("Investigate malformed delivery", access.Reason);
        Assert.Equal(accessedAt, access.AccessedAt);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("failedMessageId")]
    [InlineData("accessedByUserId")]
    public void Constructor_EmptyIdentity_ThrowsForParameter(string parameterName)
    {
        var exception = AssertInvocationThrows<ArgumentException>(() => InvokeConstructor(
            parameterName == "id" ? Guid.Empty : Guid.NewGuid(),
            parameterName == "failedMessageId" ? Guid.Empty : Guid.NewGuid(),
            parameterName == "accessedByUserId" ? Guid.Empty : Guid.NewGuid(),
            "Investigate malformed delivery",
            DateTimeOffset.UtcNow));

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankReason_ThrowsForReason(string? reason)
    {
        var exception = AssertInvocationThrows<ArgumentException>(() => InvokeConstructor(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            reason!,
            DateTimeOffset.UtcNow));

        Assert.Equal("reason", exception.ParamName);
    }

    [Fact]
    public void Constructor_ReasonLongerThanFiveHundredCharacters_ThrowsForReason()
    {
        var exception = AssertInvocationThrows<ArgumentException>(() => InvokeConstructor(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string('R', 501),
            DateTimeOffset.UtcNow));

        Assert.Equal("reason", exception.ParamName);
    }

    private static FailedMessagePayloadAccess InvokeConstructor(
        Guid id,
        Guid failedMessageId,
        Guid accessedByUserId,
        string reason,
        DateTimeOffset accessedAt)
    {
        var constructor = Assert.Single(typeof(FailedMessagePayloadAccess).GetConstructors());
        return Assert.IsType<FailedMessagePayloadAccess>(constructor.Invoke(
            [id, failedMessageId, accessedByUserId, reason, accessedAt]));
    }

    private static TException AssertInvocationThrows<TException>(Action action)
        where TException : Exception
    {
        var invocationException = Assert.Throws<TargetInvocationException>(action);
        return Assert.IsAssignableFrom<TException>(invocationException.InnerException);
    }
}
