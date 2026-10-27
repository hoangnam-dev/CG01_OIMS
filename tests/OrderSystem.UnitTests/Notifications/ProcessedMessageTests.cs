using OrderSystem.Domain.Notifications;

namespace OrderSystem.UnitTests.Notifications;

public sealed class ProcessedMessageTests
{
    [Fact]
    [Trait("Requirement", "DB-MSG-001")]
    public void Constructor_ConsumerNameLongerThanOneHundredCharacters_Throws()
    {
        var action = () => new ProcessedMessage(
            Guid.NewGuid(),
            new string('C', 101),
            DateTimeOffset.UtcNow);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("consumerName", exception.ParamName);
    }
}