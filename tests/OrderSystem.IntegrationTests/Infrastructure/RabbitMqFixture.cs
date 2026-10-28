using Testcontainers.RabbitMq;
using System.Globalization;
using System.Text;

namespace OrderSystem.IntegrationTests.Infrastructure;

public sealed class RabbitMqFixture : IAsyncLifetime
{
    private const int AmqpPort = 5672;
    private const long QueueCapacityTestDefaultBytes = 1_048_576;
    private readonly RabbitMqContainer _container;

    public RabbitMqFixture()
    {
        Password = TestCredentials.CreatePassword();
        var rabbitMqConfigurationDirectory = Path.Combine(AppContext.BaseDirectory, "rabbitmq");
        var definitions = RenderDefinitions(
            Path.Combine(rabbitMqConfigurationDirectory, "definitions.template.json"));
        _container = new RabbitMqBuilder("rabbitmq:4-management-alpine")
            .WithUsername(Username)
            .WithPassword(Password)
            .WithResourceMapping(
                definitions,
                "/etc/rabbitmq/definitions.json")
            .Build();
    }

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(AmqpPort);

    public string Username { get; } = "oims";

    public string Password { get; }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ExecuteRabbitMqControlAsync("import_definitions", "/etc/rabbitmq/definitions.json");
    }

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

    private async Task ExecuteRabbitMqControlAsync(params string[] arguments)
    {
        var command = new string[arguments.Length + 1];
        command[0] = "rabbitmqctl";
        arguments.CopyTo(command, 1);
        var result = await _container.ExecAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"rabbitmqctl {string.Join(' ', arguments)} failed with exit code {result.ExitCode}: {result.Stderr}");
        }
    }

    private static byte[] RenderDefinitions(string templatePath)
    {
        var capacity = QueueCapacityTestDefaultBytes.ToString(CultureInfo.InvariantCulture);
        var definitions = File.ReadAllText(templatePath)
            .Replace("__OIMS_MAIN_QUEUE_MAX_LENGTH_BYTES__", capacity, StringComparison.Ordinal)
            .Replace("__OIMS_RETRY_QUEUE_MAX_LENGTH_BYTES__", capacity, StringComparison.Ordinal)
            .Replace("__OIMS_DEAD_LETTER_QUEUE_MAX_LENGTH_BYTES__", capacity, StringComparison.Ordinal);

        return Encoding.UTF8.GetBytes(definitions);
    }
}

[CollectionDefinition(Name)]
public sealed class RabbitMqCollectionDefinition : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "RabbitMQ";
}
