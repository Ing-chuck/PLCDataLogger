using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcDataLogger.Configuration;

namespace PlcDataLogger.Export;

/// <summary>
/// Scheduled job (§5) that runs the export-then-upload pass via <see cref="ExportRunner"/> on the
/// cadence configured in the web UI — either a fixed interval or once a day at a local time. The
/// export always runs — even with the "None" provider — because the CSV files are useful for manual
/// pickup. Upload failures are retried on the next cycle and never affect local logging. A schedule
/// change (via <see cref="ConfigStore"/>) wakes the loop immediately to re-plan the next run.
/// </summary>
public sealed class ExportUploadService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    private readonly ExportOptions _export;
    private readonly ConfigStore _config;
    private readonly ExportRunner _runner;
    private readonly ILogger<ExportUploadService> _log;

    // Signalled whenever the runtime config changes, so a pending Task.Delay is cut short and the
    // next run is re-planned against the new schedule.
    private volatile TaskCompletionSource _reload = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ExportUploadService(
        IOptions<LoggerOptions> options,
        ConfigStore config,
        ExportRunner runner,
        ILogger<ExportUploadService> log)
    {
        _export = options.Value.Export;
        _config = config;
        _runner = runner;
        _log = log;
        _config.Changed += OnConfigChanged;
    }

    private void OnConfigChanged() =>
        Interlocked.Exchange(ref _reload, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Export service started ({Schedule}).", Describe(_config.GetSchedule()));

        if (_export.RunOnStartup)
        {
            try
            {
                await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
                await _runner.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var schedule = _config.GetSchedule();
            var delay = TimeUntilNext(schedule, DateTime.Now);
            _log.LogInformation("Next export in {Hours:F1} h ({Schedule}).", delay.TotalHours, Describe(schedule));

            // Wake on either the timer or a config change, whichever comes first.
            var reloaded = await DelayOrReload(delay, stoppingToken).ConfigureAwait(false);
            if (stoppingToken.IsCancellationRequested) break;
            if (reloaded)
            {
                _log.LogInformation("Schedule changed; re-planning next export.");
                continue;
            }

            try
            {
                await _runner.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Export/upload cycle failed; will retry on next schedule.");
            }
        }
    }

    /// <summary>Wait for <paramref name="delay"/>, returning true if a config change interrupted it.</summary>
    private async Task<bool> DelayOrReload(TimeSpan delay, CancellationToken ct)
    {
        var reload = _reload.Task;
        var timer = Task.Delay(delay, ct);
        try
        {
            var finished = await Task.WhenAny(timer, reload).ConfigureAwait(false);
            if (finished == timer) { await timer.ConfigureAwait(false); return false; }
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    private static TimeSpan TimeUntilNext(ScheduleConfig schedule, DateTime nowLocal)
    {
        if (schedule.Mode.Equals("Interval", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromMinutes(Math.Max(1, schedule.IntervalMinutes));

        var timeOfDay = ParseTimeOfDay(schedule.DailyAtLocalTime);
        var todayRun = nowLocal.Date + timeOfDay;
        var next = todayRun > nowLocal ? todayRun : todayRun.AddDays(1);
        return next - nowLocal;
    }

    private static string Describe(ScheduleConfig s) =>
        s.Mode.Equals("Interval", StringComparison.OrdinalIgnoreCase)
            ? $"every {Math.Max(1, s.IntervalMinutes)} min"
            : $"daily at {s.DailyAtLocalTime}";

    private static TimeSpan ParseTimeOfDay(string value) =>
        TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out var ts)
            ? ts
            : new TimeSpan(2, 0, 0);
}
