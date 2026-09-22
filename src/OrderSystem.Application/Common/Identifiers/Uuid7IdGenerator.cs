namespace OrderSystem.Application.Common.Identifiers;

public sealed class Uuid7IdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7();
}
