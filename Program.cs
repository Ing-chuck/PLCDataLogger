using PlcDataLogger.Configuration;
using PlcDataLogger.OpcUa;
using PlcDataLogger.Storage;
using Serilog;

// Bootstrap logger so failures during host startup are captured too.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/plcdatalogger-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    Log.Information("PLC Data Logger starting up.");

    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog();

    builder.Services.Configure<LoggerOptions>(
        builder.Configuration.GetSection(LoggerOptions.SectionName));

    builder.Services.AddSingleton<ReadingBuffer>();
    builder.Services.AddSingleton<LoggerDatabase>();

    builder.Services.AddHostedService<StorageWriter>();
    builder.Services.AddHostedService<OpcUaClientManager>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "PLC Data Logger terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}
