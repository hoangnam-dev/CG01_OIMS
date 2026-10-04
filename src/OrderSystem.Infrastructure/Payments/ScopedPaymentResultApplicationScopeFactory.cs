using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Payments;

namespace OrderSystem.Infrastructure.Payments;

internal sealed class ScopedPaymentResultApplicationScopeFactory(IServiceScopeFactory scopeFactory) : IPaymentResultApplicationScopeFactory
{
    public ValueTask<IPaymentResultApplicationScope> CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var serviceScope = scopeFactory.CreateAsyncScope();

        try
        {
            var store = serviceScope.ServiceProvider.GetRequiredService<IPaymentResultApplicationStore>();

            IPaymentResultApplicationScope result = new PaymentResultApplicationScope(serviceScope, store);

            return ValueTask.FromResult(result);
        }
        catch
        {
            serviceScope.Dispose();
            throw;
        }
    }

    private sealed class PaymentResultApplicationScope(AsyncServiceScope serviceScope, IPaymentResultApplicationStore store) : IPaymentResultApplicationScope
    {
        public IPaymentResultApplicationStore Store { get; } = store;

        public ValueTask DisposeAsync() => serviceScope.DisposeAsync();
    }
}