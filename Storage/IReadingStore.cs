namespace PlcDataLogger.Storage;

/// <summary>A discovered OPC UA variable node that should be logged.</summary>
public sealed record DiscoveredTag(string NodeId, string Name);

/// <summary>A subscribed tag: node id, its database id, and any per-tag deadband override.</summary>
public sealed record TagBinding(string NodeId, int TagId, double? Deadband);

/// <summary>An export file awaiting (or retrying) upload.</summary>
public sealed record PendingUpload(long UploadLogId, string FileName, string FilePath, long MaxReadingId);

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

    /// <summary>Stream readings with id greater than <paramref name="afterId"/>, ordered by id.</summary>
    IEnumerable<ExportRow> ReadReadingsAfter(long afterId);

    long GetMaxExportedReadingId();
    long GetMaxUploadedReadingId();

    long RecordExport(string fileName, string filePath, string? periodStart, string? periodEnd,
        long maxReadingId, int rowCount);
    List<PendingUpload> GetPendingUploads();
    void MarkUploaded(long uploadLogId);
    void MarkUploadFailed(long uploadLogId);

    /// <summary>Prune readings older than the cutoff (optionally id-gated by upload watermark),
    /// reclaiming space. Returns the number of rows deleted.</summary>
    int PruneReadings(DateTime cutoffUtc, long? maxReadingIdInclusive);

    string? GetLastExportedAt();
    string? GetLastUploadedAt();
}
