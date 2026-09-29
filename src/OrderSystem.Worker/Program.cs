using OrderSystem.Infrastructure;
using OrderSystem.Infrastructure.Logging;
using OrderSystem.Worker;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog(loggerConfiguration =>
    loggerConfiguration.ConfigureOimsLogging(builder.Configuration, "OrderSystem.Worker"));
builder.Services
    .AddOrderSystemPersistence(builder.Configuration)
    .AddOrderSystemCommonInfrastructure()
    .AddOrderSystemOrderInfrastructure(builder.Configuration);

builder.Services.AddMetrics();
builder.Services.AddSingleton<ReservationExpirationMetrics>();

builder.Services.AddHostedService<ReservationExpirationWorker>();

var host = builder.Build();
host.Run();
