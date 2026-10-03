namespace OrderSystem.Infrastructure.Payments.FakeProvider;

internal sealed class FakeProviderOperationConflictException : Exception
{
    public FakeProviderOperationConflictException(string message) : base(message)
    {
    }
}