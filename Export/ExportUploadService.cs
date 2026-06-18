using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcDataLogger.Configuration;
using PlcDataLogger.Storage;
using PlcDataLogger.Upload;

namespace PlcDataLogger.Export;

/// <summary>
/// Scheduled job (§5) that exports newly-recorded readings to a CSV file and then uploads any
/// not-yet-uploaded files via the active <see cref="ICloudUploadProvider"/>. The export always
/// runs — even with the "None" provider — because the CSV files are useful for manual pickup.
/// Upload failures are retried on the next cycle and never affect local logging.
/// </summary>
public sealed class ExportUploadService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    private readonly LoggerOptions _options;
    private readonly CsvExporter _exporter;
    private readonly LoggerDatabase _db;
    private readonly ICloudUploadProvider _provider;
    private readonly ILogger<ExportUploadService> _log;

    public ExportUploadService(
        IOptions<LoggerOptions> options,
        CsvExporter exporter,
        LoggerDatabase db,
        ICloudUploadProvider provider,
        ILogger<ExportUploadService> log)
    {
        _options = options.Value;
        _exporter = exporter;
        _db = db;
        _provider = provider;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeOfDay = ParseTimeOfDay(_options.Export.DailyAtLocalTime);
        _log.LogInformation("Export service started (daily at {Time}, provider: {Provider}).",
            _options.Export.DailyAtLocalTime, _provider.ProviderName);

        if (_options.Export.RunOnStartup)
        {
            try
            {
                await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
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
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Export/upload cycle failed; will retry on next schedule.");
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var exportDir = Path.GetFullPath(_options.Export.DirectoryPath);
        var afterId = _db.GetMaxExportedReadingId();
        var fileName = $"{Sanitize(_options.SiteName)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";
        var filePath = Path.Combine(exportDir, fileName);

        var result = _exporter.Export(afterId, filePath);

        if (result.RowCount == 0)
        {
            TryDelete(filePath);
            _log.LogInformation("Export: no new readings since id {AfterId}.", afterId);
        }
        else
        {
            _db.RecordExport(fileName, filePath, result.PeriodStart, result.PeriodEnd, result.MaxReadingId, result.RowCount);
            _log.LogInformation("Export: wrote {Rows} readings to {File} (ids {After}..{Max}).",
                result.RowCount, fileName, afterId, result.MaxReadingId);
        }

        await UploadPendingAsync(ct).ConfigureAwait(false);
    }

    private async Task UploadPendingAsync(CancellationToken ct)
    {
        if (!await _provider.IsConfiguredAsync(ct).ConfigureAwait(false))
        {
            _log.LogDebug("Upload skipped: provider '{Provider}' is not configured.", _provider.ProviderName);
            return;
        }

        var pending = _db.GetPendingUploads();
        if (pending.Count == 0)
            return;

        _log.LogInformation("Uploading {Count} pending file(s) via {Provider}.", pending.Count, _provider.ProviderName);

        foreach (var item in pending)
        {
            if (ct.IsCancellationRequested) break;

            if (!File.Exists(item.FilePath))
            {
                _log.LogWarning("Pending export {File} is missing on disk; skipping.", item.FileName);
                continue;
            }

            try
            {
                await _provider.UploadAsync(item.FilePath, _options.Upload.DestinationFolder, ct).ConfigureAwait(false);
                _db.MarkUploaded(item.UploadLogId);
                _log.LogInformation("Uploaded {File}.", item.FileName);
            }
            catch (Exception ex)
            {
                _db.MarkUploadFailed(item.UploadLogId);
                _log.LogWarning(ex, "Upload of {File} failed; will retry next cycle.", item.FileName);
                break; // Likely a connectivity problem — stop hammering, retry later.
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

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
