using System.Security.Cryptography;
using System.Text;
using OrderSystem.Application.Payments.Contracts;

namespace OrderSystem.Application.Payments;

public static class PaymentIntentHasher
{
    public static byte[] Hash(Guid orderId, PaymentScenario scenario)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order ID must not be empty.", nameof(orderId));
        }

        var scenarioCode = PaymentScenarioCodes.ToCode(scenario);

        var canonicalPayload = $"initiate-payment:v1\n" +
                               $"order-id:{orderId.ToString("D").ToLowerInvariant()}\n" +
                               $"scenario:{scenarioCode}\n";
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload));
    }
}
