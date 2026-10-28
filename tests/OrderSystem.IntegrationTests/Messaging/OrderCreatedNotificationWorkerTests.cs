extern alias worker;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderSystem.Application.Common.Diagnostics;
using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Domain.Notifications;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Notifications;
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
    [Trait("Requirement", "S10-T03")]
    [Trait("TestId", "MSG-IDEM-001")]
    public async Task ConsumeOrderCreated_DuplicateMessageId_CreatesOneNotification()
    {
        const string consumerName = "OrderCreatedNotificationConsumer";
        var now = new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
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
                    $"notification-duplicate-{userId:N}@example.com",
                    $"notification-duplicate-{userId:N}@example.com",
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
            CorrelationId: Guid.NewGuid(),
            OccurredAt: now);

        await PublishAsync(message, exchangeName, routingKey, queueName);
        await PublishAsync(message, exchangeName, routingKey, queueName);

        using var process = StartWorker(exchangeName, routingKey, queueName);
        var output = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
            CaptureOutput(output, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) =>
            CaptureOutput(output, eventArgs.Data);

        try
        {
            Assert.True(
                process.Start(),
                "The Worker process could not be started.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var processedMessage = await WaitForProcessedMessageAsync(
                dbOptions,
                messageId,
                consumerName,
                process,
                output,
                TimeSpan.FromSeconds(10));

            Assert.NotNull(processedMessage);

            await using (var assertionContext =
                new OrderSystemDbContext(dbOptions))
            {
                var notifications = await assertionContext.Notifications
                    .AsNoTracking()
                    .Where(candidate => candidate.MessageId == messageId)
                    .ToListAsync();
                var processedMessages =
                    await assertionContext.ProcessedMessages
                        .AsNoTracking()
                        .Where(candidate =>
                            candidate.MessageId == messageId &&
                            candidate.ConsumerName == consumerName)
                        .ToListAsync();

                var notification = Assert.Single(notifications);
                Assert.Equal(userId, notification.UserId);
                Assert.Equal(orderId, notification.OrderId);
                Assert.Equal(messageId, notification.MessageId);
                Assert.Single(processedMessages);
            }

            var acknowledgementCount = await WaitForOutputCountAsync(
                process,
                output,
                $"processed and acknowledged for MessageId {messageId:D}",
                expectedCount: 2,
                TimeSpan.FromSeconds(10));

            Assert.Equal(2, acknowledgementCount);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();

            var queueState = await WaitForQueueStateAsync(
                queueName,
                expectedMessageCount: 0,
                expectedConsumerCount: 0,
                TimeSpan.FromSeconds(5));

            Assert.Equal(0u, queueState.MessageCount);
            Assert.Equal(0u, queueState.ConsumerCount);
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
    [Trait("Requirement", "S10-T03")]
    [Trait("TestId", "MSG-IDEM-002")]
    public async Task ConsumeOrderCreated_CommitSucceedsButAckIsLost_RedeliveryDoesNotRepeatEffect()
    {
        const string consumerName = "OrderCreatedNotificationConsumer";
        var now = new DateTimeOffset(2026, 10, 27, 8, 30, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
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
                    $"notification-ack-loss-{userId:N}@example.com",
                    $"notification-ack-loss-{userId:N}@example.com",
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
            CorrelationId: Guid.NewGuid(),
            OccurredAt: now);

        await PublishAsync(message, exchangeName, routingKey, queueName);

        var beforeAckHook = new BlockingCheckpointHook(
            RabbitMqOrderCreatedNotificationConsumer.AfterCommitBeforeAcknowledgementCheckpoint);
        using (var firstHost = CreateNotificationConsumerHost(
            exchangeName,
            routingKey,
            queueName,
            beforeAckHook))
        {
            await firstHost.StartAsync();
            try
            {
                Assert.True(
                    await beforeAckHook.WaitUntilReachedAsync(TimeSpan.FromSeconds(10)),
                    "The consumer did not reach the post-commit, pre-ACK checkpoint");

                await using var committedContext = new OrderSystemDbContext(dbOptions);
                Assert.Single(await committedContext.Notifications
                    .AsNoTracking()
                    .Where(candidate => candidate.MessageId == messageId)
                    .ToListAsync());
            }
            finally
            {
                await firstHost.StopAsync();
            }
        }

        var returnedState = await WaitForQueueStateAsync(
            queueName,
            expectedMessageCount: 1,
            expectedConsumerCount: 0,
            TimeSpan.FromSeconds(5));
        Assert.Equal(1u, returnedState.MessageCount);

        var loggerProvider = new RecordingLoggerProvider();
        using (var secondHost = CreateNotificationConsumerHost(
            exchangeName,
            routingKey,
            queueName,
            new PassThroughOperationHook(),
            loggerProvider))
        {
            await secondHost.StartAsync();
            try
            {
                Assert.True(
                    await loggerProvider.WaitForMessageAsync(
                        $"processed and acknowledged for MessageId {messageId:D}",
                        TimeSpan.FromSeconds(10)),
                    "The redelivered message was not acknowledged");
            }
            finally
            {
                await secondHost.StopAsync();
            }
        }

        var drainedState = await WaitForQueueStateAsync(
            queueName,
            expectedMessageCount: 0,
            expectedConsumerCount: 0,
            TimeSpan.FromSeconds(5));
        Assert.Equal(0u, drainedState.MessageCount);

        await using var assertionContext = new OrderSystemDbContext(dbOptions);
        Assert.Single(await assertionContext.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.MessageId == messageId)
            .ToListAsync());
        Assert.Single(await assertionContext.ProcessedMessages
            .AsNoTracking()
            .Where(candidate =>
                candidate.MessageId == messageId &&
                candidate.ConsumerName == consumerName)
            .ToListAsync());
    }

    [Fact(Skip = "Pending S10-T08 DLQ intake implementation")]
    [Trait("Requirement", "S10-T08")]
    [Trait("TestId", "MSG-DLQ-004")]
    public async Task DeadLetterIntake_CommitSucceedsButAckIsLost_RedeliveryKeepsCorrelatedEvidence()
    {
        const string consumerName = "OrderCreatedNotificationConsumer";
        const string deadLetterExchange = "oims.notification.dead-letter";
        const string deadLetterRoutingKey = "order-created.v1";
        const string deadLetterQueue = "oims.notification.order-created.v1.dlq";
        const string beforeAckCheckpoint = "messaging.order-created-dlq.after-commit-before-ack";
        const string afterAckCheckpoint = "messaging.order-created-dlq.after-ack";
        var messageId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var sourceExchange = $"oims.events.test.{Guid.NewGuid():N}";
        var sourceRoutingKey = $"order.created.v1.test.{Guid.NewGuid():N}";
        var sourceQueue = $"oims.notification.source.test.{Guid.NewGuid():N}";
        byte[] body = [];
        var bodySha256 = FailedMessageBodyFingerprint.Compute(body);
        var dbOptions = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using (var arrangeContext = new OrderSystemDbContext(dbOptions))
        {
            await arrangeContext.Database.MigrateAsync();
        }

        await DeadLetterMalformedMessageAsync(
            messageId,
            correlationId,
            body,
            sourceExchange,
            sourceRoutingKey,
            sourceQueue,
            deadLetterExchange,
            deadLetterRoutingKey,
            deadLetterQueue);

        var beforeAckHook = new BlockingCheckpointHook(beforeAckCheckpoint);
        using (var firstHost = CreateNotificationConsumerHost(
            sourceExchange,
            sourceRoutingKey,
            sourceQueue,
            beforeAckHook))
        {
            await firstHost.StartAsync();
            try
            {
                Assert.True(
                    await beforeAckHook.WaitUntilReachedAsync(TimeSpan.FromSeconds(10)),
                    "The DLQ intake did not commit before its acknowledgement checkpoint");

                await using var committedContext = new OrderSystemDbContext(dbOptions);
                var firstEvidence = await committedContext.FailedMessages
                    .AsNoTracking()
                    .Where(candidate =>
                        candidate.MessageId == messageId &&
                        candidate.BodySha256 == bodySha256)
                    .ToListAsync();

                Assert.Single(firstEvidence);
            }
            finally
            {
                await firstHost.StopAsync();
            }
        }

        var returnedState = await WaitForQueueStateAsync(
            deadLetterQueue,
            expectedMessageCount: 1,
            expectedConsumerCount: 0,
            TimeSpan.FromSeconds(5));
        Assert.Equal(1u, returnedState.MessageCount);

        var afterAckHook = new SignalingCheckpointHook(afterAckCheckpoint);
        using (var secondHost = CreateNotificationConsumerHost(
            sourceExchange,
            sourceRoutingKey,
            sourceQueue,
            afterAckHook))
        {
            await secondHost.StartAsync();
            try
            {
                Assert.True(
                    await afterAckHook.WaitUntilReachedAsync(TimeSpan.FromSeconds(10)),
                    "The redelivered DLQ message was not acknowledged");
            }
            finally
            {
                await secondHost.StopAsync();
            }
        }

        var drainedState = await WaitForQueueStateAsync(
            deadLetterQueue,
            expectedMessageCount: 0,
            expectedConsumerCount: 0,
            TimeSpan.FromSeconds(5));
        Assert.Equal(0u, drainedState.MessageCount);

        await using var assertionContext = new OrderSystemDbContext(dbOptions);
        var evidence = await assertionContext.FailedMessages
            .AsNoTracking()
            .Where(candidate =>
                candidate.MessageId == messageId &&
                candidate.BodySha256 == bodySha256)
            .OrderBy(candidate => candidate.CapturedAt)
            .ThenBy(candidate => candidate.Id)
            .ToListAsync();

        Assert.Equal(2, evidence.Count);
        Assert.Equal(2, evidence.Select(candidate => candidate.Id).Distinct().Count());
        Assert.All(evidence, failedMessage =>
        {
            Assert.Equal(consumerName, failedMessage.ConsumerName);
            Assert.Equal(correlationId, failedMessage.CorrelationId);
            Assert.Empty(failedMessage.Body);
            Assert.Equal(FailedMessageFailureKind.Malformed, failedMessage.FailureKind);
            Assert.Equal(1, failedMessage.AttemptCount);
            Assert.Equal(FailedMessageStatus.Pending, failedMessage.Status);
            Assert.Equal(sourceExchange, failedMessage.OriginalExchange);
            Assert.Equal(sourceRoutingKey, failedMessage.OriginalRoutingKey);
        });
    }

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

    private async Task DeadLetterMalformedMessageAsync(
        Guid messageId,
        Guid correlationId,
        ReadOnlyMemory<byte> body,
        string sourceExchange,
        string sourceRoutingKey,
        string sourceQueue,
        string deadLetterExchange,
        string deadLetterRoutingKey,
        string deadLetterQueue)
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

        await channel.ExchangeDeclareAsync(
            deadLetterExchange,
            ExchangeType.Direct,
            durable: true,
            autoDelete: false);
        await channel.QueueDeclareAsync(
            deadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum"
            });
        await channel.QueueBindAsync(
            deadLetterQueue,
            deadLetterExchange,
            deadLetterRoutingKey);
        await channel.QueuePurgeAsync(deadLetterQueue);

        await channel.ExchangeDeclareAsync(
            sourceExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false);
        await channel.QueueDeclareAsync(
            sourceQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = deadLetterExchange,
                ["x-dead-letter-routing-key"] = deadLetterRoutingKey
            });
        await channel.QueueBindAsync(sourceQueue, sourceExchange, sourceRoutingKey);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            Type = "OrderCreated",
            Persistent = true,
            MessageId = messageId.ToString("D"),
            CorrelationId = correlationId.ToString("D")
        };
        await channel.BasicPublishAsync(
            sourceExchange,
            sourceRoutingKey,
            mandatory: true,
            properties,
            body);

        var delivery = await channel.BasicGetAsync(sourceQueue, autoAck: false)
            ?? throw new InvalidOperationException("The source delivery was not observed");
        await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false);

        var deadLetterState = await WaitForQueueStateAsync(
            deadLetterQueue,
            expectedMessageCount: 1,
            expectedConsumerCount: 0,
            TimeSpan.FromSeconds(5));
        Assert.Equal(1u, deadLetterState.MessageCount);
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

    private IHost CreateNotificationConsumerHost(
        string exchangeName,
        string routingKey,
        string queueName,
        IOperationHook operationHook,
        ILoggerProvider? loggerProvider = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = postgres.ConnectionString,
            ["RabbitMq:Host"] = rabbitMq.Host,
            ["RabbitMq:Port"] = rabbitMq.Port.ToString(CultureInfo.InvariantCulture),
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:Username"] = rabbitMq.Username,
            ["RabbitMq:Password"] = rabbitMq.Password,
            ["RabbitMq:ExchangeName"] = exchangeName,
            ["RabbitMq:OrderCreatedRoutingKey"] = routingKey,
            ["RabbitMq:OrderCreatedQueueName"] = queueName
        };

        return new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(settings);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLogging(builder =>
                {
                    if (loggerProvider is not null)
                    {
                        builder.AddProvider(loggerProvider);
                    }
                });
                services
                    .AddOrderSystemPersistence(context.Configuration)
                    .AddOrderSystemCommonInfrastructure()
                    .AddOrderSystemMessagingInfrastructure(context.Configuration)
                    .AddOrderSystemNotificationConsumer();
                services.RemoveAll<IOperationHook>();
                services.AddSingleton(operationHook);
            })
            .Build();
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

    private static async Task<ProcessedMessage?>
