using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcDataLogger.Configuration;

namespace PlcDataLogger.Export;

/// <summary>
/// Scheduled job (§5) that uploads a single, overwritten database backup via
/// <see cref="ExportRunner.RunScheduledUploadAsync"/> on the cadence configured in the web UI —
/// either a fixed interval or once a day at a local time. The backup always runs — even with the
/// "None" provider — so a fresh local snapshot exists for manual pickup. Upload failures are retried
/// on the next cycle and never affect local logging. A schedule change (via <see cref="ConfigStore"/>)
/// wakes the loop immediately to re-plan the next run.
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
        _log.LogInformation("Backup/upload service started ({Schedule}).", Describe(_config.GetSchedule()));

        if (_export.RunOnStartup)
        {
            try
            {
                await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
                await _runner.RunScheduledUploadAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var schedule = _config.GetSchedule();
            var delay = TimeUntilNext(schedule, DateTime.Now);
            _log.LogInformation("Next backup upload in {Hours:F1} h ({Schedule}).", delay.TotalHours, Describe(schedule));

            // Wake on either the timer or a config change, whichever comes first.
            var reloaded = await DelayOrReload(delay, stoppingToken).ConfigureAwait(false);
            if (stoppingToken.IsCancellationRequested) break;
            if (reloaded)
            {
                _log.LogInformation("Schedule changed; re-planning next backup upload.");
                continue;
            }

            try
            {
                await _runner.RunScheduledUploadAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Backup/upload cycle failed; will retry on next schedule.");
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
        var next = schedule.Mode.ToLowerInvariant() switch
        {
            "weekly" => NextWeekly(nowLocal, timeOfDay, schedule.DayOfWeek),
            "monthly" => NextMonthly(nowLocal, timeOfDay, schedule.DayOfMonth),
            _ => NextDaily(nowLocal, timeOfDay),
        };
        return next - nowLocal;
    }

    private static DateTime NextDaily(DateTime now, TimeSpan tod)
    {
        var run = now.Date + tod;
        return run > now ? run : run.AddDays(1);
    }

    private static DateTime NextWeekly(DateTime now, TimeSpan tod, int dayOfWeek)
    {
        var target = (DayOfWeek)Math.Clamp(dayOfWeek, 0, 6);
        var run = now.Date + tod;
        var days = ((int)target - (int)run.DayOfWeek + 7) % 7;
        run = run.AddDays(days);
        return run > now ? run : run.AddDays(7);
    }

    private static DateTime NextMonthly(DateTime now, TimeSpan tod, int dayOfMonth)
    {
        DateTime RunFor(int year, int month)
        {
            var day = Math.Min(Math.Clamp(dayOfMonth, 1, 28), DateTime.DaysInMonth(year, month));
            return new DateTime(year, month, day) + tod;
        }
        var run = RunFor(now.Year, now.Month);
        if (run > now) return run;
        var next = now.AddMonths(1);
        return RunFor(next.Year, next.Month);
    }

    private static string Describe(ScheduleConfig s) => s.Mode.ToLowerInvariant() switch
    {
        "interval" => $"every {Math.Max(1, s.IntervalMinutes)} min",
        "weekly" => $"weekly on {(DayOfWeek)Math.Clamp(s.DayOfWeek, 0, 6)} at {s.DailyAtLocalTime}",
        "monthly" => $"monthly on day {Math.Clamp(s.DayOfMonth, 1, 28)} at {s.DailyAtLocalTime}",
        _ => $"daily at {s.DailyAtLocalTime}",
    } + $", {Math.Max(1, s.PartitionHours)}h partitions";

    private static TimeSpan ParseTimeOfDay(string value) =>
        TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out var ts)
            ? ts
            : new TimeSpan(2, 0, 0);
}
