namespace OrderSystem.Infrastructure.Payments.FakeProvider;

internal sealed record FakeProviderOperationResolution(FakeProviderOperation Operation, bool WasCreated);