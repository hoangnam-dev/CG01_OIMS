using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Inventories;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Inventories;

public sealed class InventoryDependencyInjectionTests
{
    [Fact]
    public void Services_ResolveInventoryStoreThroughPublicInterface()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration()));
        using var scope = factory.Services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IInventoryStore>();

        Assert.NotNull(store);
    }
}
