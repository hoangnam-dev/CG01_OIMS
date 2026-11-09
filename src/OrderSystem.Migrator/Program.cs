using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderSystem.Infrastructure;
using OrderSystem.Infrastructure.Persistence;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOrderSystemPersistence(builder.Configuration);

using var host = builder.Build();
await host.StartAsync();
try
{
    await using var scope = host.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
    var stoppingToken = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
    await dbContext.Database.MigrateAsync(stoppingToken);
}
finally
{
    await host.StopAsync();
}
