using Parquet;
using Parquet.Serialization;

namespace PlcDataLogger.Storage;

// Note: Parquet.Net 6 uses Parquet.ParquetOptions (with Append + CompressionMethod) for serializer
// options; earlier versions used ParquetSerializerOptions.

/// <summary>One row in a Parquet time-partition. Property names become the column names.
/// Quality is the raw integer code (0=Good, 1=Uncertain, 2=Bad).</summary>
public sealed class ParquetReading
{
    public long ts_utc { get; set; }
    public long? ts_received_utc { get; set; }
    public string plc_name { get; set; } = "";
    public string tag_name { get; set; } = "";
    public double? value { get; set; }
    public string? value_text { get; set; }
    public int quality { get; set; }
}

/// <summary>Outcome of writing one Parquet partition.</summary>
public sealed record ParquetExportResult(int RowCount, long MaxReadingId);

/// <summary>
/// Writes a stream of readings to a zstd-compressed Parquet file, one row group per batch so memory
/// stays bounded regardless of how many rows a partition holds. Columnar + zstd shrinks the upload
/// dramatically versus a raw SQLite snapshot. The write is atomic (temp file, then move).
/// </summary>
public sealed class ParquetExporter
{
    private readonly int _rowGroupSize;

    public ParquetExporter(int rowGroupSize = 50_000) => _rowGroupSize = rowGroupSize;

    private static readonly ParquetOptions CreateOptions =
        new() { CompressionMethod = CompressionMethod.Zstd, Append = false };
    private static readonly ParquetOptions AppendOptions =
        new() { CompressionMethod = CompressionMethod.Zstd, Append = true };

    /// <summary>Write <paramref name="rows"/> to <paramref name="filePath"/>. Returns the row count
    /// and max reading id, or RowCount 0 (no file written) when the source is empty.</summary>
    public async Task<ParquetExportResult> ExportAsync(IEnumerable<RangeReading> rows, string filePath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = filePath + ".tmp";
        TryDelete(tempPath);

        var batch = new List<ParquetReading>(_rowGroupSize);
        var rowCount = 0;
        long maxId = 0;
        var wroteAny = false;

        foreach (var r in rows)
        {
            batch.Add(new ParquetReading
            {
                ts_utc = r.TsUtcMs,
                ts_received_utc = r.TsReceivedMs,
                plc_name = r.PlcName,
                tag_name = r.TagName,
                value = r.Value,
                value_text = r.ValueText,
                quality = r.Quality,
            });
            rowCount++;
            if (r.Id > maxId) maxId = r.Id;

            if (batch.Count >= _rowGroupSize)
            {
                await WriteBatchAsync(tempPath, batch, wroteAny, ct).ConfigureAwait(false);
                wroteAny = true;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await WriteBatchAsync(tempPath, batch, wroteAny, ct).ConfigureAwait(false);
            wroteAny = true;
        }

        if (!wroteAny)
        {
            TryDelete(tempPath);
            return new ParquetExportResult(0, 0);
        }

        File.Move(tempPath, filePath, overwrite: true);
        return new ParquetExportResult(rowCount, maxId);
    }

    // Each batch is a new row group. The first creates the file; the rest reopen and append.
    private static async Task WriteBatchAsync(string path, IReadOnlyCollection<ParquetReading> batch, bool append, CancellationToken ct)
    {
        await using var stream = append
            ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite)
            : new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        await ParquetSerializer.SerializeAsync(batch, stream, append ? AppendOptions : CreateOptions, null, ct).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
