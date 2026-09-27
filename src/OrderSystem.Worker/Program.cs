using OrderSystem.Infrastructure.Logging;
using OrderSystem.Worker;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog(loggerConfiguration =>
    loggerConfiguration.ConfigureOimsLogging(builder.Configuration, "OrderSystem.Worker"));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
