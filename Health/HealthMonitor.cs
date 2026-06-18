using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PlcDataLogger.Configuration;
using PlcDataLogger.Storage;
using PlcDataLogger.Upload;

namespace PlcDataLogger.Health;

/// <summary>Per-PLC health, as surfaced to the status UI (§5 Health Monitor).</summary>
public sealed class PlcHealth
{
    public required string Name { get; init; }
    public string EndpointUrl { get; set; } = "";
    public bool Connected { get; set; }
    public DateTime? LastConnectedUtc { get; set; }
    public DateTime? LastSampleUtc { get; set; }
    public int DiscoveredTags { get; set; }
    public int MonitoredItems { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Point-in-time snapshot of overall logger health.</summary>
public sealed class HealthSnapshot
{
    public DateTime GeneratedUtc { get; init; }
    public required string SiteName { get; init; }
    public required IReadOnlyList<PlcHealth> Plcs { get; init; }
    public long ReadingsWritten { get; init; }
    public DateTime? LastWriteUtc { get; init; }
    public long BufferDepth { get; init; }
    public string UploadProvider { get; init; } = "None";
    public string? LastExportUtc { get; init; }
    public string? LastUploadUtc { get; init; }
    public long FreeDiskMb { get; init; }
}

/// <summary>
/// Central, thread-safe collector of runtime health. Components push state changes in;
/// the web UI and the /api/health endpoint pull a <see cref="HealthSnapshot"/> out.
/// Distinguishes "upload not configured" (neutral) from "configured but failing" (§9).
/// </summary>
public sealed class HealthMonitor
{
    private readonly ConcurrentDictionary<string, PlcHealth> _plcs = new();
    private readonly LoggerOptions _options;
    private readonly ReadingBuffer _buffer;
    private readonly LoggerDatabase _db;
    private readonly ICloudUploadProvider _provider;

    private long _readingsWritten;
    private DateTime? _lastWriteUtc;

    public HealthMonitor(
        IOptions<LoggerOptions> options,
        ReadingBuffer buffer,
        LoggerDatabase db,
        ICloudUploadProvider provider)
    {
        _options = options.Value;
        _buffer = buffer;
        _db = db;
        _provider = provider;
    }

    public void SetConnected(string plc, string endpointUrl)
    {
        var h = Get(plc);
        h.EndpointUrl = endpointUrl;
        h.Connected = true;
        h.LastConnectedUtc = DateTime.UtcNow;
        h.LastError = null;
    }

    public void SetDisconnected(string plc, string? error = null)
    {
        var h = Get(plc);
        h.Connected = false;
        if (error is not null) h.LastError = error;
    }

    public void SetDiscovery(string plc, int discoveredTags, int monitoredItems)
    {
        var h = Get(plc);
        h.DiscoveredTags = discoveredTags;
        h.MonitoredItems = monitoredItems;
    }

    public void MarkSample(string plc) => Get(plc).LastSampleUtc = DateTime.UtcNow;

    public void AddWritten(int count)
    {
        Interlocked.Add(ref _readingsWritten, count);
        _lastWriteUtc = DateTime.UtcNow;
    }

    public HealthSnapshot Snapshot()
    {
        var written = Interlocked.Read(ref _readingsWritten);
        var depth = Math.Max(0, _buffer.Enqueued - written);

        string? lastExport = null, lastUpload = null;
        if (_db.IsInitialized)
        {
            try { lastExport = _db.GetLastExportedAt(); lastUpload = _db.GetLastUploadedAt(); }
            catch { /* health must never throw */ }
        }

        return new HealthSnapshot
        {
            GeneratedUtc = DateTime.UtcNow,
            SiteName = _options.SiteName,
            Plcs = _plcs.Values.OrderBy(p => p.Name).ToList(),
            ReadingsWritten = written,
            LastWriteUtc = _lastWriteUtc,
            BufferDepth = depth,
            UploadProvider = _provider.ProviderName,
            LastExportUtc = lastExport,
            LastUploadUtc = lastUpload,
            FreeDiskMb = FreeDiskMb(),
        };
    }

    private PlcHealth Get(string plc) => _plcs.GetOrAdd(plc, n => new PlcHealth { Name = n });

    private long FreeDiskMb()
    {
        try
        {
            var root = _db.IsInitialized ? Path.GetPathRoot(_db.FullPath) : null;
            if (string.IsNullOrEmpty(root)) return -1;
            return new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
        }
        catch { return -1; }
    }
}