WaitForProcessedMessageAsync(
    DbContextOptions<OrderSystemDbContext> options,
    Guid messageId,
    string consumerName,
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
                    $"Worker exited before persisting the ProcessedMessage" +
                    $"{Environment.NewLine}{output}");
            }

            await using var assertionContext =
                new OrderSystemDbContext(options);
            var processedMessage = await assertionContext.ProcessedMessages
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate =>
                    candidate.MessageId == messageId &&
                    candidate.ConsumerName == consumerName);

            if (processedMessage is not null)
            {
                return processedMessage;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return null;
    }

    private static async Task<int> WaitForOutputCountAsync(
        Process process,
        StringBuilder output,
        string expectedToken,
        int expectedCount,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        var matchingCount = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Worker exited before writing the expected trace logs" +
                    $"{Environment.NewLine}{output}");
            }

            lock (output)
            {
                matchingCount = output
                    .ToString()
                    .Split(
                        Environment.NewLine,
                        StringSplitOptions.RemoveEmptyEntries)
                    .Count(line =>
                        line.Contains(
                            expectedToken,
                            StringComparison.Ordinal));
            }

            if (matchingCount >= expectedCount)
            {
                return matchingCount;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return matchingCount;
    }

    private async Task<QueueDeclareOk> WaitForQueueStateAsync(
        string queueName,
        uint expectedMessageCount,
        uint expectedConsumerCount,
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

        await using var connection =
            await connectionFactory.CreateConnectionAsync();
        await using var channel =
            await connection.CreateChannelAsync();
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        QueueDeclareOk? queueState = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            queueState =
                await channel.QueueDeclarePassiveAsync(queueName);

            if (queueState.MessageCount == expectedMessageCount &&
                queueState.ConsumerCount == expectedConsumerCount)
            {
                return queueState;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return queueState ?? throw new InvalidOperationException(
            "RabbitMQ queue state was not observed");
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

    private sealed class BlockingCheckpointHook(string targetCheckpoint) : IOperationHook
    {
        private readonly TaskCompletionSource reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ReachAsync(string checkpoint, CancellationToken cancellationToken)
        {
            if (!string.Equals(checkpoint, targetCheckpoint, StringComparison.Ordinal))
            {
                return;
            }

            reached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public async Task<bool> WaitUntilReachedAsync(TimeSpan timeout)
        {
            try
            {
                await reached.Task.WaitAsync(timeout);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    private sealed class PassThroughOperationHook : IOperationHook
    {
        public Task ReachAsync(string checkpoint, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class SignalingCheckpointHook(string targetCheckpoint) : IOperationHook
    {
        private readonly TaskCompletionSource reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReachAsync(string checkpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(checkpoint, targetCheckpoint, StringComparison.Ordinal))
            {
                reached.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public async Task<bool> WaitUntilReachedAsync(TimeSpan timeout)
        {
            try
            {
                await reached.Task.WaitAsync(timeout);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> messages = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(messages);

        public void Dispose()
        {
        }

        public async Task<bool> WaitForMessageAsync(string expectedToken, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (messages.Any(message =>
                    message.Contains(expectedToken, StringComparison.Ordinal)))
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            return false;
        }

        private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
