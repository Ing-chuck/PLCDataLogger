using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcDataLogger.Configuration;
using PlcDataLogger.Storage;
using PlcDataLogger.Upload;

namespace PlcDataLogger.Export;

/// <summary>Summary of one export + upload run, for the UI/API.</summary>
public sealed record ExportRunResult(int ExportedRows, int Uploaded, int FailedUploads, string Message);

/// <summary>
/// Runs the export/backup + upload actions (§5). The scheduled <see cref="ExportUploadService"/>
/// uploads a single, overwritten database backup (<see cref="RunScheduledUploadAsync"/>); CSV export
/// (rolling <see cref="RunOnceAsync"/>, windowed <see cref="RunRangeExportAsync"/>) and timestamped
/// backups (<see cref="RunDatabaseBackupAsync"/>) remain available on demand. A semaphore guarantees
/// only one action runs at a time, so a manual trigger can't overlap the scheduled job.
/// </summary>
public sealed class ExportRunner
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LoggerOptions _options;
    private readonly ConfigStore _config;
    private readonly CsvExporter _exporter;
    private readonly ParquetExporter _parquet;
    private readonly IReadingStore _db;
    private readonly UploadProviderResolver _providers;
    private readonly ILogger<ExportRunner> _log;

    public ExportRunner(
        IOptions<LoggerOptions> options,
        ConfigStore config,
        CsvExporter exporter,
        ParquetExporter parquet,
        IReadingStore db,
        UploadProviderResolver providers,
        ILogger<ExportRunner> log)
    {
        _options = options.Value;
        _config = config;
        _exporter = exporter;
        _parquet = parquet;
        _db = db;
        _providers = providers;
        _log = log;
    }

    private string PartitionsDir => Path.Combine(Path.GetFullPath(_options.Export.DirectoryPath), "partitions");

    /// <summary>
    /// The scheduled action: write any completed time partitions to zstd Parquet, upload every
    /// partition not yet uploaded, then delete the local copies (or keep them until they age past the
    /// retention window). Uploading a partition advances the retention watermark so its readings can
    /// be pruned locally once safely in the cloud.
    /// </summary>
    public async Task<ExportRunResult> RunScheduledUploadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var schedule = _config.GetSchedule();
            var exported = await ExportPartitionsAsync(schedule, ct).ConfigureAwait(false);

            var pending = _db.GetPendingPartitionUploads();
            var (uploaded, failed) = await UploadPartitionsAsync(pending, schedule.KeepUploadedPartitions, ct).ConfigureAwait(false);

            if (schedule.KeepUploadedPartitions)
                CleanupKeptPartitions();

            var msg = $"Exported {exported} new partition file(s)." + UploadSuffix(uploaded, failed, pending.Count);
            return new ExportRunResult(0, uploaded, failed, msg);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Write Parquet files for every completed partition not yet exported. Partitions align
    /// to UTC boundaries of the configured size; only fully-elapsed windows are written.</summary>
    private async Task<int> ExportPartitionsAsync(ScheduleConfig schedule, CancellationToken ct)
    {
        var minTs = _db.GetMinReadingTs();
        if (minTs < 0) return 0; // no readings yet

        var partHours = Math.Max(1, schedule.PartitionHours);
        var partMs = partHours * 3_600_000L;
        var site = Sanitize(_config.GetSiteName());

        var cursor = _db.GetPartitionExportCursor();
        var startFrom = Math.Max(minTs, cursor);
        var firstStart = startFrom / partMs * partMs;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var lastCompletedStart = nowMs / partMs * partMs - partMs; // last fully-elapsed window

        var files = 0;
        for (var start = firstStart; start <= lastCompletedStart; start += partMs)
        {
            if (ct.IsCancellationRequested) break;
            var end = start + partMs;
            var fileName = $"{site}-{DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime:yyyyMMddTHHmm}Z.parquet";
            var filePath = Path.Combine(PartitionsDir, fileName);

            var result = await _parquet.ExportAsync(_db.ReadReadingsInRange(start, end), filePath, ct).ConfigureAwait(false);
            if (result.RowCount > 0)
            {
                _db.RecordPartitionExport(start, partHours, fileName, result.RowCount, result.MaxReadingId);
                _log.LogInformation("Partition: wrote {Rows} readings to {File}.", result.RowCount, fileName);
                files++;
            }
            _db.SetPartitionExportCursor(end); // advance past this window (empty ones aren't revisited)
        }
        return files;
    }

    private async Task<(int Uploaded, int Failed)> UploadPartitionsAsync(
        IReadOnlyList<PartitionUpload> pending, bool keep, CancellationToken ct)
    {
        var provider = _providers.Current;
        if (pending.Count == 0 || !await provider.IsConfiguredAsync(ct).ConfigureAwait(false))
            return (0, 0);

        var uploaded = 0;
        var failed = 0;
        foreach (var item in pending)
        {
            if (ct.IsCancellationRequested) break;
            var path = Path.Combine(PartitionsDir, item.FileName);
            if (!File.Exists(path))
            {
                _log.LogWarning("Partition {File} missing on disk; marking done to stop retrying.", item.FileName);
                _db.MarkPartitionLocalDeleted(item.PartitionStart);
                continue;
            }

            try
            {
                await provider.UploadAsync(path, _providers.DestinationFolder, ct).ConfigureAwait(false);
                _db.MarkPartitionUploaded(item.PartitionStart);
                _log.LogInformation("Uploaded {File}.", item.FileName);
                uploaded++;
                if (!keep)
                {
                    TryDelete(path);
                    _db.MarkPartitionLocalDeleted(item.PartitionStart);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Upload of {File} failed.", item.FileName);
                failed++;
                break; // connectivity — stop; the local file remains and retries next cycle.
            }
        }
        return (uploaded, failed);
    }

    private void CleanupKeptPartitions()
    {
        var retentionDays = _config.GetRetentionDays();
        if (retentionDays <= 0) return; // keep forever

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeMilliseconds();
        foreach (var (start, fileName) in _db.GetKeptPartitionsBefore(cutoff))
        {
            TryDelete(Path.Combine(PartitionsDir, fileName));
            _db.MarkPartitionLocalDeleted(start);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>On-demand rolling CSV export + upload: one overwritten <c>{Site}-{Plc}.csv</c> per PLC
    /// (kept as an option; no longer part of the scheduled cycle).</summary>
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

    /// <summary>
    /// On-demand export of a specific time window to its own uniquely-named file per PLC
    /// (<c>{Site}-{Plc}-{start}_{end}.csv</c>), then upload. Independent of the rolling export/upload
    /// state — these are one-off snapshots, so each is a fresh cloud file, never overwritten.
    /// </summary>
    public async Task<ExportRunResult> RunRangeExportAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
    {
        if (endUtc <= startUtc)
            return new ExportRunResult(0, 0, 0, "End time must be after start time.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var exportDir = Path.GetFullPath(_options.Export.DirectoryPath);
            var site = Sanitize(_config.GetSiteName());
            var startMs = ToEpochMs(startUtc);
            var endMs = ToEpochMs(endUtc);
            var stamp = $"{startUtc:yyyyMMddHHmm}_{endUtc:yyyyMMddHHmm}";
            var plcs = _db.GetLoggedPlcs();

            var files = new List<string>();
            var totalRows = 0;
            foreach (var plc in plcs)
            {
                var fileName = $"{site}-{Sanitize(plc.Name)}-{stamp}.csv";
                var filePath = Path.Combine(exportDir, fileName);
                var result = _exporter.Export(_db.ReadReadingsForPlcRange(plc.PlcId, startMs, endMs), filePath);
                if (result.RowCount == 0)
                {
                    _log.LogInformation("Range export: {Plc} has no readings in the window; skipped.", plc.Name);
                    continue;
                }
                _log.LogInformation("Range export: wrote {Rows} readings for {Plc} to {File}.", result.RowCount, plc.Name, fileName);
                files.Add(filePath);
                totalRows += result.RowCount;
            }

            if (totalRows == 0)
                return new ExportRunResult(0, 0, 0, "No readings found in that time window.");

            var (uploaded, failed) = await UploadFilesAsync(files, ct).ConfigureAwait(false);
            var msg = $"Exported {totalRows} reading(s) across {files.Count} file(s)."
                + UploadSuffix(uploaded, failed, files.Count);
            return new ExportRunResult(totalRows, uploaded, failed, msg);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// On-demand raw backup of the SQLite database, uploaded as a timestamped
    /// <c>{Site}-backup-{ts}.db</c> so a history of backups accumulates in the cloud, in addition to
    /// the single rolling <c>{Site}-backup.db</c> the schedule maintains.
    /// </summary>
    public async Task<ExportRunResult> RunDatabaseBackupAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var fileName = $"{Sanitize(_config.GetSiteName())}-backup-{DateTime.Now:yyyyMMdd_HHmm}.db";
            return await BackupAndUploadAsync(fileName, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Snapshot the database to <paramref name="fileName"/> (a consistent single-file
    /// <c>VACUUM INTO</c> copy) and upload it. On a successful upload the retention watermark is
    /// advanced to the max reading id captured, so pruning can reclaim it locally. Assumes the run
    /// gate is held.</summary>
    private async Task<ExportRunResult> BackupAndUploadAsync(string fileName, CancellationToken ct)
    {
        var exportDir = Path.GetFullPath(_options.Export.DirectoryPath);
        var filePath = Path.Combine(exportDir, fileName);

        var maxReadingId = _db.GetMaxReadingId();
        _db.BackupTo(filePath);
        var sizeMb = new FileInfo(filePath).Length / (1024.0 * 1024.0);
        _log.LogInformation("DB backup: wrote {File} ({Size:F1} MB).", fileName, sizeMb);

        var (uploaded, failed) = await UploadFilesAsync(new[] { filePath }, ct).ConfigureAwait(false);
        if (uploaded > 0)
            _db.SetBackupUploadedReadingId(maxReadingId); // the whole DB (up to this id) is now in the cloud

        var msg = $"Backed up database ({sizeMb:F1} MB)." + UploadSuffix(uploaded, failed, 1);
        return new ExportRunResult(0, uploaded, failed, msg);
    }

    private int Export()
    {
        var exportDir = Path.GetFullPath(_options.Export.DirectoryPath);
        var site = Sanitize(_config.GetSiteName());
        var plcs = _db.GetLoggedPlcs();

        if (plcs.Count == 0)
        {
            _log.LogInformation("Export: no PLCs with readings to export yet.");
            return 0;
        }

        var totalRows = 0;
        foreach (var plc in plcs)
        {
            // One stable, overwritten file per PLC: {SiteName}-{PlcName}.csv. Regenerated each run
            // from the retained readings, then re-uploaded over the same cloud file.
            var fileName = $"{site}-{Sanitize(plc.Name)}.csv";
            var filePath = Path.Combine(exportDir, fileName);

            var result = _exporter.Export(_db.ReadReadingsForPlc(plc.PlcId), filePath);
            if (result.RowCount == 0)
            {
                _log.LogDebug("Export: PLC {Plc} has no retained readings; left {File} untouched.", plc.Name, fileName);
                continue;
            }

            _db.RecordExport(plc.PlcId, fileName, filePath, result.MaxReadingId, result.RowCount);
            _log.LogInformation("Export: wrote {Rows} readings for {Plc} to {File} (max id {Max}).",
                result.RowCount, plc.Name, fileName, result.MaxReadingId);
            totalRows += result.RowCount;
        }

        return totalRows;
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
                _db.MarkUploaded(item.PlcId, item.LastReadingId);
                _log.LogInformation("Uploaded {File}.", item.FileName);
                uploaded++;
            }
            catch (Exception ex)
            {
                _db.MarkUploadFailed(item.PlcId);
                _log.LogWarning(ex, "Upload of {File} failed.", item.FileName);
                failed++;
                break; // Likely a connectivity problem — stop hammering, retry later.
            }
        }

        return (uploaded, failed, false);
    }

    /// <summary>Upload an explicit set of files (one-off exports / backups), independent of the
    /// per-PLC rolling upload state. Returns how many succeeded/failed; no-ops when the provider
    /// isn't configured.</summary>
    private async Task<(int Uploaded, int Failed)> UploadFilesAsync(IReadOnlyCollection<string> files, CancellationToken ct)
    {
        var provider = _providers.Current;
        if (files.Count == 0 || !await provider.IsConfiguredAsync(ct).ConfigureAwait(false))
            return (0, 0);

        var uploaded = 0;
        var failed = 0;
        foreach (var path in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await provider.UploadAsync(path, _providers.DestinationFolder, ct).ConfigureAwait(false);
                _log.LogInformation("Uploaded {File}.", Path.GetFileName(path));
                uploaded++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Upload of {File} failed.", Path.GetFileName(path));
                failed++;
                break; // Likely a connectivity problem — stop; the local file remains for manual pickup.
            }
        }
        return (uploaded, failed);
    }

    private string UploadSuffix(int uploaded, int failed, int fileCount)
    {
        if (_providers.Current.ProviderName == "None")
            return " Saved locally (no upload provider).";
        if (failed > 0)
            return $" Uploaded {uploaded}/{fileCount}, {failed} failed — see logs.";
        if (uploaded > 0)
            return $" Uploaded {uploaded} file(s).";
        return " Upload provider not configured.";
    }

    private static long ToEpochMs(DateTime dt) =>
        new DateTimeOffset(dt.ToUniversalTime()).ToUnixTimeMilliseconds();

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

    private static string Sanitize(string name)
    {
        var cleaned = string.Concat(name.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c));
        return string.IsNullOrWhiteSpace(cleaned) ? "Site" : cleaned;
    }
}
