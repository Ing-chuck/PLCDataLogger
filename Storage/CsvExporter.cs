using System.Globalization;
using System.Text;

namespace PlcDataLogger.Storage;

/// <summary>Outcome of a single CSV export run.</summary>
public sealed record ExportResult(int RowCount, long MaxReadingId, string? PeriodStart, string? PeriodEnd);

/// <summary>
/// Writes readings newer than a given id to a flat, long-format CSV file (§5, §6.2):
/// one row per reading — <c>timestamp_utc, plc_name, tag_name, value, quality</c>.
/// Streams through a read-only connection so it never blocks the storage writer.
/// </summary>
public sealed class CsvExporter
{
    private readonly LoggerDatabase _db;

    public CsvExporter(LoggerDatabase db) => _db = db;

    /// <summary>
    /// Export every reading with <c>id &gt; afterReadingId</c> to <paramref name="filePath"/>.
    /// Returns the row count and the maximum reading id written (0 rows if nothing new).
    /// </summary>
    public ExportResult Export(long afterReadingId, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var conn = _db.OpenReadOnlyConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.ts_utc, p.name AS plc_name, t.tag_name, r.value, r.value_text, r.quality
            FROM readings r
            JOIN tags t ON t.tag_id = r.tag_id
            JOIN plcs p ON p.plc_id = t.plc_id
            WHERE r.id > $afterId
            ORDER BY r.id;
            """;
        cmd.Parameters.AddWithValue("$afterId", afterReadingId);

        using var reader = cmd.ExecuteReader();

        var rowCount = 0;
        long maxId = afterReadingId;
        string? firstTs = null;
        string? lastTs = null;

        using var writer = new StreamWriter(filePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("timestamp_utc,plc_name,tag_name,value,quality");

        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var ts = reader.GetString(1);
            var plcName = reader.GetString(2);
            var tagName = reader.GetString(3);
            string value = reader.IsDBNull(4)
                ? (reader.IsDBNull(5) ? "" : reader.GetString(5))
                : reader.GetDouble(4).ToString(CultureInfo.InvariantCulture);
            var quality = reader.GetString(6);

            writer.Write(Csv(ts));
            writer.Write(',');
            writer.Write(Csv(plcName));
            writer.Write(',');
            writer.Write(Csv(tagName));
            writer.Write(',');
            writer.Write(Csv(value));
            writer.Write(',');
            writer.Write(Csv(quality));
            writer.Write('\n');

            rowCount++;
            maxId = id;
            firstTs ??= ts;
            lastTs = ts;
        }

        return new ExportResult(rowCount, maxId, firstTs, lastTs);
    }

    /// <summary>Minimal RFC-4180 field escaping.</summary>
    private static string Csv(string field)
    {
        if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return field;
        return '"' + field.Replace("\"", "\"\"") + '"';
    }
}
