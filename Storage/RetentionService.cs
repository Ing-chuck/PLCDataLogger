using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcDataLogger.Configuration;
using PlcDataLogger.Upload;

namespace PlcDataLogger.Storage;

/// <summary>
/// Prunes old readings to manage disk usage (§8), in one of two modes:
///   • Upload enabled (a real provider): only prune readings older than the retention window
///     that have also been confirmed uploaded — never drop un-uploaded data.
///   • Upload disabled ("None" provider): prune purely by age, since there is no upload to wait
///     for (offline sites). Very old data is then only retrievable directly from the machine.
/// </summary>
public sealed class RetentionService : BackgroundService
{
    private readonly LoggerOptions _options;
    private readonly LoggerDatabase _db;
    private readonly UploadProviderResolver _providers;
    private readonly ILogger<RetentionService> _log;

    public RetentionService(
        IOptions<LoggerOptions> options,
        LoggerDatabase db,
        UploadProviderResolver providers,
        ILogger<RetentionService> log)
    {
        _options = options.Value;
        _db = db;
        _providers = providers;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.Storage.RetentionCheckIntervalMinutes));
        _log.LogInformation("Retention service started (keep {Days} days).", _options.Storage.RetentionDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Recompute the mode each sweep — the upload provider can change at runtime.
                Sweep(_providers.Current.ProviderName != "None");
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Retention sweep failed; will retry.");
                try { await Task.Delay(interval, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void Sweep(bool uploadEnabled)
    {
        if (_options.Storage.RetentionDays <= 0)
            return; // retention disabled — keep everything

        var cutoff = DateTime.UtcNow.AddDays(-_options.Storage.RetentionDays);

        long? watermark = null;
        if (uploadEnabled)
        {
            watermark = _db.GetMaxUploadedReadingId();
            if (watermark == 0)
            {
                _log.LogDebug("Retention: nothing confirmed uploaded yet; skipping prune.");
                LogDiskSpace();
                return;
            }
        }

        var deleted = _db.PruneReadings(cutoff, watermark);
        if (deleted > 0)
            _log.LogInformation("Retention: pruned {Count} readings older than {Cutoff:u}.", deleted, cutoff);

        LogDiskSpace();
    }

    private void LogDiskSpace()
    {
        try
        {
            var root = Path.GetPathRoot(_db.FullPath);
            if (string.IsNullOrEmpty(root)) return;
            var drive = new DriveInfo(root);
            var freeMb = drive.AvailableFreeSpace / (1024 * 1024);
            if (freeMb < 1024)
                _log.LogWarning("Low disk space on {Drive}: {FreeMb} MB free.", drive.Name, freeMb);
            else
                _log.LogDebug("Disk free on {Drive}: {FreeMb} MB.", drive.Name, freeMb);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not determine free disk space.");
        }
    }
}
