using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcDataLogger.Components;
using PlcDataLogger.Configuration;
using PlcDataLogger.Export;
using PlcDataLogger.Health;
using PlcDataLogger.OpcUa;
using PlcDataLogger.Storage;
using PlcDataLogger.Upload;
using Serilog;

// A Windows Service starts with its working directory set to System32, not the folder the
// executable lives in. Anchor it to the binary's directory so all relative paths
// (appsettings.json, data/, logs/, pki/) resolve next to the exe in both console and service mode.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// Bootstrap logger so failures during host startup are captured too.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/plcdatalogger-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    Log.Information("PLC Data Logger starting up.");

    var builder = WebApplication.CreateBuilder(args);

    // Run as a Windows Service when launched by the SCM; falls back to a normal console
    // host when run interactively (e.g. `dotnet run`).
    builder.Services.AddWindowsService(options => options.ServiceName = "PLC Data Logger");

    builder.Services.AddSerilog();

    builder.Services.Configure<LoggerOptions>(
        builder.Configuration.GetSection(LoggerOptions.SectionName));

    // Bind the status/config web UI to localhost only — it's a convenience for whoever is at
    // the machine, not a remotely accessible service (§11).
    var uiPort = builder.Configuration.GetValue<int?>($"{LoggerOptions.SectionName}:WebUi:Port") ?? 5198;
    builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(uiPort));

    builder.Services.AddSingleton<ReadingBuffer>();
    builder.Services.AddSingleton<IReadingStore, LoggerDatabase>();
    builder.Services.AddSingleton<CsvExporter>();
    builder.Services.AddSingleton<HealthMonitor>();

    // Runtime-editable configuration (PLCs + upload) and the network scanner that backs the UI.
    builder.Services.AddSingleton<ConfigStore>();
    builder.Services.AddSingleton<OpcUaNetworkScanner>();

    // Active upload provider, rebuilt from config when it changes. "None" is the default and a
    // fully supported permanent state for offline sites (§9).
    builder.Services.AddSingleton<UploadProviderResolver>();
    builder.Services.AddSingleton<ExportRunner>();

    builder.Services.AddHostedService<StartupValidationService>();
    builder.Services.AddHostedService<StorageWriter>();
    builder.Services.AddHostedService<OpcUaClientManager>();
    builder.Services.AddHostedService<ExportUploadService>();
    builder.Services.AddHostedService<RetentionService>();

    builder.Services.AddRazorComponents().AddInteractiveServerComponents();

    var app = builder.Build();

    app.UseStaticFiles();
    app.UseAntiforgery();

    // Machine-readable endpoints (localhost only) for monitoring and scripted provisioning.
    app.MapGet("/api/health", (HealthMonitor health) => health.Snapshot());
    app.MapGet("/api/scan", (OpcUaNetworkScanner scanner, CancellationToken ct) => scanner.ScanAsync(ct: ct));
    app.MapGet("/api/plcs", (ConfigStore store) => store.GetPlcs());
    app.MapPost("/api/plcs", (PlcOptions plc, ConfigStore store) => { store.UpsertPlc(plc); return Results.Ok(); });
    app.MapDelete("/api/plcs/{name}", (string name, ConfigStore store) => { store.RemovePlc(name); return Results.Ok(); });
    app.MapPost("/api/export-now", (ExportRunner runner, CancellationToken ct) => runner.RunOnceAsync(ct));
    app.MapGet("/api/config/validate", (IOptions<LoggerOptions> opts, ConfigStore store) =>
        ConfigValidation.Validate(opts.Value, store.GetPlcs(), store.GetUpload()));

    app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "PLC Data Logger terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}
