using PlcDataLogger.Configuration;
using PlcDataLogger.OpcUa;
using PlcDataLogger.Storage;
using Serilog;

// A Windows Service starts with its working directory set to System32, not the folder the
// executable lives in. Anchor it to the binary's directory so all relative paths
// (appsettings.json, data/, logs/, pki/) resolve next to the exe in both console and service mode.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

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

    // Run as a Windows Service when launched by the SCM; falls back to a normal console
    // host when run interactively (e.g. `dotnet run`).
    builder.Services.AddWindowsService(options => options.ServiceName = "PLC Data Logger");

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
