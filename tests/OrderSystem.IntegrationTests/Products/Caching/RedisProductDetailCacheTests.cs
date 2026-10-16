using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Infrastructure.Products.Caching;
using OrderSystem.IntegrationTests.Infrastructure;
using StackExchange.Redis;

namespace OrderSystem.IntegrationTests.Products.Caching;

[Collection(RedisCollectionDefinition.Name)]
public sealed class RedisProductDetailCacheTests(RedisFixture redisFixture)
{
    [Fact]
    [Trait("Requirement", "CACHE-005")]
    public async Task WriteAsync_Product_StoresOnlyApprovedProductPayload()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(redisFixture.ConnectionString);

        var keyPrefix = $"test-{Guid.NewGuid():N}";
        var keyBuilder = new ProductCacheKeyBuilder(keyPrefix);
        var productId = Guid.Parse("0199a5f2-8b72-7bd1-9f91-1f40c94356c2");
        var createdAt = new DateTimeOffset(2026, 10, 16, 8, 30, 0, TimeSpan.FromHours(7));
        var variant = new ProductVariantDto(
            Guid.Parse("0299a5f2-8b72-7bd1-9f91-1f40c94356c3"),
            productId,
            "CACHE-005-SKU",
            "Cached Variant",
            1_299_000m,
            "Active",
            createdAt,
            createdAt.AddMinutes(15));
        var product = new ProductDetailDto(
            productId,
            "Cached Product",
            "Product-only Redis payload",
            "Active",
            [variant],
            createdAt,
            createdAt.AddMinutes(15));
        var cache = new RedisProductDetailCache(
            connection,
            keyBuilder,
            TimeSpan.FromMinutes(5),
            NullLogger<RedisProductDetailCache>.Instance);

        await cache.WriteAsync(product, CancellationToken.None);

        var cachedValue = await connection.GetDatabase().StringGetAsync(keyBuilder.BuildProductDetailKey(productId));
        Assert.False(cachedValue.IsNull);

        byte[]? payload = cachedValue;
        Assert.NotNull(payload);

