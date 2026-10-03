using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Payments;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Payments;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class PaymentReconciliationStoreTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task RecordStatusCheckAsync_PersistsCheckAndDefersNextReconciliation()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var paymentId = Guid.NewGuid();
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>(
                            "Database:ConnectionString",
                            postgres.ConnectionString)));
            });

        using (var setupScope = factory.Services.CreateScope())
        {
            var dbContext = setupScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            await dbContext.Database.MigrateAsync();
            var userId = Guid.NewGuid();
            var orderId = Guid.NewGuid();
            var createdAt = now.AddMinutes(-2);
            var email = $"reconciliation-record-check-{userId:N}@example.com";
            dbContext.AddRange(
                new User(userId, email, email, "test-password-hash", UserRole.Customer, createdAt),
                new Order(orderId, userId, 100m, now.AddMinutes(13), createdAt),
                new Payment(
                    paymentId,
                    orderId,
                    100m,
                    PaymentProviderCodes.Fake,
                    $"fake-pay-{Guid.NewGuid():N}",
                    $"gateway-{Guid.NewGuid():N}",
                    createdAt));
            await dbContext.SaveChangesAsync();
        }

        using var storeScope = factory.Services.CreateScope();
        var store = storeScope.ServiceProvider.GetRequiredService<IPaymentReconciliationStore>();

        await store.RecordStatusCheckAsync(paymentId, now, CancellationToken.None);
        var immediatelyDue = await store.ListDueAsync(now, 100, CancellationToken.None);
        var dueAfterOneMinute = await store.ListDueAsync(now.AddMinutes(1), 100, CancellationToken.None);

        Assert.DoesNotContain(immediatelyDue, candidate => candidate.PaymentId == paymentId);
        var candidate = Assert.Single(dueAfterOneMinute, candidate => candidate.PaymentId == paymentId);
        Assert.Equal(now, candidate.LastStatusCheckedAt);
    }

    [Fact]
    public async Task ListDueAsync_OlderThanThirtyMinutes_UsesFifteenMinuteCheckSpacing()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var duePaymentId = Guid.NewGuid();
        var notDuePaymentId = Guid.NewGuid();
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>(
                            "Database:ConnectionString",
                            postgres.ConnectionString)));
            });

        using (var setupScope = factory.Services.CreateScope())
        {
            var dbContext = setupScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            await dbContext.Database.MigrateAsync();
            var dueUserId = Guid.NewGuid();
            var notDueUserId = Guid.NewGuid();
            var dueOrderId = Guid.NewGuid();
            var notDueOrderId = Guid.NewGuid();
            var createdAt = now.AddMinutes(-31);
            var dueEmail = $"reconciliation-due-fifteen-minute-{dueUserId:N}@example.com";
            var notDueEmail = $"reconciliation-not-due-fifteen-minute-{notDueUserId:N}@example.com";
            var duePayment = new Payment(
                duePaymentId,
                dueOrderId,
                100m,
                PaymentProviderCodes.Fake,
                $"fake-pay-{Guid.NewGuid():N}",
                $"gateway-{Guid.NewGuid():N}",
                createdAt);
            var notDuePayment = new Payment(
                notDuePaymentId,
                notDueOrderId,
                100m,
                PaymentProviderCodes.Fake,
                $"fake-pay-{Guid.NewGuid():N}",
                $"gateway-{Guid.NewGuid():N}",
                createdAt);
            dbContext.AddRange(
                new User(dueUserId, dueEmail, dueEmail, "test-password-hash", UserRole.Customer, createdAt),
                new User(notDueUserId, notDueEmail, notDueEmail, "test-password-hash", UserRole.Customer, createdAt),
                new Order(dueOrderId, dueUserId, 100m, now.AddMinutes(-16), createdAt),
                new Order(notDueOrderId, notDueUserId, 100m, now.AddMinutes(-16), createdAt),
                duePayment,
                notDuePayment);
            dbContext.Entry(duePayment).Property(payment => payment.LastStatusCheckedAt).CurrentValue = now.AddMinutes(-15);
            dbContext.Entry(notDuePayment).Property(payment => payment.LastStatusCheckedAt).CurrentValue = now.AddSeconds(-899);
            await dbContext.SaveChangesAsync();
        }

        using var queryScope = factory.Services.CreateScope();
        var store = queryScope.ServiceProvider.GetRequiredService<IPaymentReconciliationStore>();

        var candidates = await store.ListDueAsync(now, 100, CancellationToken.None);

        Assert.Contains(candidates, candidate => candidate.PaymentId == duePaymentId);
        Assert.DoesNotContain(candidates, candidate => candidate.PaymentId == notDuePaymentId);
    }

    [Fact]
    public async Task ListDueAsync_BetweenFiveAndThirtyMinutesOld_UsesFiveMinuteCheckSpacing()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var duePaymentId = Guid.NewGuid();
        var notDuePaymentId = Guid.NewGuid();
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>(
                            "Database:ConnectionString",
                            postgres.ConnectionString)));
            });

        using (var setupScope = factory.Services.CreateScope())
        {
            var dbContext = setupScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            await dbContext.Database.MigrateAsync();
            var dueUserId = Guid.NewGuid();
            var notDueUserId = Guid.NewGuid();
            var dueOrderId = Guid.NewGuid();
            var notDueOrderId = Guid.NewGuid();
            var createdAt = now.AddMinutes(-10);
            var dueEmail = $"reconciliation-due-five-minute-{dueUserId:N}@example.com";
            var notDueEmail = $"reconciliation-not-due-five-minute-{notDueUserId:N}@example.com";
            var duePayment = new Payment(
                duePaymentId,
                dueOrderId,
                100m,
                PaymentProviderCodes.Fake,
                $"fake-pay-{Guid.NewGuid():N}",
                $"gateway-{Guid.NewGuid():N}",
                createdAt);
            var notDuePayment = new Payment(
                notDuePaymentId,
                notDueOrderId,
                100m,
                PaymentProviderCodes.Fake,
                $"fake-pay-{Guid.NewGuid():N}",
                $"gateway-{Guid.NewGuid():N}",
                createdAt);
            dbContext.AddRange(
                new User(dueUserId, dueEmail, dueEmail, "test-password-hash", UserRole.Customer, createdAt),
                new User(notDueUserId, notDueEmail, notDueEmail, "test-password-hash", UserRole.Customer, createdAt),
                new Order(dueOrderId, dueUserId, 100m, now.AddMinutes(5), createdAt),
                new Order(notDueOrderId, notDueUserId, 100m, now.AddMinutes(5), createdAt),
                duePayment,
                notDuePayment);
            dbContext.Entry(duePayment).Property(payment => payment.LastStatusCheckedAt).CurrentValue = now.AddMinutes(-5);
            dbContext.Entry(notDuePayment).Property(payment => payment.LastStatusCheckedAt).CurrentValue = now.AddSeconds(-299);
            await dbContext.SaveChangesAsync();
        }

        using var queryScope = factory.Services.CreateScope();
        var store = queryScope.ServiceProvider.GetRequiredService<IPaymentReconciliationStore>();

        var candidates = await store.ListDueAsync(now, 100, CancellationToken.None);

        Assert.Contains(candidates, candidate => candidate.PaymentId == duePaymentId);
        Assert.DoesNotContain(candidates, candidate => candidate.PaymentId == notDuePaymentId);
    }

    [Fact]
    public async Task ListDueAsync_UnderFiveMinutesOld_UsesOneMinuteCheckSpacing()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var duePaymentId = Guid.NewGuid();
        var notDuePaymentId = Guid.NewGuid();
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>(
                            "Database:ConnectionString",
                            postgres.ConnectionString)));
            });

        using (var setupScope = factory.Services.CreateScope())
        {
            var dbContext = setupScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            await dbContext.Database.MigrateAsync();
            var dueUserId = Guid.NewGuid();
            var notDueUserId = Guid.NewGuid();
            var dueOrderId = Guid.NewGuid();
            var notDueOrderId = Guid.NewGuid();
            var createdAt = now.AddMinutes(-4);
            var dueEmail = $"reconciliation-due-checked-{dueUserId:N}@example.com";
            var notDueEmail = $"reconciliation-not-due-checked-{notDueUserId:N}@example.com";
            var duePayment = new Payment(
                duePaymentId,
                dueOrderId,
                100m,
                PaymentProviderCodes.Fake,
                $"fake-pay-{Guid.NewGuid():N}",
                $"gateway-{Guid.NewGuid():N}",
                createdAt);
            var notDuePayment = new Payment(
                notDuePaymentId,
                notDueOrderId,
                100m,
                PaymentProviderCodes.Fake,
                $"fake-pay-{Guid.NewGuid():N}",
                $"gateway-{Guid.NewGuid():N}",
                createdAt);
            dbContext.AddRange(
                new User(dueUserId, dueEmail, dueEmail, "test-password-hash", UserRole.Customer, createdAt),
                new User(notDueUserId, notDueEmail, notDueEmail, "test-password-hash", UserRole.Customer, createdAt),
                new Order(dueOrderId, dueUserId, 100m, now.AddMinutes(11), createdAt),
                new Order(notDueOrderId, notDueUserId, 100m, now.AddMinutes(11), createdAt),
                duePayment,
                notDuePayment);
            dbContext.Entry(duePayment).Property(payment => payment.LastStatusCheckedAt).CurrentValue = now.AddMinutes(-1);
            dbContext.Entry(notDuePayment).Property(payment => payment.LastStatusCheckedAt).CurrentValue = now.AddSeconds(-59);
            await dbContext.SaveChangesAsync();
        }

        using var queryScope = factory.Services.CreateScope();
        var store = queryScope.ServiceProvider.GetRequiredService<IPaymentReconciliationStore>();

        var candidates = await store.ListDueAsync(now, 100, CancellationToken.None);

        Assert.Contains(candidates, candidate => candidate.PaymentId == duePaymentId);
        Assert.DoesNotContain(candidates, candidate => candidate.PaymentId == notDuePaymentId);
    }

    [Fact]
    public async Task ListDueAsync_ReturnsOldEnoughPendingPaymentAndExcludesYoungPayment()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var duePaymentId = Guid.NewGuid();
        var youngPaymentId = Guid.NewGuid();
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>(
                            "Database:ConnectionString",
                            postgres.ConnectionString)));
            });

        using (var setupScope = factory.Services.CreateScope())
        {
            var dbContext = setupScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            await dbContext.Database.MigrateAsync();
            var dueUserId = Guid.NewGuid();
            var youngUserId = Guid.NewGuid();
            var dueOrderId = Guid.NewGuid();
            var youngOrderId = Guid.NewGuid();
            var dueCreatedAt = now.AddMinutes(-1);
            var youngCreatedAt = now.AddSeconds(-59);
            var dueEmail = $"reconciliation-due-{dueUserId:N}@example.com";
            var youngEmail = $"reconciliation-young-{youngUserId:N}@example.com";
            dbContext.AddRange(
                new User(dueUserId, dueEmail, dueEmail, "test-password-hash", UserRole.Customer, dueCreatedAt),
                new User(youngUserId, youngEmail, youngEmail, "test-password-hash", UserRole.Customer, youngCreatedAt),
                new Order(dueOrderId, dueUserId, 100m, now.AddMinutes(14), dueCreatedAt),
                new Order(youngOrderId, youngUserId, 100m, now.AddMinutes(14), youngCreatedAt),
                new Payment(
                    duePaymentId,
                    dueOrderId,
                    100m,
                    PaymentProviderCodes.Fake,
                    $"fake-pay-{Guid.NewGuid():N}",
                    $"gateway-{Guid.NewGuid():N}",
                    dueCreatedAt,
                    PaymentScenario.DelayedSuccess),
                new Payment(
                    youngPaymentId,
                    youngOrderId,
                    100m,
                    PaymentProviderCodes.Fake,
                    $"fake-pay-{Guid.NewGuid():N}",
                    $"gateway-{Guid.NewGuid():N}",
                    youngCreatedAt));
            await dbContext.SaveChangesAsync();
        }

        using var queryScope = factory.Services.CreateScope();
        var store = queryScope.ServiceProvider.GetRequiredService<IPaymentReconciliationStore>();

        var candidates = await store.ListDueAsync(now, 100, CancellationToken.None);

        var dueCandidate = Assert.Single(candidates, candidate => candidate.PaymentId == duePaymentId);
        Assert.Equal(PaymentScenario.DelayedSuccess, dueCandidate.Scenario);
        Assert.DoesNotContain(candidates, candidate => candidate.PaymentId == youngPaymentId);
    }
}
