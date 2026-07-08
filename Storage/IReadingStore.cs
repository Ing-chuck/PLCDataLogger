namespace PlcDataLogger.Storage;

/// <summary>A discovered OPC UA variable node that should be logged.</summary>
public sealed record DiscoveredTag(string NodeId, string Name);

/// <summary>A subscribed tag: node id, its database id, and any per-tag deadband override.</summary>
public sealed record TagBinding(string NodeId, int TagId, double? Deadband);

/// <summary>A per-PLC export file whose contents have changed since the last successful upload.</summary>
public sealed record PendingUpload(int PlcId, string FileName, string FilePath, long LastReadingId);

/// <summary>An exported Parquet partition file awaiting (or retrying) upload.</summary>
public sealed record PartitionUpload(long PartitionStart, string FileName, long MaxReadingId);

/// <summary>A PLC that has logged readings, identified for per-PLC export.</summary>
public sealed record LoggedPlc(int PlcId, string Name);

/// <summary>A discovered tag and whether the user has selected it for logging.</summary>
public sealed record TagSelection(int TagId, string TagName, bool Enabled);

/// <summary>One row streamed out for CSV export (timestamps as epoch-ms UTC).</summary>
public sealed record ExportRow(
    long Id, long TsUtcMs, string PlcName, string TagName, double? Value, string? ValueText, string Quality);

/// <summary>One reading streamed out for a Parquet time-partition (raw column values: quality as its
/// integer code, both epoch-ms timestamps).</summary>
public sealed record RangeReading(
    long Id, long TsUtcMs, long? TsReceivedMs, string PlcName, string TagName,
    double? Value, string? ValueText, int Quality);

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

    /// <summary>Reconcile a PLC's discovered tags and return the live subscription bindings
    /// (active <b>and</b> user-enabled tags only).</summary>
    IReadOnlyList<TagBinding> SyncTags(int plcId, IReadOnlyCollection<DiscoveredTag> tags);

    /// <summary>Current subscription bindings for a PLC (active + enabled), without re-browsing —
    /// used to rebuild the subscription after a tag-selection change.</summary>
    IReadOnlyList<TagBinding> GetEnabledBindings(int plcId);

    /// <summary>All active tags for a PLC with their logged/unlogged selection, ordered by name.</summary>
    IReadOnlyList<TagSelection> GetTagSelection(int plcId);

    /// <summary>Set exactly <paramref name="enabledTagIds"/> as logged for a PLC; all other active
    /// tags become unlogged.</summary>
    void SetTagsEnabled(int plcId, IReadOnlyCollection<int> enabledTagIds);

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

    /// <summary>Stream all PLCs' readings whose source timestamp falls in [startMs, endMs) (raw column
    /// values), ordered by id. Backs the scheduled Parquet time-partition export.</summary>
    IEnumerable<RangeReading> ReadReadingsInRange(long startMs, long endMs);

    /// <summary>Write a consistent single-file snapshot of the whole database to <paramref name="destPath"/>
    /// (via <c>VACUUM INTO</c>) — safe to run while logging continues. Used for the raw DB backup.</summary>
    void BackupTo(string destPath);

    /// <summary>Highest reading id currently in the store.</summary>
    long GetMaxReadingId();

    /// <summary>Oldest reading source timestamp (epoch-ms), or -1 if the store is empty.</summary>
    long GetMinReadingTs();

    // ── Parquet partition export state ──────────────────────────────────────────
    /// <summary>How far (epoch-ms) partition export has advanced; 0 if none yet.</summary>
    long GetPartitionExportCursor();
    void SetPartitionExportCursor(long endMs);

    /// <summary>Record a freshly-written partition file (idempotent on partition_start).</summary>
    void RecordPartitionExport(long partitionStart, int partitionHours, string fileName, int rowCount, long maxReadingId);

    /// <summary>Partition files written but not yet uploaded (and not locally deleted).</summary>
    IReadOnlyList<PartitionUpload> GetPendingPartitionUploads();
    void MarkPartitionUploaded(long partitionStart);
    void MarkPartitionLocalDeleted(long partitionStart);

    /// <summary>Uploaded partition files still on disk whose window starts before <paramref name="cutoffStartMs"/>
    /// — eligible for local cleanup when "keep uploaded partitions" is on.</summary>
    IReadOnlyList<(long PartitionStart, string FileName)> GetKeptPartitionsBefore(long cutoffStartMs);

    /// <summary>Reading id up to which a database backup has been uploaded — the safe pruning
    /// watermark, so retention never drops a reading before a full snapshot of it reached the cloud.</summary>
    long GetMaxUploadedReadingId();

    /// <summary>Record that a backup containing readings up to <paramref name="readingId"/> was
    /// uploaded, advancing the retention pruning watermark.</summary>
    void SetBackupUploadedReadingId(long readingId);

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
