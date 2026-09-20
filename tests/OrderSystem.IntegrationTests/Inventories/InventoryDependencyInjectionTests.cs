using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Inventories;

namespace OrderSystem.IntegrationTests.Inventories;

public sealed class InventoryDependencyInjectionTests(
    WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void Services_ResolveInventoryStoreThroughPublicInterface()
    {
        using var scope = factory.Services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IInventoryStore>();

        Assert.NotNull(store);
    }
}
