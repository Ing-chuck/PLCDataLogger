using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PlcDataLogger.Health;

namespace PlcDataLogger.Storage;

/// <summary>
/// Drains the in-memory buffer and writes readings to SQLite in batched transactions
/// (§5 Storage Writer). Batches on a short interval so the OPC UA side never blocks on disk.
/// </summary>
public sealed class StorageWriter : BackgroundService
{
    private const int MaxBatchSize = 1000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(1000);

    private readonly ReadingBuffer _buffer;
    private readonly LoggerDatabase _db;
    private readonly HealthMonitor _health;
    private readonly ILogger<StorageWriter> _log;
    private readonly HashSet<int> _typedTags = new();

    public StorageWriter(ReadingBuffer buffer, LoggerDatabase db, HealthMonitor health, ILogger<StorageWriter> log)
    {
        _buffer = buffer;
        _db = db;
        _health = health;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _db.Initialize();
        _log.LogInformation("Storage writer started.");

        var reader = _buffer.Reader;
        var batch = new List<Reading>(MaxBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(stoppingToken))
                    break; // channel completed

                // Accumulate briefly so writes batch up instead of one row per transaction.
                await Task.Delay(FlushInterval, stoppingToken);

                while (batch.Count < MaxBatchSize && reader.TryRead(out var r))
                    batch.Add(r);

                if (batch.Count == 0)
                    continue;

                _db.InsertReadings(batch);
                RecordNewlyTypedTags(batch);
                _health.AddWritten(batch.Count);
                _log.LogDebug("Persisted {Count} readings.", batch.Count);
                batch.Clear();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to persist a batch of {Count} readings; will retry.", batch.Count);
                batch.Clear();
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ContinueWith(_ => { });
            }
        }

        // Final drain on shutdown.
        batch.Clear();
        while (reader.TryRead(out var r))
            batch.Add(r);
        if (batch.Count > 0)
        {
            try { _db.InsertReadings(batch); }
            catch (Exception ex) { _log.LogError(ex, "Failed to flush {Count} readings on shutdown.", batch.Count); }
        }

        _log.LogInformation("Storage writer stopped.");
    }

    private void RecordNewlyTypedTags(IReadOnlyList<Reading> batch)
    {
        foreach (var r in batch)
        {
            if (r.DataTypeName == "Null" || _typedTags.Contains(r.TagId))
                continue;
            _db.SetTagDataType(r.TagId, r.DataTypeName);
            _typedTags.Add(r.TagId);
        }
    }
}
