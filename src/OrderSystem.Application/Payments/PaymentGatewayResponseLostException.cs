namespace OrderSystem.Application.Payments;

public sealed class PaymentGatewayResponseLostException : Exception
{
    public PaymentGatewayResponseLostException(string message) : base(message)
    {
    }
}