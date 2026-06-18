using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using PlcDataLogger.Configuration;
using PlcDataLogger.Storage;

namespace PlcDataLogger.OpcUa;

/// <summary>
/// Hosted service that owns one <see cref="OpcUaPlcSession"/> per configured PLC and
/// the periodic re-discovery loop (§5, §7). Each PLC connects independently with
/// retry/backoff, so one PLC being down never stalls the others.
/// </summary>
public sealed class OpcUaClientManager : BackgroundService
{
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(10);

    private readonly LoggerOptions _options;
    private readonly LoggerDatabase _db;
    private readonly ReadingBuffer _buffer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OpcUaClientManager> _log;
    private readonly List<OpcUaPlcSession> _sessions = new();

    public OpcUaClientManager(
        IOptions<LoggerOptions> options,
        LoggerDatabase db,
        ReadingBuffer buffer,
        ILoggerFactory loggerFactory,
        ILogger<OpcUaClientManager> log)
    {
        _options = options.Value;
        _db = db;
        _buffer = buffer;
        _loggerFactory = loggerFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Plcs.Count == 0)
        {
            _log.LogWarning("No PLCs configured under '{Section}:Plcs'; nothing to log.", LoggerOptions.SectionName);
            return;
        }

        ApplicationConfiguration appConfig;
        try
        {
            appConfig = await OpcUaApplication.CreateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Failed to initialize the OPC UA application configuration.");
            return;
        }

        var sessionLogger = _loggerFactory.CreateLogger<OpcUaPlcSession>();

        // Start every PLC concurrently; each retries on its own.
        var tasks = _options.Plcs
            .Select(plc => RunPlcAsync(plc, appConfig, sessionLogger, stoppingToken))
            .ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RunPlcAsync(
        PlcOptions plc,
        ApplicationConfiguration appConfig,
        ILogger sessionLogger,
        CancellationToken ct)
    {
        var rescanInterval = TimeSpan.FromMinutes(Math.Max(1, _options.Discovery.RescanIntervalMinutes));
        var filter = new TagFilter(_options.Discovery.Filter);

        while (!ct.IsCancellationRequested)
        {
            var session = new OpcUaPlcSession(plc, _options.Subscription, filter, appConfig, _db, _buffer, sessionLogger);
            try
            {
                await session.StartAsync(ct).ConfigureAwait(false);
                lock (_sessions) _sessions.Add(session);

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
                lock (_sessions) _sessions.Remove(session);
                await session.DisposeAsync().ConfigureAwait(false);
                _log.LogError(ex, "[{Plc}] Connection failed; retrying in {Delay}s.",
                    plc.Name, ConnectRetryDelay.TotalSeconds);
                try { await Task.Delay(ConnectRetryDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        OpcUaPlcSession[] toDispose;
        lock (_sessions) toDispose = _sessions.ToArray();

        foreach (var session in toDispose)
            await session.DisposeAsync().ConfigureAwait(false);
    }
}
