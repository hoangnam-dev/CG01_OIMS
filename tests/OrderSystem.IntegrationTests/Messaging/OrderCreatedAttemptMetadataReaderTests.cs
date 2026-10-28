using System.Text;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Messaging;

namespace OrderSystem.IntegrationTests.Messaging;

public sealed class OrderCreatedAttemptMetadataReaderTests
{
    [Fact]
    [Trait("Requirement", "S10-T06")]
    public void Read_BusinessEventRouteWithProducerAttemptHeader_StartsAtAttemptOne()
    {
        var options = new RabbitMqOptions();
        var headers = new Dictionary<string, object?>
        {
            [OrderCreatedAttemptMetadataReader.AttemptHeaderName] = 3
        };

        var attempt = OrderCreatedAttemptMetadataReader.Read(
            options.ExchangeName,
            options.OrderCreatedRoutingKey,
            headers,
            options);

        Assert.Equal(1, attempt);
    }

    [Fact]
    [Trait("Requirement", "S10-T06")]
    public void Read_RetryIngressWithMatchingExpiredDeathEvidence_ReturnsAttemptTwo()
    {
        var options = new RabbitMqOptions();
        var headers = new Dictionary<string, object?>
        {
            [OrderCreatedAttemptMetadataReader.AttemptHeaderName] = 2,
            [OrderCreatedAttemptMetadataReader.DeathHeaderName] = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["queue"] = Encoding.UTF8.GetBytes(options.OrderCreatedRetryQueueName),
                ["reason"] = Encoding.UTF8.GetBytes("expired")
            }
        }
        };

        var attempt = OrderCreatedAttemptMetadataReader.Read(
            options.ConsumerIngressExchangeName,
            options.OrderCreatedConsumerRoutingKey,
            headers,
            options);

        Assert.Equal(2, attempt);
    }

    [Fact]
    [Trait("Requirement", "S10-T06")]
    public void Read_FinalRetryIngressWithMatchingExpiredDeathEvidence_ReturnsAttemptThree()
    {
        var options = new RabbitMqOptions();
        var headers = new Dictionary<string, object?>
        {
            [OrderCreatedAttemptMetadataReader.AttemptHeaderName] = 3,
            [OrderCreatedAttemptMetadataReader.DeathHeaderName] = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["queue"] = Encoding.UTF8.GetBytes(options.OrderCreatedRetryQueueName),
                ["reason"] = Encoding.UTF8.GetBytes("expired")
            }
        }
        };
        var attempt = 0;

        var exception = Record.Exception(() =>
            attempt = OrderCreatedAttemptMetadataReader.Read(
                options.ConsumerIngressExchangeName,
                options.OrderCreatedConsumerRoutingKey,
                headers,
                options));

        Assert.Null(exception);
        Assert.Equal(3, attempt);
    }

    [Fact]
    [Trait("Requirement", "S10-T06")]
    public void Read_SanitizedRedriveIngressWithoutTransportHeaders_StartsAtAttemptOne()
    {
        var options = new RabbitMqOptions();
        var attempt = 0;

        var exception = Record.Exception(() =>
            attempt = OrderCreatedAttemptMetadataReader.Read(
                options.ConsumerIngressExchangeName,
                options.OrderCreatedConsumerRoutingKey,
                headers: null,
                options));

        Assert.Null(exception);
        Assert.Equal(1, attempt);
    }
}