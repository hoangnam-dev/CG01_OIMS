using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Products.Caching;
using StackExchange.Redis;

namespace OrderSystem.IntegrationTests.Products.Caching;

public sealed class RedisProductDetailCacheFailureTests
{
    [Fact]
    public async Task RemoveAsync_RedisUnavailable_RecordsBoundedFailureMetricAndDoesNotThrow()
    {
        const string meterName =
            "OrderSystem.Infrastructure.Products.Cache";
        const string counterName =
            "oims.product_cache.operations";

        const string redisPassword = "cache-test-password";

        var redisOptions = new RedisOptions
        {
            Configuration = $"127.0.0.1:1,password={redisPassword},connectRetry=0",
            KeyPrefix = $"test-{Guid.NewGuid():N}",
            ProductTtl = TimeSpan.FromMinutes(5),
            OperationTimeout = TimeSpan.FromMilliseconds(100),
            ConnectTimeout = TimeSpan.FromMilliseconds(100)
        };

        var connectionOptions =
            RedisConnectionOptionsFactory.Create(redisOptions);

        using var connection =
            await ConnectionMultiplexer.ConnectAsync(connectionOptions);

        var logger =
            new RecordingLogger<RedisProductDetailCache>();

        var cache = new RedisProductDetailCache(
            connection,
            new ProductCacheKeyBuilder(redisOptions.KeyPrefix),
            redisOptions.ProductTtl,
            logger);

        var measurements =
            new ConcurrentQueue<(
                long Value,
                KeyValuePair<string, object?>[] Tags)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == meterName &&
                    instrument.Name == counterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>(
            (_, measurement, tags, _) =>
            {
                measurements.Enqueue(
                    (
                        measurement,
                        tags.ToArray()));
            });

        listener.Start();

        var productId = Guid.NewGuid();

        var exception = await Record.ExceptionAsync(
            () => cache.RemoveAsync(productId));

        Assert.Null(exception);

        var recorded = Assert.Single(
            measurements,
            measurement =>
                measurement.Tags.Any(
                    tag =>
                        tag.Key == "operation" &&
                        Equals(tag.Value, "invalidation")) &&
                measurement.Tags.Any(
                    tag =>
                        tag.Key == "outcome" &&
                        Equals(tag.Value, "failure")));

        Assert.Equal(1, recorded.Value);
        Assert.Equal(2, recorded.Tags.Length);

        Assert.Contains(
            recorded.Tags,
            tag =>
                tag.Key == "operation" &&
                Equals(tag.Value, "invalidation"));

        Assert.Contains(
            recorded.Tags,
            tag =>
                tag.Key == "outcome" &&
                Equals(tag.Value, "failure"));

        Assert.DoesNotContain(
            recorded.Tags,
            tag =>
                tag.Key.Contains(
                    "product",
                    StringComparison.OrdinalIgnoreCase) ||
                Equals(tag.Value, productId));

        var warning = Assert.Single(
            logger.Entries,
            entry =>
                entry.Level == LogLevel.Warning &&
                entry.EventId.Name == "CacheInvalidationFailed");

        Assert.Equal(
            productId,
            warning.Properties["ProductId"]);

        Assert.True(
            warning.Exception is RedisException or TimeoutException);

        Assert.DoesNotContain(
            redisPassword,
            warning.Message,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            redisPassword,
            warning.Exception?.ToString() ?? string.Empty,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            warning.Properties,
            property =>
                property.Value?.ToString()?.Contains(
                    redisPassword,
                    StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ReadAsync_RedisUnavailable_ReturnsUnavailableAndRecordsBoundedUnavailableMetric()
    {
        const string redisPassword = "cache-test-password";

        var redisOptions = new RedisOptions
        {
            Configuration = $"127.0.0.1:1,password={redisPassword},connectRetry=0",
            KeyPrefix = $"test-{Guid.NewGuid():N}",
            ProductTtl = TimeSpan.FromMinutes(5),
            OperationTimeout = TimeSpan.FromMilliseconds(100),
            ConnectTimeout = TimeSpan.FromMilliseconds(100)
        };

        var connectionOptions =
            RedisConnectionOptionsFactory.Create(redisOptions);

        using var connection =
            await ConnectionMultiplexer.ConnectAsync(connectionOptions);

        var logger =
            new RecordingLogger<RedisProductDetailCache>();

        var cache = new RedisProductDetailCache(
            connection,
            new ProductCacheKeyBuilder(redisOptions.KeyPrefix),
            redisOptions.ProductTtl,
            logger);

        var measurements =
            new ConcurrentQueue<(
                long Value,
                KeyValuePair<string, object?>[] Tags)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name ==
                        ProductCacheMetrics.MeterName &&
                    instrument.Name ==
                        ProductCacheMetrics.OperationCounterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>(
            (_, measurement, tags, _) =>
            {
                measurements.Enqueue(
                    (
                        measurement,
                        tags.ToArray()));
            });

        listener.Start();

        var productId = Guid.NewGuid();
        ProductCacheReadResult? result = null;

        var exception = await Record.ExceptionAsync(async () =>
        {
            result = await cache.ReadAsync(
                productId,
                CancellationToken.None);
        });

        Assert.Null(exception);

        var actualResult =
            Assert.IsType<ProductCacheReadResult>(result);

        Assert.Equal(
            ProductCacheReadStatus.Unavailable,
            actualResult.Status);

        Assert.Null(actualResult.Product);

        Assert.Contains(
            measurements,
            measurement =>
                measurement.Value == 1 &&
                measurement.Tags.Length == 2 &&
                measurement.Tags.Any(
                    tag =>
                        tag.Key == "operation" &&
                        Equals(tag.Value, "read")) &&
                measurement.Tags.Any(
                    tag =>
                        tag.Key == "outcome" &&
                        Equals(tag.Value, "unavailable")));

        Assert.DoesNotContain(
            measurements.SelectMany(
                measurement => measurement.Tags),
            tag =>
                tag.Key.Contains(
                    "product",
                    StringComparison.OrdinalIgnoreCase) ||
                Equals(tag.Value, productId));

        var warning = Assert.Single(
            logger.Entries,
            entry =>
                entry.Level == LogLevel.Warning &&
                entry.EventId.Name == "CacheReadFailed");

        Assert.Equal(
            productId,
            warning.Properties["ProductId"]);

        Assert.True(
            warning.Exception is RedisException or TimeoutException);

        Assert.DoesNotContain(
            redisPassword,
            warning.Message,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            redisPassword,
            warning.Exception?.ToString() ?? string.Empty,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            warning.Properties,
            property =>
                property.Value?.ToString()?.Contains(
                    redisPassword,
                    StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task WriteAsync_RedisUnavailable_RecordsBoundedFailureMetricAndDoesNotThrow()
    {
        var redisOptions = new RedisOptions
        {
            Configuration = "127.0.0.1:1,connectRetry=0",
            KeyPrefix = $"test-{Guid.NewGuid():N}",
            ProductTtl = TimeSpan.FromMinutes(5),
            OperationTimeout = TimeSpan.FromMilliseconds(100),
            ConnectTimeout = TimeSpan.FromMilliseconds(100)
        };

        var connectionOptions =
            RedisConnectionOptionsFactory.Create(redisOptions);

        using var connection =
            await ConnectionMultiplexer.ConnectAsync(connectionOptions);

        var cache = new RedisProductDetailCache(
            connection,
            new ProductCacheKeyBuilder(redisOptions.KeyPrefix),
            redisOptions.ProductTtl,
            NullLogger<RedisProductDetailCache>.Instance);

        var timestamp = new DateTimeOffset(
            2026,
            10,
            14,
            10,
            0,
            0,
            TimeSpan.FromHours(7));

        var product = new ProductDetailDto(
            Guid.NewGuid(),
            "Failure Isolation Product",
            "Product returned from PostgreSQL while Redis is unavailable",
            "Active",
            [],
            timestamp,
            timestamp);

        var measurements =
            new ConcurrentQueue<(
                long Value,
                KeyValuePair<string, object?>[] Tags)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name ==
                        ProductCacheMetrics.MeterName &&
                    instrument.Name ==
                        ProductCacheMetrics.OperationCounterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>(
            (_, measurement, tags, _) =>
            {
                measurements.Enqueue(
                    (
                        measurement,
                        tags.ToArray()));
            });

        listener.Start();

        var exception = await Record.ExceptionAsync(
            () => cache.WriteAsync(
                product,
                CancellationToken.None));

        Assert.Null(exception);

        Assert.Contains(
            measurements,
            measurement =>
                measurement.Value == 1 &&
                measurement.Tags.Length == 2 &&
                measurement.Tags.Any(
                    tag =>
                        tag.Key == "operation" &&
                        Equals(tag.Value, "write")) &&
                measurement.Tags.Any(
                    tag =>
                        tag.Key == "outcome" &&
                        Equals(tag.Value, "failure")));

        Assert.DoesNotContain(
            measurements.SelectMany(
                measurement => measurement.Tags),
            tag =>
                tag.Key.Contains(
                    "product",
                    StringComparison.OrdinalIgnoreCase) ||
                Equals(tag.Value, product.Id));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<LogEntry> entries = [];

        public IReadOnlyCollection<LogEntry> Entries =>
            entries.ToArray();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NoOpScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            IReadOnlyDictionary<string, object?> properties =
                state is IEnumerable<KeyValuePair<string, object?>> values
                    ? values.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value)
                    : new Dictionary<string, object?>();

            entries.Enqueue(
                new LogEntry(
                    logLevel,
                    eventId,
                    formatter(state, exception),
                    exception,
                    properties));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class NoOpScope : IDisposable
    {
        public static NoOpScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
