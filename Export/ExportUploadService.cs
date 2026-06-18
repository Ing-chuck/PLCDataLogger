using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcDataLogger.Configuration;

namespace PlcDataLogger.Export;

/// <summary>
/// Scheduled job (§5) that runs the export-then-upload pass once a day via <see cref="ExportRunner"/>.
/// The export always runs — even with the "None" provider — because the CSV files are useful for
/// manual pickup. Upload failures are retried on the next cycle and never affect local logging.
/// </summary>
public sealed class ExportUploadService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    private readonly ExportOptions _export;
    private readonly ExportRunner _runner;
    private readonly ILogger<ExportUploadService> _log;

    public ExportUploadService(
        IOptions<LoggerOptions> options,
        ExportRunner runner,
        ILogger<ExportUploadService> log)
    {
        _export = options.Value.Export;
        _runner = runner;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeOfDay = ParseTimeOfDay(_export.DailyAtLocalTime);
        _log.LogInformation("Export service started (daily at {Time}).", _export.DailyAtLocalTime);

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
            var delay = TimeUntilNext(timeOfDay, DateTime.Now);
            _log.LogInformation("Next export in {Hours:F1} h.", delay.TotalHours);
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                await _runner.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Export/upload cycle failed; will retry on next schedule.");
            }
        }
    }

    private static TimeSpan TimeUntilNext(TimeSpan timeOfDay, DateTime nowLocal)
    {
        var todayRun = nowLocal.Date + timeOfDay;
        var next = todayRun > nowLocal ? todayRun : todayRun.AddDays(1);
        return next - nowLocal;
    }

    private static TimeSpan ParseTimeOfDay(string value) =>
        TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out var ts)
            ? ts
            : new TimeSpan(2, 0, 0);
}
