namespace OrderSystem.Application.Payments;

public interface IPaymentResultApplicationScope : IAsyncDisposable
{
    IPaymentResultApplicationStore Store { get; }
}

public interface IPaymentResultApplicationScopeFactory
{
    ValueTask<IPaymentResultApplicationScope> CreateAsync(CancellationToken cancellationToken);
}