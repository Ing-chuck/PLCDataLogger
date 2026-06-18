using System.Globalization;
using System.Text;

namespace PlcDataLogger.Storage;

/// <summary>Outcome of a single CSV export run.</summary>
public sealed record ExportResult(int RowCount, long MaxReadingId, string? PeriodStart, string? PeriodEnd);

/// <summary>
/// Writes readings newer than a given id to a flat, long-format CSV file (§5, §6.2):
/// one row per reading — <c>timestamp_utc, plc_name, tag_name, value, quality</c>. Streams from
/// the store so it never holds the whole result set in memory or blocks ingestion. Timestamps are
/// rendered back to ISO 8601 UTC for the file even though they are stored as epoch-ms.
/// </summary>
public sealed class CsvExporter
{
    private readonly IReadingStore _store;

    public CsvExporter(IReadingStore store) => _store = store;

    /// <summary>
    /// Export every reading with <c>id &gt; afterReadingId</c> to <paramref name="filePath"/>.
    /// Returns the row count and the maximum reading id written (0 rows if nothing new).
    /// </summary>
    public ExportResult Export(long afterReadingId, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var rowCount = 0;
        long maxId = afterReadingId;
        string? firstTs = null;
        string? lastTs = null;

        using var writer = new StreamWriter(filePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("timestamp_utc,plc_name,tag_name,value,quality");

        foreach (var row in _store.ReadReadingsAfter(afterReadingId))
        {
            var ts = Iso(row.TsUtcMs);
            var value = row.Value is { } v
                ? v.ToString(CultureInfo.InvariantCulture)
                : (row.ValueText ?? "");

            writer.Write(Csv(ts));
            writer.Write(',');
            writer.Write(Csv(row.PlcName));
            writer.Write(',');
            writer.Write(Csv(row.TagName));
            writer.Write(',');
            writer.Write(Csv(value));
            writer.Write(',');
            writer.Write(Csv(row.Quality));
            writer.Write('\n');

            rowCount++;
            maxId = row.Id;
            firstTs ??= ts;
            lastTs = ts;
        }

        return new ExportResult(rowCount, maxId, firstTs, lastTs);
    }

    private static string Iso(long epochMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>Minimal RFC-4180 field escaping.</summary>
    private static string Csv(string field)
    {
        if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return field;
        return '"' + field.Replace("\"", "\"\"") + '"';
    }
}