        using var document = JsonDocument.Parse(payload);
        var productProperties = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            [
                "createdAt",
                "description",
                "id",
                "name",
                "status",
                "updatedAt",
                "variants"
            ],
            productProperties);

        var serializedVariant = Assert.Single(document.RootElement.GetProperty("variants").EnumerateArray());
        var variantProperties = serializedVariant
            .EnumerateObject()
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            [
                "createdAt",
                "currentPrice",
                "id",
                "name",
                "productId",
                "sku",
                "status",
                "updatedAt"
            ],
            variantProperties);
    }

    [Fact]
    public async Task RemoveAsync_ExistingKey_DeletesKeyAndRecordsBoundedSuccessMetric()
    {
        using var connection =
            await ConnectionMultiplexer.ConnectAsync(
                redisFixture.ConnectionString);

        var keyPrefix = $"test-{Guid.NewGuid():N}";
        var keyBuilder = new ProductCacheKeyBuilder(keyPrefix);
        var productId = Guid.NewGuid();
        var database = connection.GetDatabase();
        var key = keyBuilder.BuildProductDetailKey(productId);

        var stored = await database.StringSetAsync(
            key,
            "cached-product");

        Assert.True(stored);

        var cache = new RedisProductDetailCache(
            connection,
            keyBuilder,
            TimeSpan.FromMinutes(5),
            NullLogger<RedisProductDetailCache>.Instance);

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

        await cache.RemoveAsync(productId);

        var existsAfterRemoval =
            await database.KeyExistsAsync(key);

        Assert.False(existsAfterRemoval);

        Assert.Contains(
            measurements,
            measurement =>
                measurement.Value == 1 &&
                measurement.Tags.Length == 2 &&
                measurement.Tags.Any(
                    tag =>
                        tag.Key == "operation" &&
                        Equals(tag.Value, "invalidation")) &&
                measurement.Tags.Any(
                    tag =>
                        tag.Key == "outcome" &&
                        Equals(tag.Value, "success")));

        Assert.DoesNotContain(
            measurements.SelectMany(
                measurement => measurement.Tags),
            tag =>
                tag.Key.Contains(
                    "product",
                    StringComparison.OrdinalIgnoreCase) ||
                Equals(tag.Value, productId));
    }

    [Fact]
    public async Task ReadAsync_ValidPayload_RecordsBoundedHitMetric()
    {
        using var connection =
            await ConnectionMultiplexer.ConnectAsync(
                redisFixture.ConnectionString);

        var keyPrefix = $"test-{Guid.NewGuid():N}";
        var keyBuilder = new ProductCacheKeyBuilder(keyPrefix);
        var productId = Guid.NewGuid();
        var timestamp = new DateTimeOffset(
            2026,
            10,
            15,
            14,
            0,
            0,
            TimeSpan.Zero);
        var product = new ProductDetailDto(
            productId,
            "Metric Hit Product",
            "Product used to verify bounded cache-hit telemetry",
            "Active",
            [],
            timestamp,
            timestamp);
        var payload =
            ProductDetailCacheSerializer.Serialize(product);
        var key =
            keyBuilder.BuildProductDetailKey(productId);

        var stored = await connection
            .GetDatabase()
            .StringSetAsync(key, payload);

        Assert.True(stored);

        var cache = new RedisProductDetailCache(
            connection,
            keyBuilder,
            TimeSpan.FromMinutes(5),
            NullLogger<RedisProductDetailCache>.Instance);

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

        var result = await cache.ReadAsync(
            productId,
            CancellationToken.None);

        Assert.Equal(
            ProductCacheReadStatus.Hit,
            result.Status);

        Assert.Equal(
            productId,
            result.Product!.Id);

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
                        Equals(tag.Value, "hit")));

        Assert.DoesNotContain(
            measurements.SelectMany(
                measurement => measurement.Tags),
            tag =>
                tag.Key.Contains(
                    "product",
                    StringComparison.OrdinalIgnoreCase) ||
                Equals(tag.Value, productId));
    }

    [Fact]
    public async Task ReadAsync_KeyDoesNotExist_ReturnsMissAndRecordsBoundedMissMetric()
    {
        using var connection =
            await ConnectionMultiplexer.ConnectAsync(
                redisFixture.ConnectionString);

        var keyPrefix = $"test-{Guid.NewGuid():N}";
        var keyBuilder = new ProductCacheKeyBuilder(keyPrefix);
        var productId = Guid.NewGuid();

        var cache = new RedisProductDetailCache(
            connection,
            keyBuilder,
            TimeSpan.FromMinutes(5),
            NullLogger<RedisProductDetailCache>.Instance);

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

        var result = await cache.ReadAsync(
            productId,
            CancellationToken.None);

        Assert.Equal(
            ProductCacheReadStatus.Miss,
            result.Status);

        Assert.Null(result.Product);

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
                        Equals(tag.Value, "miss")));

        Assert.DoesNotContain(
            measurements.SelectMany(
                measurement => measurement.Tags),
            tag =>
                tag.Key.Contains(
                    "product",
                    StringComparison.OrdinalIgnoreCase) ||
                Equals(tag.Value, productId));
    }

    [Fact]
    public async Task ReadAsync_ValidPayload_ReturnsHitWithDeserializedProduct()
    {
        using var connection =
            await ConnectionMultiplexer.ConnectAsync(redisFixture.ConnectionString);

        var keyPrefix = $"test-{Guid.NewGuid():N}";
        var keyBuilder = new ProductCacheKeyBuilder(keyPrefix);

        var productId = Guid.Parse(
            "0199a5f2-8b72-7bd1-9f91-1f40c94356c2");

        var createdAt = new DateTimeOffset(
            2026,
            10,
            14,
            8,
            30,
            0,
            TimeSpan.FromHours(7));

        var updatedAt = createdAt.AddMinutes(15);

        var expectedVariant = new ProductVariantDto(
            Guid.Parse("0299a5f2-8b72-7bd1-9f91-1f40c94356c3"),
            productId,
            "KB-RED-001",
            "Mechanical Keyboard Red Switch",
            1_299_000m,
            "Active",
            createdAt,
            updatedAt);

        var expectedProduct = new ProductDetailDto(
            productId,
            "Mechanical Keyboard",
            "Cached Product detail",
            "Active",
            [expectedVariant],
            createdAt,
            updatedAt);

        var key = keyBuilder.BuildProductDetailKey(productId);
        var payload = ProductDetailCacheSerializer.Serialize(expectedProduct);

        var stored = await connection
            .GetDatabase()
            .StringSetAsync(key, payload);

        Assert.True(stored);

        var cache = new RedisProductDetailCache(connection, keyBuilder, TimeSpan.FromMinutes(5), NullLogger<RedisProductDetailCache>.Instance);

        var result = await cache.ReadAsync(
            productId,
            CancellationToken.None);

        Assert.Equal(ProductCacheReadStatus.Hit, result.Status);

        var actualProduct = Assert.IsType<ProductDetailDto>(result.Product);

        Assert.Equal(expectedProduct.Id, actualProduct.Id);
        Assert.Equal(expectedProduct.Name, actualProduct.Name);
        Assert.Equal(expectedProduct.Description, actualProduct.Description);
        Assert.Equal(expectedProduct.Status, actualProduct.Status);
        Assert.Equal(expectedProduct.CreatedAt, actualProduct.CreatedAt);
        Assert.Equal(expectedProduct.UpdatedAt, actualProduct.UpdatedAt);

        var actualVariant = Assert.Single(actualProduct.Variants);

        Assert.Equal(expectedVariant.Id, actualVariant.Id);
        Assert.Equal(expectedVariant.ProductId, actualVariant.ProductId);
        Assert.Equal(expectedVariant.Sku, actualVariant.Sku);
        Assert.Equal(expectedVariant.Name, actualVariant.Name);
        Assert.Equal(expectedVariant.CurrentPrice, actualVariant.CurrentPrice);
        Assert.Equal(expectedVariant.Status, actualVariant.Status);
        Assert.Equal(expectedVariant.CreatedAt, actualVariant.CreatedAt);
        Assert.Equal(expectedVariant.UpdatedAt, actualVariant.UpdatedAt);
    }

    [Fact]
    public async Task ReadAsync_CorruptPayload_DeletesKeyReturnsMissAndRecordsBoundedCorruptMetric()
    {
        using var connection =
            await ConnectionMultiplexer.ConnectAsync(
                redisFixture.ConnectionString);

        var keyPrefix = $"test-{Guid.NewGuid():N}";
        var keyBuilder = new ProductCacheKeyBuilder(keyPrefix);
        var productId = Guid.NewGuid();
        var key = keyBuilder.BuildProductDetailKey(productId);
        var database = connection.GetDatabase();
        var corruptPayload = "{not-valid-json"u8.ToArray();

        var stored = await database.StringSetAsync(
            key,
            corruptPayload);

        Assert.True(stored);

        var cache = new RedisProductDetailCache(
            connection,
            keyBuilder,
            TimeSpan.FromMinutes(5),
            NullLogger<RedisProductDetailCache>.Instance);

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

        var result = await cache.ReadAsync(
            productId,
            CancellationToken.None);

        Assert.Equal(
            ProductCacheReadStatus.Miss,
            result.Status);

        Assert.Null(result.Product);

        var remainingValue =
            await database.StringGetAsync(key);

        Assert.True(remainingValue.IsNull);

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
                        Equals(tag.Value, "corrupt")));

        Assert.DoesNotContain(
            measurements.SelectMany(
                measurement => measurement.Tags),
            tag =>
                tag.Key.Contains(
                    "product",
                    StringComparison.OrdinalIgnoreCase) ||
                Equals(tag.Value, productId));
    }

    [Fact]
    public async Task WriteAsync_Product_StoresSerializedPayloadAndRecordsBoundedSuccessMetric()
    {
        using var connection =
            await ConnectionMultiplexer.ConnectAsync(
                redisFixture.ConnectionString);

        var keyPrefix = $"test-{Guid.NewGuid():N}";
        var keyBuilder = new ProductCacheKeyBuilder(keyPrefix);
        var productId = Guid.NewGuid();

        var createdAt = new DateTimeOffset(
            2026,
            10,
            14,
            9,
            0,
            0,
            TimeSpan.FromHours(7));

        var product = new ProductDetailDto(
            productId,
            "Wireless Mouse",
            "Active Product written through RedisProductDetailCache",
            "Active",
            [],
            createdAt,
            createdAt.AddMinutes(10));

        var cache = new RedisProductDetailCache(
            connection,
            keyBuilder,
            TimeSpan.FromMinutes(5),
            NullLogger<RedisProductDetailCache>.Instance);

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

        await cache.WriteAsync(
            product,
            CancellationToken.None);

        var key = keyBuilder.BuildProductDetailKey(productId);
        var cachedValue = await connection
            .GetDatabase()
            .StringGetAsync(key);

        Assert.False(cachedValue.IsNull);

        byte[]? payload = cachedValue;

        Assert.NotNull(payload);

        var deserialized =
            ProductDetailCacheSerializer.TryDeserialize(
                payload,
                out var cachedProduct);

        Assert.True(deserialized);

        var actualProduct =
            Assert.IsType<ProductDetailDto>(cachedProduct);

        Assert.Equal(product.Id, actualProduct.Id);
        Assert.Equal(product.Name, actualProduct.Name);
        Assert.Equal(product.Description, actualProduct.Description);
        Assert.Equal(product.Status, actualProduct.Status);
        Assert.Equal(product.CreatedAt, actualProduct.CreatedAt);
        Assert.Equal(product.UpdatedAt, actualProduct.UpdatedAt);
        Assert.Empty(actualProduct.Variants);

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
                        Equals(tag.Value, "success")));

        Assert.DoesNotContain(
            measurements.SelectMany(
                measurement => measurement.Tags),
            tag =>
                tag.Key.Contains(
                    "product",
                    StringComparison.OrdinalIgnoreCase) ||
                Equals(tag.Value, productId));
    }

    [Fact]
    public async Task WriteAsync_Product_AppliesConfiguredAbsoluteTtl()
    {
        using var connection =
            await ConnectionMultiplexer.ConnectAsync(redisFixture.ConnectionString);

        var keyPrefix = $"test-{Guid.NewGuid():N}";
        var keyBuilder = new ProductCacheKeyBuilder(keyPrefix);
        var configuredTtl = TimeSpan.FromSeconds(30);
        var productId = Guid.NewGuid();

        var createdAt = new DateTimeOffset(
            2026,
            10,
            14,
            9,
            30,
            0,
            TimeSpan.FromHours(7));

        var product = new ProductDetailDto(
            productId,
            "USB-C Hub",
            "Active Product with configured cache TTL",
            "Active",
            [],
            createdAt,
            createdAt);

        var cache = new RedisProductDetailCache(
            connection,
            keyBuilder,
            configuredTtl,
            NullLogger<RedisProductDetailCache>.Instance);

        await cache.WriteAsync(
            product,
            CancellationToken.None);

        var key = keyBuilder.BuildProductDetailKey(productId);
        var remainingTtl = await connection
            .GetDatabase()
            .KeyTimeToLiveAsync(key);

        Assert.NotNull(remainingTtl);

        Assert.InRange(
            remainingTtl.Value,
            configuredTtl - TimeSpan.FromSeconds(5),
            configuredTtl);
    }

    [Fact]
    public async Task ReadAsync_CallerCancelsWhileRedisIsWaiting_ThrowsOperationCanceledException()
    {
        var connectionOptions =
            ConfigurationOptions.Parse(redisFixture.ConnectionString);

        connectionOptions.AllowAdmin = true;
        connectionOptions.AsyncTimeout = 5000;

        using var connection =
            await ConnectionMultiplexer.ConnectAsync(connectionOptions);

        var server = connection.GetServer(
            Assert.Single(connection.GetEndPoints()));

        var keyPrefix = $"test-{Guid.NewGuid():N}";
        var cache = new RedisProductDetailCache(
            connection,
            new ProductCacheKeyBuilder(keyPrefix),
            TimeSpan.FromMinutes(5),
            NullLogger<RedisProductDetailCache>.Instance);

        _ = await server.ExecuteAsync(
            "CLIENT",
            "PAUSE",
            1000,
            "ALL");

        try
        {
            using var cancellationSource =
                new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(100));

            var exception = await Record.ExceptionAsync(
                () => cache.ReadAsync(
                    Guid.NewGuid(),
                    cancellationSource.Token));

            Assert.IsAssignableFrom<OperationCanceledException>(
                exception);
        }
        finally
        {
            _ = await server.ExecuteAsync(
                "CLIENT",
                "UNPAUSE");
        }
    }

    [Fact]
    public async Task WriteAsync_CallerCancelsWhileRedisIsWaiting_ThrowsOperationCanceledException()
    {
        var connectionOptions =
            ConfigurationOptions.Parse(redisFixture.ConnectionString);

        connectionOptions.AllowAdmin = true;
        connectionOptions.AsyncTimeout = 5000;

        using var connection =
            await ConnectionMultiplexer.ConnectAsync(connectionOptions);

        var server = connection.GetServer(
            Assert.Single(connection.GetEndPoints()));

        var keyPrefix = $"test-{Guid.NewGuid():N}";
        var cache = new RedisProductDetailCache(
            connection,
            new ProductCacheKeyBuilder(keyPrefix),
            TimeSpan.FromMinutes(5),
            NullLogger<RedisProductDetailCache>.Instance);

        var timestamp = new DateTimeOffset(
            2026,
            10,
            14,
            10,
            30,
            0,
            TimeSpan.FromHours(7));

        var product = new ProductDetailDto(
            Guid.NewGuid(),
            "Cancelled Cache Write",
            "Product used to verify in-flight write cancellation",
            "Active",
            [],
            timestamp,
            timestamp);

        _ = await server.ExecuteAsync(
            "CLIENT",
            "PAUSE",
            1000,
            "ALL");

        try
        {
            using var cancellationSource =
                new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(100));

            var exception = await Record.ExceptionAsync(
                () => cache.WriteAsync(
                    product,
                    cancellationSource.Token));

            Assert.IsAssignableFrom<OperationCanceledException>(
                exception);
        }
        finally
        {
            _ = await server.ExecuteAsync(
                "CLIENT",
                "UNPAUSE");
        }
    }
}
