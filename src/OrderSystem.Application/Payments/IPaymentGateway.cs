using OrderSystem.Application.Payments.Contracts;

namespace OrderSystem.Application.Payments;

public interface IPaymentGateway
{
    Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken);
    Task<PaymentStatusResult> GetStatusAsync(string providerPaymentId, CancellationToken cancellationToken);
    Task<RefundPaymentResult> RefundAsync(RefundPaymentRequest request, CancellationToken cancellationToken);
}
