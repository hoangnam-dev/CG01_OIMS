using System.Security.Cryptography;
using System.Text;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;

namespace OrderSystem.UnitTests.Payments;

public sealed class PaymentIntentHasherTests
{
    [Fact]
    public void Hash_ReturnsSha256OfCanonicalPaymentIntent()
    {
        var orderId = Guid.Parse("A0B1C2D3-E4F5-4678-9ABC-DEF012345678");

        var canonicalPayload =
            "initiate-payment:v1\n" +
            "order-id:a0b1c2d3-e4f5-4678-9abc-def012345678\n" +
            "scenario:SUCCESS\n";

        var expected = SHA256.HashData(
            Encoding.UTF8.GetBytes(canonicalPayload));

        var actual = PaymentIntentHasher.Hash(
            orderId,
            PaymentScenario.Success);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Hash_WithSameIntent_ReturnsSameHash()
    {
        var orderId = Guid.NewGuid();

        var first = PaymentIntentHasher.Hash(
            orderId,
            PaymentScenario.DelayedSuccess);

        var second = PaymentIntentHasher.Hash(
            orderId,
            PaymentScenario.DelayedSuccess);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Hash_WithDifferentScenario_ReturnsDifferentHash()
    {
        var orderId = Guid.NewGuid();

        var success = PaymentIntentHasher.Hash(
            orderId,
            PaymentScenario.Success);

        var failed = PaymentIntentHasher.Hash(
            orderId,
            PaymentScenario.Failed);

        Assert.NotEqual(success, failed);
    }

    [Fact]
    public void Hash_ReturnsRawSha256Bytes()
    {
        var result = PaymentIntentHasher.Hash(
            Guid.NewGuid(),
            PaymentScenario.Success);

        Assert.Equal(32, result.Length);
    }

    [Fact]
    public void Hash_WithEmptyOrderId_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PaymentIntentHasher.Hash(
                Guid.Empty,
                PaymentScenario.Success));

        Assert.Equal("orderId", exception.ParamName);
    }

    [Fact]
    public void Hash_WithUndefinedScenario_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => PaymentIntentHasher.Hash(
                Guid.NewGuid(),
                (PaymentScenario)999));

        Assert.Equal("scenario", exception.ParamName);
    }

    [Theory]
    [InlineData(PaymentScenario.Success, "SUCCESS")]
    [InlineData(PaymentScenario.Failed, "FAILED")]
    [InlineData(PaymentScenario.SuccessButResponseLost, "SUCCESS_BUT_RESPONSE_LOST")]
    [InlineData(PaymentScenario.DelayedSuccess, "DELAYED_SUCCESS")]
    public void Hash_UsesStableScenarioCode(
    PaymentScenario scenario,
    string expectedCode)
    {
        var orderId = Guid.Parse("a0b1c2d3-e4f5-4678-9abc-def012345678");

        var canonicalPayload =
            "initiate-payment:v1\n" +
            $"order-id:{orderId:D}\n" +
            $"scenario:{expectedCode}\n";

        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload));

        var actual = PaymentIntentHasher.Hash(orderId, scenario);

        Assert.Equal(expected, actual);
    }
}
