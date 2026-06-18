using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcDataLogger.Configuration;
using PlcDataLogger.Storage;
using PlcDataLogger.Upload;

namespace PlcDataLogger.Export;

/// <summary>Summary of one export + upload run, for the UI/API.</summary>
public sealed record ExportRunResult(int ExportedRows, int Uploaded, int FailedUploads, string Message);

/// <summary>
/// Performs a single export-then-upload pass (§5). Shared by the scheduled
/// <see cref="ExportUploadService"/> and the on-demand "upload now" button / API. A semaphore
/// guarantees only one run at a time, so a manual trigger can't overlap the scheduled job.
/// </summary>
public sealed class ExportRunner
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LoggerOptions _options;
    private readonly CsvExporter _exporter;
    private readonly IReadingStore _db;
    private readonly UploadProviderResolver _providers;
    private readonly ILogger<ExportRunner> _log;

    public ExportRunner(
        IOptions<LoggerOptions> options,
        CsvExporter exporter,
        IReadingStore db,
        UploadProviderResolver providers,
        ILogger<ExportRunner> log)
    {
        _options = options.Value;
        _exporter = exporter;
        _db = db;
        _providers = providers;
        _log = log;
    }

    public async Task<ExportRunResult> RunOnceAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var exportedRows = Export();
            var (uploaded, failed, skipped) = await UploadPendingAsync(ct).ConfigureAwait(false);
            return new ExportRunResult(exportedRows, uploaded, failed, BuildMessage(exportedRows, uploaded, failed, skipped));
        }
        finally
        {
            _gate.Release();
        }
    }

    private int Export()
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
            return 0;
        }

        _db.RecordExport(fileName, filePath, result.PeriodStart, result.PeriodEnd, result.MaxReadingId, result.RowCount);
        _log.LogInformation("Export: wrote {Rows} readings to {File} (ids {After}..{Max}).",
            result.RowCount, fileName, afterId, result.MaxReadingId);
        return result.RowCount;
    }

    private async Task<(int Uploaded, int Failed, bool Skipped)> UploadPendingAsync(CancellationToken ct)
    {
        var provider = _providers.Current;
        if (!await provider.IsConfiguredAsync(ct).ConfigureAwait(false))
        {
            _log.LogDebug("Upload skipped: provider '{Provider}' is not configured.", provider.ProviderName);
            return (0, 0, true);
        }

        var pending = _db.GetPendingUploads();
        if (pending.Count == 0)
            return (0, 0, false);

        _log.LogInformation("Uploading {Count} pending file(s) via {Provider}.", pending.Count, provider.ProviderName);

        var uploaded = 0;
        var failed = 0;
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
                await provider.UploadAsync(item.FilePath, _providers.DestinationFolder, ct).ConfigureAwait(false);
                _db.MarkUploaded(item.UploadLogId);
                _log.LogInformation("Uploaded {File}.", item.FileName);
                uploaded++;
            }
            catch (Exception ex)
            {
                _db.MarkUploadFailed(item.UploadLogId);
                _log.LogWarning(ex, "Upload of {File} failed.", item.FileName);
                failed++;
                break; // Likely a connectivity problem — stop hammering, retry later.
            }
        }

        return (uploaded, failed, false);
    }

    private static string BuildMessage(int exportedRows, int uploaded, int failed, bool uploadSkipped)
    {
        var export = exportedRows > 0 ? $"Exported {exportedRows} reading(s)." : "No new readings to export.";
        if (uploadSkipped)
            return $"{export} Upload provider not configured.";
        if (failed > 0)
            return $"{export} Uploaded {uploaded}, {failed} failed — see logs.";
        if (uploaded > 0)
            return $"{export} Uploaded {uploaded} file(s).";
        return $"{export} Nothing pending to upload.";
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
