using Microsoft.Extensions.Options;
using OrderSystem.Application.Authentication;

namespace OrderSystem.Api.Authentication;

public sealed partial class AdminBootstrapHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<AdminBootstrapOptions> options,
    ILogger<AdminBootstrapHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var bootstrap = options.Value;
        if (!bootstrap.Enabled)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AdminBootstrapService>();
        var result = await service.EnsureAdminAsync(
            bootstrap.Email!,
            bootstrap.Password!,
            cancellationToken);
        LogCompleted(logger, result);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Admin bootstrap completed with result {BootstrapResult}.")]
    private static partial void LogCompleted(ILogger logger, AdminBootstrapResult bootstrapResult);
}
