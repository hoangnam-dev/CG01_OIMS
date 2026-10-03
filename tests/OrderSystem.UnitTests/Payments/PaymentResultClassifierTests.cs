using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;

namespace OrderSystem.UnitTests.Payments;

public sealed class PaymentResultClassifierTests
{
    [Theory]
    [InlineData(PaymentGatewayStatus.Succeeded, ProviderPaymentOutcome.Succeeded)]
    [InlineData(PaymentGatewayStatus.Failed, ProviderPaymentOutcome.Failed)]
    public void TryClassify_WithAuthoritativeStatus_ReturnsExpectedOutcome(
        PaymentGatewayStatus status,
        ProviderPaymentOutcome expected)
    {
        var classified = PaymentResultClassifier.TryClassify(status, out var outcome);

        Assert.True(classified);
        Assert.Equal(expected, outcome);
    }

    [Theory]
    [InlineData(PaymentGatewayStatus.Pending)]
    [InlineData(PaymentGatewayStatus.Processing)]
    public void TryClassify_WithUnresolvedStatus_ReturnsFalse(PaymentGatewayStatus status)
    {
        var classified = PaymentResultClassifier.TryClassify(status, out var outcome);

        Assert.False(classified);
        Assert.Equal(default, outcome);
    }
    [Fact]
    public void TryClassify_WithUnsupportedStatus_ThrowsArgumentOutOfRangeException()
    {
        var exception = () =>
        {
            PaymentResultClassifier.TryClassify((PaymentGatewayStatus)999, out _);
        };

        Assert.Throws<ArgumentOutOfRangeException>(exception);
    }
}