extern alias worker;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;
using RabbitMQ.Client;

namespace OrderSystem.IntegrationTests.Worker;

[Collection(RabbitMqCollectionDefinition.Name)]
public sealed class OutboxPublisherHostTests(RabbitMqFixture rabbitMq, PostgreSqlFixture postgres) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task Executable_DueOutboxIntent_PublishesAndMarksPublished()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var exchange = $"oims.outbox.host.{suffix}";
        var queue = $"oims.outbox.host.queue.{suffix}";
        var route = $"host.{suffix}";
        var connectionFactory = new ConnectionFactory
        {
            HostName = rabbitMq.Host,
            Port = rabbitMq.Port,
            VirtualHost = "/",
            UserName = rabbitMq.Username,
            Password = rabbitMq.Password
        };
        using var connectionProvider = new RabbitMqConnectionProvider(connectionFactory);
        var channelFactory = new RabbitMqChannelFactory(connectionProvider);
        await using (var channel = await channelFactory.CreateChannelAsync())
        {
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: false);
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
            await channel.QueueBindAsync(queue, exchange, route);
        }

        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var occurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var eventMessage = new OrderCreatedV1(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), occurredAt);
        var intent = new OutboxMessage(
            eventMessage.MessageId, eventMessage.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(eventMessage)),
            eventMessage.CorrelationId, occurredAt, exchange, route);
        await using (var seedContext = new OrderSystemDbContext(options))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.OutboxMessages.Add(intent);
            await seedContext.SaveChangesAsync();
        }

        var workerAssemblyPath = typeof(worker::OrderSystem.Worker.OutboxPublisherWorker).Assembly.Location;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(workerAssemblyPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(workerAssemblyPath);
        process.StartInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        process.StartInfo.Environment["Database__ConnectionString"] = postgres.ConnectionString;
        process.StartInfo.Environment["RabbitMq__Host"] = rabbitMq.Host;
        process.StartInfo.Environment["RabbitMq__Port"] = rabbitMq.Port.ToString(CultureInfo.InvariantCulture);
        process.StartInfo.Environment["RabbitMq__VirtualHost"] = "/";
        process.StartInfo.Environment["RabbitMq__Username"] = rabbitMq.Username;
        process.StartInfo.Environment["RabbitMq__Password"] = rabbitMq.Password;
        process.StartInfo.Environment["MessageRedrive__Enabled"] = "false";
        process.StartInfo.Environment.Remove("Jwt__SigningKey");
        process.StartInfo.Environment.Remove("Payment__FakeWebhookSecret");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) => CaptureOutput(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => CaptureOutput(eventArgs.Data);

        void CaptureOutput(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(line);
            }
        }

        try
        {
            Assert.True(process.Start(), "The Worker process could not be started");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            OutboxMessageStatus status;
            do
            {
                await using var assertionContext = new OrderSystemDbContext(options);
                status = await assertionContext.OutboxMessages.AsNoTracking()
                    .Where(message => message.MessageId == intent.MessageId)
                    .Select(message => message.Status)
                    .SingleAsync();
                if (status == OutboxMessageStatus.Published || process.HasExited)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            } while (DateTimeOffset.UtcNow < deadline);

            Assert.False(process.HasExited, "Worker process exited before Outbox publication");
            Assert.Equal(OutboxMessageStatus.Published, status);
            await using var channel = await channelFactory.CreateChannelAsync();
            var delivery = await channel.BasicGetAsync(queue, autoAck: true);
            Assert.NotNull(delivery);
            Assert.Equal(intent.MessageId.ToString("D"), delivery.BasicProperties.MessageId);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "OUTBOX-003")]
    [Trait("Requirement", "OUTBOX-004")]
    public async Task Executable_BrokerOutage_KeepsIntentPendingThenPublishesAfterRecovery()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var exchange = $"oims.outbox.recovery.{suffix}";
        var queue = $"oims.outbox.recovery.queue.{suffix}";
        var route = $"recovery.{suffix}";
        var connectionFactory = new ConnectionFactory
        {
            HostName = rabbitMq.Host,
            Port = rabbitMq.Port,
            VirtualHost = "/",
            UserName = rabbitMq.Username,
            Password = rabbitMq.Password
        };
        using var connectionProvider = new RabbitMqConnectionProvider(connectionFactory);
        var channelFactory = new RabbitMqChannelFactory(connectionProvider);
        await using (var channel = await channelFactory.CreateChannelAsync())
        {
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true);
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
            await channel.QueueBindAsync(queue, exchange, route);
        }

        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var occurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var eventMessage = new OrderCreatedV1(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), occurredAt);
        var intent = new OutboxMessage(
            eventMessage.MessageId, eventMessage.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(eventMessage)),
            eventMessage.CorrelationId, occurredAt, exchange, route);
        await using (var seedContext = new OrderSystemDbContext(options))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.OutboxMessages.Add(intent);
            await seedContext.SaveChangesAsync();
        }

        var workerAssemblyPath = typeof(worker::OrderSystem.Worker.OutboxPublisherWorker).Assembly.Location;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(workerAssemblyPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(workerAssemblyPath);
        process.StartInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        process.StartInfo.Environment["Database__ConnectionString"] = postgres.ConnectionString;
        process.StartInfo.Environment["RabbitMq__Host"] = rabbitMq.Host;
        process.StartInfo.Environment["RabbitMq__Port"] = rabbitMq.Port.ToString(CultureInfo.InvariantCulture);
        process.StartInfo.Environment["RabbitMq__VirtualHost"] = "/";
        process.StartInfo.Environment["RabbitMq__Username"] = rabbitMq.Username;
        process.StartInfo.Environment["RabbitMq__Password"] = rabbitMq.Password;
        process.StartInfo.Environment["MessageRedrive__Enabled"] = "false";
        process.StartInfo.Environment.Remove("Jwt__SigningKey");
        process.StartInfo.Environment.Remove("Payment__FakeWebhookSecret");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) => CaptureOutput(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => CaptureOutput(eventArgs.Data);

        void CaptureOutput(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(line);
            }
        }

        var brokerStopped = false;
        var processStarted = false;
        try
        {
            await rabbitMq.StopBrokerAsync();
            brokerStopped = true;
            processStarted = process.Start();
            Assert.True(processStarted, "The Worker process could not be started");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var outageDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
            OutboxMessage persisted;
            do
            {
                await using var assertionContext = new OrderSystemDbContext(options);
                persisted = await assertionContext.OutboxMessages.AsNoTracking()
                    .SingleAsync(message => message.MessageId == intent.MessageId);
                if ((persisted.Status == OutboxMessageStatus.Pending &&
                        persisted.LastFailureCategory == OutboxFailureCategory.BrokerUnavailable) || process.HasExited)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            } while (DateTimeOffset.UtcNow < outageDeadline);

            var exceptionTypes = Regex.Matches(output.ToString(), @"\b(?:[A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*Exception\b")
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal);
            Assert.False(process.HasExited, $"Worker exited while RabbitMQ was unavailable: {string.Join(", ", exceptionTypes)}");
            Assert.Equal(OutboxMessageStatus.Pending, persisted.Status);
            Assert.Equal(OutboxFailureCategory.BrokerUnavailable, persisted.LastFailureCategory);
            Assert.Equal(1, persisted.AttemptCount);
            Assert.Equal(intent.MessageId, persisted.MessageId);

            await rabbitMq.StartBrokerAsync();
            brokerStopped = false;
            var recoveryDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
            do
            {
                await using var assertionContext = new OrderSystemDbContext(options);
                persisted = await assertionContext.OutboxMessages.AsNoTracking()
                    .SingleAsync(message => message.MessageId == intent.MessageId);
                if (persisted.Status == OutboxMessageStatus.Published || process.HasExited)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            } while (DateTimeOffset.UtcNow < recoveryDeadline);

            Assert.False(process.HasExited, "Worker exited before Outbox recovery");
            Assert.Equal(OutboxMessageStatus.Published, persisted.Status);
            Assert.True(persisted.AttemptCount >= 2);
            await using var channel = await channelFactory.CreateChannelAsync();
            var delivery = await channel.BasicGetAsync(queue, autoAck: true);
            Assert.NotNull(delivery);
            Assert.Equal(intent.MessageId.ToString("D"), delivery.BasicProperties.MessageId);
        }
        finally
        {
            try
            {
                if (brokerStopped)
                {
                    await rabbitMq.StartBrokerAsync();
                }
            }
            finally
            {
                if (processStarted)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }

                    await process.WaitForExitAsync();
                }
            }
        }
    }
}
