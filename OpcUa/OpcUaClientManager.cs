using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using PlcDataLogger.Configuration;
using PlcDataLogger.Health;
using PlcDataLogger.Storage;

namespace PlcDataLogger.OpcUa;

/// <summary>
/// Hosted service that owns one <see cref="OpcUaPlcSession"/> per configured PLC and the periodic
/// re-discovery loop (§5, §7). Each PLC connects independently with retry/backoff, so one PLC
/// being down never stalls the others. The set of PLCs is taken from <see cref="ConfigStore"/> and
/// reconciled live whenever the configuration changes — connections are added, removed, or
/// restarted without a service restart (§5).
/// </summary>
public sealed class OpcUaClientManager : BackgroundService
{
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(10);

    private readonly LoggerOptions _options;
    private readonly ConfigStore _configStore;
    private readonly IReadingStore _db;
    private readonly ReadingBuffer _buffer;
    private readonly HealthMonitor _health;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OpcUaClientManager> _log;

    private readonly object _gate = new();
    private readonly Dictionary<string, Runner> _runners = new(StringComparer.OrdinalIgnoreCase);

    private ApplicationConfiguration? _appConfig;
    private ILogger? _sessionLogger;
    private CancellationToken _stoppingToken;

    public OpcUaClientManager(
        IOptions<LoggerOptions> options,
        ConfigStore configStore,
        IReadingStore db,
        ReadingBuffer buffer,
        HealthMonitor health,
        ILoggerFactory loggerFactory,
        ILogger<OpcUaClientManager> log)
    {
        _options = options.Value;
        _configStore = configStore;
        _db = db;
        _buffer = buffer;
        _health = health;
        _loggerFactory = loggerFactory;
        _log = log;
    }

    private sealed record Runner(PlcOptions Plc, CancellationTokenSource Cts)
    {
        public Task Task { get; set; } = Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _appConfig = await OpcUaApplication.CreateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Failed to initialize the OPC UA application configuration.");
            return;
        }

        _sessionLogger = _loggerFactory.CreateLogger<OpcUaPlcSession>();
        _stoppingToken = stoppingToken;

        _configStore.Changed += OnConfigChanged;
        Reconcile();

        // Stay alive until shutdown; PLC work happens on the per-runner tasks.
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shutting down */ }

        _configStore.Changed -= OnConfigChanged;
        await StopAllAsync().ConfigureAwait(false);
    }

    private void OnConfigChanged()
    {
        try { Reconcile(); }
        catch (Exception ex) { _log.LogError(ex, "Failed to reconcile PLC sessions after config change."); }
    }

    /// <summary>Bring running sessions in line with the configured PLC set.</summary>
    private void Reconcile()
    {
        if (_appConfig is null || _sessionLogger is null)
            return;

        lock (_gate)
        {
            var desired = _configStore.GetPlcs().ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

            // Stop runners that were removed or whose endpoint/security changed.
            foreach (var name in _runners.Keys.ToList())
            {
                var runner = _runners[name];
                if (!desired.TryGetValue(name, out var want) || !SameConnection(runner.Plc, want))
                {
                    _log.LogInformation("[{Plc}] Stopping session (removed or changed).", name);
                    runner.Cts.Cancel();
                    _runners.Remove(name);
                    _health.RemovePlc(name);
                }
            }

            // Start runners for newly added / changed PLCs.
            foreach (var (name, plc) in desired)
            {
                if (_runners.ContainsKey(name))
                    continue;

                _log.LogInformation("[{Plc}] Starting session for {Endpoint}.", name, plc.EndpointUrl);
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
                var runner = new Runner(plc, cts);
                runner.Task = RunPlcAsync(plc, cts.Token);
                _runners[name] = runner;
            }

            if (_runners.Count == 0)
                _log.LogWarning("No PLCs configured; add one via the web UI to begin logging.");
        }
    }

    private static bool SameConnection(PlcOptions a, PlcOptions b) =>
        string.Equals(a.EndpointUrl, b.EndpointUrl, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.SecurityPolicy, b.SecurityPolicy, StringComparison.OrdinalIgnoreCase);

    private async Task RunPlcAsync(PlcOptions plc, CancellationToken ct)
    {
        var rescanInterval = TimeSpan.FromMinutes(Math.Max(1, _options.Discovery.RescanIntervalMinutes));
        var filter = new TagFilter(_options.Discovery.Filter);

        while (!ct.IsCancellationRequested)
        {
            var session = new OpcUaPlcSession(plc, _options.Subscription, filter, _appConfig!, _db, _buffer, _health, _sessionLogger!);
            try
            {
                await session.StartAsync(ct).ConfigureAwait(false);

                // Periodic re-discovery to pick up tag changes after Codesys project updates.
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(rescanInterval, ct).ConfigureAwait(false);
                    _log.LogInformation("[{Plc}] Running scheduled tag re-discovery.", plc.Name);
                    await session.RunDiscoveryAndSubscribeAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                _health.SetDisconnected(plc.Name, ex.Message);
                _log.LogError(ex, "[{Plc}] Connection failed; retrying in {Delay}s.",
                    plc.Name, ConnectRetryDelay.TotalSeconds);
                try { await Task.Delay(ConnectRetryDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task StopAllAsync()
    {
        Runner[] runners;
        lock (_gate)
        {
            runners = _runners.Values.ToArray();
            _runners.Clear();
        }

        foreach (var runner in runners)
            runner.Cts.Cancel();

        await Task.WhenAll(runners.Select(r => r.Task)).ConfigureAwait(false);
    }
}
