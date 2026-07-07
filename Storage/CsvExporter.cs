using System.Globalization;
using System.Text;

namespace PlcDataLogger.Storage;

/// <summary>Outcome of a single CSV export run.</summary>
public sealed record ExportResult(int RowCount, long MaxReadingId, string? PeriodStart, string? PeriodEnd);

/// <summary>
/// Writes a rolling snapshot of readings to a flat, long-format CSV file (§5, §6.2):
/// one row per reading — <c>timestamp_utc, plc_name, tag_name, value, quality</c>. Streams the
/// rows so it never holds the whole result set in memory or blocks ingestion. Timestamps are
/// rendered back to ISO 8601 UTC for the file even though they are stored as epoch-ms.
///
/// The write is atomic: rows go to a temp file first and only replace the target once complete,
/// so a re-upload never races a half-written file and an empty result never clobbers a good file.
/// </summary>
public sealed class CsvExporter
{
    /// <summary>
    /// Overwrite <paramref name="filePath"/> with every row in <paramref name="rows"/>.
    /// Returns the row count and the maximum reading id written. When the source is empty the
    /// existing file is left untouched (RowCount 0), so a momentarily-empty store can't wipe it.
    /// </summary>
    public ExportResult Export(IEnumerable<ExportRow> rows, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = filePath + ".tmp";
        var rowCount = 0;
        long maxId = 0;
        string? firstTs = null;
        string? lastTs = null;

        using (var writer = new StreamWriter(tempPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.WriteLine("timestamp_utc,plc_name,tag_name,value,quality");

            foreach (var row in rows)
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
        }

        if (rowCount == 0)
        {
            TryDelete(tempPath);
            return new ExportResult(0, 0, null, null);
        }

        File.Move(tempPath, filePath, overwrite: true);
        return new ExportResult(rowCount, maxId, firstTs, lastTs);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
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
