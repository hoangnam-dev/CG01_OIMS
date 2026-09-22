using OrderSystem.Application.Common.Identifiers;

namespace OrderSystem.UnitTests.Common;

public sealed class Uuid7IdGeneratorTests
{
    [Fact]
    public void NewId_CreatesNonEmptyUuidVersion7()
    {
        var generator = new Uuid7IdGenerator();

        var id = generator.NewId();

        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal('7', id.ToString("N")[12]);
    }
}
