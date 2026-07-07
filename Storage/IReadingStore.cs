namespace PlcDataLogger.Storage;

/// <summary>A discovered OPC UA variable node that should be logged.</summary>
public sealed record DiscoveredTag(string NodeId, string Name);

/// <summary>A subscribed tag: node id, its database id, and any per-tag deadband override.</summary>
public sealed record TagBinding(string NodeId, int TagId, double? Deadband);

/// <summary>A per-PLC export file whose contents have changed since the last successful upload.</summary>
public sealed record PendingUpload(int PlcId, string FileName, string FilePath, long LastReadingId);

/// <summary>A PLC that has logged readings, identified for per-PLC export.</summary>
public sealed record LoggedPlc(int PlcId, string Name);

/// <summary>One row streamed out for CSV export (timestamps as epoch-ms UTC).</summary>
public sealed record ExportRow(
    long Id, long TsUtcMs, string PlcName, string TagName, double? Value, string? ValueText, string Quality);

/// <summary>
/// Abstraction over the local time-series store (§6). Keeps the engine choice (currently
/// optimized SQLite) behind a domain interface so the rest of the system — acquisition, export,
/// retention, health — never depends on storage internals and the engine can evolve without
/// touching them.
/// </summary>
public interface IReadingStore
{
    void Initialize();
    bool IsInitialized { get; }

    /// <summary>Filesystem path of the primary store, used for free-disk reporting.</summary>
    string PrimaryPath { get; }

    int UpsertPlc(string name, string endpointUrl, string securityPolicy);

    /// <summary>Reconcile a PLC's discovered tags and return the live subscription bindings.</summary>
    IReadOnlyList<TagBinding> SyncTags(int plcId, IReadOnlyCollection<DiscoveredTag> tags);

    void SetTagDataType(int tagId, string dataType);

    void InsertReadings(IReadOnlyList<Reading> batch);

    /// <summary>PLCs that currently have logged readings, for per-PLC export.</summary>
    IReadOnlyList<LoggedPlc> GetLoggedPlcs();

    /// <summary>Stream all currently-retained readings for one PLC, ordered by id. The export is a
    /// rolling snapshot of the local store, so it reflects whatever survives the retention window.</summary>
    IEnumerable<ExportRow> ReadReadingsForPlc(int plcId);

    /// <summary>Stream one PLC's readings whose source timestamp falls in [startMs, endMs], ordered by
    /// id. Backs the on-demand windowed export.</summary>
    IEnumerable<ExportRow> ReadReadingsForPlcRange(int plcId, long startMs, long endMs);

    /// <summary>Write a consistent single-file snapshot of the whole database to <paramref name="destPath"/>
    /// (via <c>VACUUM INTO</c>) — safe to run while logging continues. Used for the raw DB backup.</summary>
    void BackupTo(string destPath);

    /// <summary>Lowest reading id that every PLC's export file has been uploaded up to — the safe
    /// pruning watermark, so retention never drops a reading that hasn't reached the cloud yet.</summary>
    long GetMaxUploadedReadingId();

    /// <summary>Record that a PLC's export file was (re)written up to <paramref name="maxReadingId"/>.
    /// Marks it pending upload when its contents advanced past the last uploaded point.</summary>
    void RecordExport(int plcId, string fileName, string filePath, long maxReadingId, int rowCount);
    List<PendingUpload> GetPendingUploads();
    void MarkUploaded(int plcId, long uploadedReadingId);
    void MarkUploadFailed(int plcId);

    /// <summary>Prune readings older than the cutoff (optionally id-gated by upload watermark),
    /// reclaiming space. Returns the number of rows deleted.</summary>
    int PruneReadings(DateTime cutoffUtc, long? maxReadingIdInclusive);

    string? GetLastExportedAt();
    string? GetLastUploadedAt();
}
