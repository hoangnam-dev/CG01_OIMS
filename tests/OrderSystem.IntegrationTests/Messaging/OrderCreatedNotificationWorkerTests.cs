extern alias worker;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Domain.Notifications;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;
using RabbitMQ.Client;

namespace OrderSystem.IntegrationTests.Messaging;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class OrderCreatedNotificationWorkerTests(
    PostgreSqlFixture postgres,
    RabbitMqFixture rabbitMq) : IClassFixture<RabbitMqFixture>
{
    [Fact]
    [Trait("Requirement", "S9-T05")]
    [Trait("Requirement", "S9-T06")]
    [Trait("TestId", "MSG-CONSUME-001")]
    [Trait("TestId", "MSG-TRACE-001")]
    public async Task Worker_ValidOrderCreated_PersistsExpectedNotification()
    {
        var now = new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var exchangeName = $"oims.events.test.{Guid.NewGuid():N}";
        var routingKey = $"order.created.v1.test.{Guid.NewGuid():N}";
        var queueName = $"oims.notification.order-created.v1.test.{Guid.NewGuid():N}";

        var dbOptions = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using (var arrangeContext = new OrderSystemDbContext(dbOptions))
        {
            await arrangeContext.Database.MigrateAsync();
            arrangeContext.AddRange(
                new User(
                    userId,
                    $"notification-worker-{userId:N}@example.com",
                    $"notification-worker-{userId:N}@example.com",
                    TestCredentials.CreateHashPlaceholder(),
                    UserRole.Customer,
                    now),
                new Order(
                    orderId,
                    userId,
                    totalAmount: 0m,
                    reservationExpiresAt: now.AddMinutes(15),
                    createdAt: now));
            await arrangeContext.SaveChangesAsync();
        }

        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: messageId,
            OrderId: orderId,
            UserId: userId,
            CorrelationId: correlationId,
            OccurredAt: now);

        await PublishAsync(message, exchangeName, routingKey, queueName);

        using var process = StartWorker(exchangeName, routingKey, queueName);
        var output = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
            CaptureOutput(output, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) =>
            CaptureOutput(output, eventArgs.Data);

        try
        {
            Assert.True(process.Start(), "The Worker process could not be started.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var notification = await WaitForNotificationAsync(
                dbOptions,
                messageId,
                process,
                output,
                TimeSpan.FromSeconds(10));

            Assert.NotNull(notification);
            Assert.Equal(userId, notification.UserId);
            Assert.Equal(orderId, notification.OrderId);
            Assert.Equal(messageId, notification.MessageId);
            Assert.Equal(NotificationType.OrderCreated, notification.Type);
            Assert.NotEqual(default, notification.CreatedAt);
            var traceLine = await WaitForOutputLineAsync(
                process,
                output,
                $"MessageId {messageId:D}",
                TimeSpan.FromSeconds(5)
            );

            Assert.NotNull(traceLine);
            Assert.Contains($"CorrelationId {correlationId:D}", traceLine, StringComparison.Ordinal);
            Assert.Contains($"OrderId {orderId:D}", traceLine, StringComparison.Ordinal);
            Assert.Contains($"UserId {userId:D}", traceLine, StringComparison.Ordinal);
            Assert.Contains("ConsumerName OrderCreatedNotificationConsumer", traceLine, StringComparison.Ordinal);
            Assert.Contains("EventVersion 1", traceLine, StringComparison.Ordinal);
            Assert.DoesNotContain("Payload", traceLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Body", traceLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(rabbitMq.Password, traceLine, StringComparison.Ordinal);
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
    [Trait("Requirement", "S9-T05")]
    [Trait("Requirement", "S9-T06")]
    [Trait("TestId", "MSG-CONSUME-002")]
    public async Task Worker_DatabaseProcessingFails_StopsConsumerAndReturnsUnacknowledgedDelivery()
    {
        var now = new DateTimeOffset(2026, 10, 18, 8, 30, 0, TimeSpan.Zero);
        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var exchangeName = $"oims.events.test.{Guid.NewGuid():N}";
        var routingKey = $"order.created.v1.test.{Guid.NewGuid():N}";
        var queueName = $"oims.notification.order-created.v1.test.{Guid.NewGuid():N}";

        var dbOptions = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using (var arrangeContext = new OrderSystemDbContext(dbOptions))
        {
            await arrangeContext.Database.MigrateAsync();
        }

        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: messageId,
            OrderId: orderId,
            UserId: userId,
            CorrelationId: correlationId,
            OccurredAt: now);

        await PublishAsync(message, exchangeName, routingKey, queueName);

        using var process = StartWorker(exchangeName, routingKey, queueName);
        var output = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
            CaptureOutput(output, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) =>
            CaptureOutput(output, eventArgs.Data);

        try
        {
            Assert.True(process.Start(), "The Worker process could not be started.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var queueState = await WaitForStoppedConsumerWithReturnedMessageAsync(
                queueName,
                process,
                output,
                TimeSpan.FromSeconds(10));

            Assert.Equal(1u, queueState.MessageCount);
            Assert.Equal(0u, queueState.ConsumerCount);

            await using var assertionContext = new OrderSystemDbContext(dbOptions);
            Assert.False(await assertionContext.Notifications
                .AsNoTracking()
                .AnyAsync(candidate => candidate.MessageId == messageId));

            var traceLine = await WaitForOutputLineAsync(
                process,
                output,
                $"MessageId {messageId:D}",
                TimeSpan.FromSeconds(5));

            Assert.NotNull(traceLine);
            Assert.Contains($"CorrelationId {correlationId:D}", traceLine, StringComparison.Ordinal);
            Assert.Contains($"OrderId {orderId:D}", traceLine, StringComparison.Ordinal);
            Assert.Contains($"UserId {userId:D}", traceLine, StringComparison.Ordinal);
            Assert.Contains("ConsumerName OrderCreatedNotificationConsumer", traceLine, StringComparison.Ordinal);
            Assert.Contains("EventVersion 1", traceLine, StringComparison.Ordinal);

            string capturedOutput;
            lock (output)
            {
                capturedOutput = output.ToString();
            }

            var rawPayload = Encoding.UTF8.GetString(
                OrderCreatedV1Serializer.Serialize(message));

            Assert.DoesNotContain(rawPayload, capturedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(rabbitMq.Password, capturedOutput, StringComparison.Ordinal);
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
    [Trait("Requirement", "S9-T06")]
    [Trait("TestId", "MSG-CONTRACT-002")]
    public async Task Worker_InvalidOrderCreated_LogsSafeBrokerMetadataAndReturnsUnacknowledgedDelivery()
    {
        var messageId = Guid.NewGuid().ToString("D");
        var correlationId = Guid.NewGuid().ToString("D");
        var exchangeName = $"oims.events.test.{Guid.NewGuid():N}";
        var routingKey = $"order.created.v1.test.{Guid.NewGuid():N}";
        var queueName = $"oims.notification.order-created.v1.test.{Guid.NewGuid():N}";
        const string sensitiveMarker = "must-not-appear-in-worker-logs";
        var payload = Encoding.UTF8.GetBytes($$"""
            {"eventVersion":1,"secretMarker":"{{sensitiveMarker}}"}
            """);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            Type = "OrderCreated",
            Persistent = true,
            MessageId = messageId,
            CorrelationId = correlationId
        };

        await PublishAsync(payload, properties, exchangeName, routingKey, queueName);

        using var process = StartWorker(exchangeName, routingKey, queueName);
        var output = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) => CaptureOutput(output, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => CaptureOutput(output, eventArgs.Data);

        try
        {
            Assert.True(process.Start(), "The Worker process could not be started.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var queueState = await WaitForStoppedConsumerWithReturnedMessageAsync(
                queueName,
                process,
                output,
                TimeSpan.FromSeconds(10));

            Assert.Equal(1u, queueState.MessageCount);
            Assert.Equal(0u, queueState.ConsumerCount);

            var traceLine = await WaitForOutputLineAsync(
                process,
                output,
                $"MessageId {messageId}",
                TimeSpan.FromSeconds(5));

            Assert.NotNull(traceLine);
            Assert.Contains($"CorrelationId {correlationId}", traceLine, StringComparison.Ordinal);
            Assert.Contains("ConsumerName OrderCreatedNotificationConsumer", traceLine, StringComparison.Ordinal);
            Assert.Contains($"Exchange {exchangeName}", traceLine, StringComparison.Ordinal);
            Assert.Contains($"RoutingKey {routingKey}", traceLine, StringComparison.Ordinal);
            Assert.Contains($"Queue {queueName}", traceLine, StringComparison.Ordinal);

            string capturedOutput;
            lock (output)
            {
                capturedOutput = output.ToString();
            }

            Assert.DoesNotContain(sensitiveMarker, capturedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(Encoding.UTF8.GetString(payload), capturedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(rabbitMq.Password, capturedOutput, StringComparison.Ordinal);
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

    private async Task PublishAsync(
        OrderCreatedV1 message,
        string exchangeName,
        string routingKey,
        string queueName)
    {
        await PublishAsync(
            OrderCreatedV1Serializer.Serialize(message),
            OrderCreatedV1BasicPropertiesFactory.Create(message),
            exchangeName,
            routingKey,
            queueName);
    }

    private async Task PublishAsync(
        ReadOnlyMemory<byte> payload,
        BasicProperties properties,
        string exchangeName,
        string routingKey,
        string queueName)
    {
        var connectionFactory = new ConnectionFactory
        {
            HostName = rabbitMq.Host,
            Port = rabbitMq.Port,
            VirtualHost = "/",
            UserName = rabbitMq.Username,
            Password = rabbitMq.Password
        };

        await using var connection =
            await connectionFactory.CreateConnectionAsync();
        await using var channel =
            await connection.CreateChannelAsync();

        await channel.ExchangeDeclareAsync(
            exchangeName,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false);
        await channel.QueueDeclareAsync(
            queueName,
            durable: true,
            exclusive: false,
            autoDelete: false);
        await channel.QueueBindAsync(
            queueName,
            exchangeName,
            routingKey);
        await channel.BasicPublishAsync(
            exchangeName,
            routingKey,
            mandatory: true,
            properties,
            payload);
    }

    private Process StartWorker(
        string exchangeName,
        string routingKey,
        string queueName)
    {
        var workerAssemblyPath =
            typeof(worker::OrderSystem.Worker.ReservationExpirationWorker)
                .Assembly.Location;

        var process = new Process
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
        process.StartInfo.Environment["Database__ConnectionString"] =
            postgres.ConnectionString;
        process.StartInfo.Environment["RabbitMq__Host"] = rabbitMq.Host;
        process.StartInfo.Environment["RabbitMq__Port"] =
            rabbitMq.Port.ToString(CultureInfo.InvariantCulture);
        process.StartInfo.Environment["RabbitMq__VirtualHost"] = "/";
        process.StartInfo.Environment["RabbitMq__Username"] =
            rabbitMq.Username;
        process.StartInfo.Environment["RabbitMq__Password"] =
            rabbitMq.Password;
        process.StartInfo.Environment["RabbitMq__ExchangeName"] =
            exchangeName;
        process.StartInfo.Environment["RabbitMq__OrderCreatedRoutingKey"] =
            routingKey;
        process.StartInfo.Environment["RabbitMq__OrderCreatedQueueName"] =
            queueName;
        process.StartInfo.Environment.Remove("Jwt__SigningKey");
        process.StartInfo.Environment.Remove("Payment__FakeWebhookSecret");

        return process;
    }

    private static async Task<Notification?> WaitForNotificationAsync(
        DbContextOptions<OrderSystemDbContext> options,
        Guid messageId,
        Process process,
        StringBuilder output,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Worker exited before persisting the Notification" +
                    $"{Environment.NewLine}{output}");
            }

            await using var assertionContext =
                new OrderSystemDbContext(options);

            var notification = await assertionContext.Notifications
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate =>
                    candidate.MessageId == messageId);

            if (notification is not null)
            {
                return notification;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return null;
    }

    private static async Task<string?> WaitForOutputLineAsync(
        Process process,
        StringBuilder output,
        string expectedToken,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Worker exited before writing the expected trace log" +
                    $"{Environment.NewLine}{output}");
            }

            string? matchingLine;
            lock (output)
            {
                matchingLine = output
                    .ToString()
                    .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line =>
                        line.Contains(expectedToken, StringComparison.Ordinal));
            }

            if (matchingLine is not null)
            {
                return matchingLine;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return null;
    }

    private async Task<QueueDeclareOk> WaitForStoppedConsumerWithReturnedMessageAsync(
    string queueName,
    Process process,
    StringBuilder output,
    TimeSpan timeout)
    {
        var connectionFactory = new ConnectionFactory
        {
            HostName = rabbitMq.Host,
            Port = rabbitMq.Port,
            VirtualHost = "/",
            UserName = rabbitMq.Username,
            Password = rabbitMq.Password
        };

        await using var connection = await connectionFactory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        QueueDeclareOk? queueState = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Worker exited before returning the failed delivery" +
                    $"{Environment.NewLine}{output}");
            }

            queueState = await channel.QueueDeclarePassiveAsync(queueName);
            if (queueState.MessageCount == 1 && queueState.ConsumerCount == 0)
            {
                return queueState;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return queueState ?? throw new InvalidOperationException(
            "RabbitMQ queue state was not observed");
    }

    private static void CaptureOutput(
        StringBuilder output,
        string? line)
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
}
