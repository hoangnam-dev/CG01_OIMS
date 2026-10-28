using Testcontainers.RabbitMq;

namespace OrderSystem.IntegrationTests.Infrastructure;

public sealed class RabbitMqFixture : IAsyncLifetime
{
    private const int AmqpPort = 5672;
    private readonly RabbitMqContainer _container;

    public RabbitMqFixture()
    {
        Password = TestCredentials.CreatePassword();
        _container = new RabbitMqBuilder("rabbitmq:4-management-alpine")
            .WithUsername(Username)
            .WithPassword(Password)
            .Build();
    }

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(AmqpPort);

    public string Username { get; } = "oims";

    public string Password { get; }

    public Task InitializeAsync() => _container.StartAsync();

    public Task StartBrokerAsync() => ExecuteRabbitMqControlAsync("start_app");

    public Task StopBrokerAsync() => ExecuteRabbitMqControlAsync("stop_app");

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<string> ListPoliciesAsync()
    {
        var result = await _container.ExecAsync(["rabbitmqctl", "list_policies", "--formatter=json"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"rabbitmqctl list_policies failed with exit code {result.ExitCode}: {result.Stderr}");
        }

        return result.Stdout;
    }

    private async Task ExecuteRabbitMqControlAsync(string command)
    {
        var result = await _container.ExecAsync(["rabbitmqctl", command]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"rabbitmqctl {command} failed with exit code {result.ExitCode}: {result.Stderr}");
        }
    }
}

[CollectionDefinition(Name)]
public sealed class RabbitMqCollectionDefinition : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "RabbitMQ";
}
