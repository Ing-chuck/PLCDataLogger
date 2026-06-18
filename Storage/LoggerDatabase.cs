using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PlcDataLogger.Configuration;

namespace PlcDataLogger.Storage;

/// <summary>
/// Optimized SQLite implementation of <see cref="IReadingStore"/> — the local time-series source
/// of truth (§6). WAL mode for crash safety; a single writer connection serialized by a lock.
///
/// Storage is tuned for the high-volume readings table: timestamps are stored as INTEGER epoch
/// milliseconds (not ISO text) and quality as a small INTEGER code, which roughly halves row +
/// index size and speeds range scans/prunes. The low-volume metadata tables keep human-readable
/// ISO text. <c>auto_vacuum=INCREMENTAL</c> lets retention reclaim space without a full VACUUM lock.
/// </summary>
public sealed class LoggerDatabase : IReadingStore, IDisposable
{
    private const int PruneBatchSize = 50_000;

    private readonly object _gate = new();
    private readonly string _databasePath;
    private SqliteConnection? _connection;
    private string? _fullPath;
    private volatile bool _initialized;

    public LoggerDatabase(IOptions<LoggerOptions> options)
    {
        _databasePath = options.Value.DatabasePath;
    }

    public bool IsInitialized => _initialized;

    public string PrimaryPath => _fullPath ?? throw new InvalidOperationException("Database not initialized.");

    public void Initialize()
    {
        lock (_gate)
        {
            var fullPath = Path.GetFullPath(_databasePath);
            _fullPath = fullPath;
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            _connection = new SqliteConnection($"Data Source={fullPath}");
            _connection.Open();

            // auto_vacuum must be set before tables exist on a fresh database.
            Execute("PRAGMA auto_vacuum=INCREMENTAL;");
            Execute("PRAGMA journal_mode=WAL;");
            Execute("PRAGMA synchronous=NORMAL;");
            Execute("PRAGMA foreign_keys=ON;");

            Execute(SchemaSql);
            _initialized = true;
        }
    }

    public string? GetLastExportedAt() => ScalarString("SELECT MAX(exported_at) FROM upload_log;");

    public string? GetLastUploadedAt() =>
        ScalarString("SELECT MAX(uploaded_at) FROM upload_log WHERE status = 'Uploaded';");

    public int UpsertPlc(string name, string endpointUrl, string securityPolicy)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO plcs (name, endpoint_url, security_policy)
                VALUES ($name, $url, $sec)
                ON CONFLICT(name) DO UPDATE SET
                    endpoint_url = excluded.endpoint_url,
                    security_policy = excluded.security_policy
                RETURNING plc_id;
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$url", endpointUrl);
            cmd.Parameters.AddWithValue("$sec", securityPolicy);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public IReadOnlyList<TagBinding> SyncTags(int plcId, IReadOnlyCollection<DiscoveredTag> tags)
    {
        lock (_gate)
        {
            var now = IsoNow();
            using var tx = Conn.BeginTransaction();

            using (var upsert = Conn.CreateCommand())
            {
                upsert.Transaction = tx;
                upsert.CommandText = """
                    INSERT INTO tags (plc_id, node_id, tag_name, data_type, first_seen_at, last_seen_at, active)
                    VALUES ($plc, $node, $name, 'Unknown', $now, $now, 1)
                    ON CONFLICT(plc_id, node_id) DO UPDATE SET
                        tag_name = excluded.tag_name,
                        last_seen_at = excluded.last_seen_at,
                        active = 1;
                    """;
                var pPlc = upsert.Parameters.Add("$plc", SqliteType.Integer);
                var pNode = upsert.Parameters.Add("$node", SqliteType.Text);
                var pName = upsert.Parameters.Add("$name", SqliteType.Text);
                var pNow = upsert.Parameters.Add("$now", SqliteType.Text);
                pPlc.Value = plcId;
                pNow.Value = now;
                foreach (var t in tags)
                {
                    pNode.Value = t.NodeId;
                    pName.Value = t.Name;
                    upsert.ExecuteNonQuery();
                }
            }

            // Anything for this PLC not touched in this run is no longer exposed → inactive.
            using (var deactivate = Conn.CreateCommand())
            {
                deactivate.Transaction = tx;
                deactivate.CommandText =
                    "UPDATE tags SET active = 0 WHERE plc_id = $plc AND last_seen_at <> $now;";
                deactivate.Parameters.AddWithValue("$plc", plcId);
                deactivate.Parameters.AddWithValue("$now", now);
                deactivate.ExecuteNonQuery();
            }

            var bindings = new List<TagBinding>();
            using (var select = Conn.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText =
                    "SELECT node_id, tag_id, deadband_override FROM tags WHERE plc_id = $plc AND active = 1;";
                select.Parameters.AddWithValue("$plc", plcId);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var deadband = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2);
                    bindings.Add(new TagBinding(reader.GetString(0), reader.GetInt32(1), deadband));
                }
            }

            tx.Commit();
            return bindings;
        }
    }

    public void InsertReadings(IReadOnlyList<Reading> batch)
    {
        if (batch.Count == 0) return;
        lock (_gate)
        {
            using var tx = Conn.BeginTransaction();
            using var cmd = Conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO readings (tag_id, ts_utc, ts_received_utc, value, value_text, quality)
                VALUES ($tag, $ts, $rx, $val, $txt, $q);
                """;
            var pTag = cmd.Parameters.Add("$tag", SqliteType.Integer);
            var pTs = cmd.Parameters.Add("$ts", SqliteType.Integer);
            var pRx = cmd.Parameters.Add("$rx", SqliteType.Integer);
            var pVal = cmd.Parameters.Add("$val", SqliteType.Real);
            var pTxt = cmd.Parameters.Add("$txt", SqliteType.Text);
            var pQ = cmd.Parameters.Add("$q", SqliteType.Integer);

            foreach (var r in batch)
            {
                pTag.Value = r.TagId;
                pTs.Value = ToEpochMs(r.TsSourceUtc);
                pRx.Value = ToEpochMs(r.TsReceivedUtc);
                pVal.Value = (object?)r.Value ?? DBNull.Value;
                pTxt.Value = (object?)r.ValueText ?? DBNull.Value;
                pQ.Value = QualityCode(r.Quality);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public IEnumerable<ExportRow> ReadReadingsAfter(long afterId)
    {
        // Separate read-only connection: WAL lets this stream concurrently with the writer.
        using var conn = new SqliteConnection($"Data Source={PrimaryPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.ts_utc, p.name, t.tag_name, r.value, r.value_text, r.quality
            FROM readings r
            JOIN tags t ON t.tag_id = r.tag_id
            JOIN plcs p ON p.plc_id = t.plc_id
            WHERE r.id > $afterId
            ORDER BY r.id;
            """;
        cmd.Parameters.AddWithValue("$afterId", afterId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return new ExportRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                QualityName(reader.GetInt32(6)));
        }
    }

    public void SetTagDataType(int tagId, string dataType)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "UPDATE tags SET data_type = $dt WHERE tag_id = $id;";
            cmd.Parameters.AddWithValue("$dt", dataType);
            cmd.Parameters.AddWithValue("$id", tagId);
            cmd.ExecuteNonQuery();
        }
    }

    public long GetMaxExportedReadingId() => ScalarLong("SELECT MAX(max_reading_id) FROM upload_log;");

    public long GetMaxUploadedReadingId() =>
        ScalarLong("SELECT MAX(max_reading_id) FROM upload_log WHERE status = 'Uploaded';");

    public long RecordExport(string fileName, string filePath, string? periodStart, string? periodEnd,
        long maxReadingId, int rowCount)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO upload_log
                    (file_name, file_path, period_start, period_end, max_reading_id, row_count, exported_at, status)
                VALUES ($name, $path, $ps, $pe, $maxId, $rows, $now, 'Exported')
                RETURNING id;
                """;
            cmd.Parameters.AddWithValue("$name", fileName);
            cmd.Parameters.AddWithValue("$path", filePath);
            cmd.Parameters.AddWithValue("$ps", (object?)periodStart ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pe", (object?)periodEnd ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$maxId", maxReadingId);
            cmd.Parameters.AddWithValue("$rows", rowCount);
            cmd.Parameters.AddWithValue("$now", IsoNow());
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public List<PendingUpload> GetPendingUploads()
    {
        lock (_gate)
        {
            var list = new List<PendingUpload>();
            using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                "SELECT id, file_name, file_path, max_reading_id FROM upload_log WHERE status <> 'Uploaded' ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(new PendingUpload(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
            return list;
        }
    }

    public void MarkUploaded(long uploadLogId)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "UPDATE upload_log SET status = 'Uploaded', uploaded_at = $now WHERE id = $id;";
            cmd.Parameters.AddWithValue("$now", IsoNow());
            cmd.Parameters.AddWithValue("$id", uploadLogId);
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkUploadFailed(long uploadLogId)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "UPDATE upload_log SET status = 'UploadFailed' WHERE id = $id AND status <> 'Uploaded';";
            cmd.Parameters.AddWithValue("$id", uploadLogId);
            cmd.ExecuteNonQuery();
        }
    }

    public int PruneReadings(DateTime cutoffUtc, long? maxReadingIdInclusive)
    {
        var cutoff = ToEpochMs(cutoffUtc);
        lock (_gate)
        {
            var total = 0;
            // Delete in bounded batches so the WAL doesn't balloon on a large sweep.
            while (true)
            {
                using var cmd = Conn.CreateCommand();
                cmd.CommandText = maxReadingIdInclusive is null
                    ? "DELETE FROM readings WHERE id IN (SELECT id FROM readings WHERE ts_utc < $cutoff LIMIT $limit);"
                    : "DELETE FROM readings WHERE id IN (SELECT id FROM readings WHERE ts_utc < $cutoff AND id <= $maxId LIMIT $limit);";
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                cmd.Parameters.AddWithValue("$limit", PruneBatchSize);
                if (maxReadingIdInclusive is not null)
                    cmd.Parameters.AddWithValue("$maxId", maxReadingIdInclusive.Value);

                var deleted = cmd.ExecuteNonQuery();
                total += deleted;
                if (deleted < PruneBatchSize)
                    break;
            }

            if (total > 0)
            {
                Execute("PRAGMA incremental_vacuum;");
                Execute("PRAGMA wal_checkpoint(TRUNCATE);");
            }
            return total;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string? ScalarString(string sql)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = sql;
            var result = cmd.ExecuteScalar();
            return result is null or DBNull ? null : result.ToString();
        }
    }

    private long ScalarLong(string sql)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = sql;
            var result = cmd.ExecuteScalar();
            return result is null or DBNull ? 0L : Convert.ToInt64(result);
        }
    }

    private SqliteConnection Conn =>
        _connection ?? throw new InvalidOperationException("Database not initialized.");

    private void Execute(string sql)
    {
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long ToEpochMs(DateTime dt) => new DateTimeOffset(dt.ToUniversalTime()).ToUnixTimeMilliseconds();

    private static string IsoNow() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static int QualityCode(string quality) => quality switch
    {
        "Good" => 0,
        "Bad" => 2,
        _ => 1, // Uncertain
    };

    private static string QualityName(int code) => code switch
    {
        0 => "Good",
        2 => "Bad",
        _ => "Uncertain",
    };

    public void Dispose() => _connection?.Dispose();

    // Long/narrow schema (§6.1): one row per reading. The high-volume readings table uses
    // INTEGER epoch-ms timestamps and an INTEGER quality code; metadata tables keep ISO text.
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS plcs (
            plc_id          INTEGER PRIMARY KEY,
            name            TEXT NOT NULL UNIQUE,
            endpoint_url    TEXT NOT NULL,
            security_policy TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS tags (
            tag_id            INTEGER PRIMARY KEY,
            plc_id            INTEGER NOT NULL REFERENCES plcs(plc_id),
            node_id           TEXT NOT NULL,
            tag_name          TEXT NOT NULL,
            data_type         TEXT NOT NULL,
            units             TEXT,
            deadband_override REAL,
            first_seen_at     TEXT NOT NULL,
            last_seen_at      TEXT NOT NULL,
            active            INTEGER NOT NULL DEFAULT 1,
            UNIQUE(plc_id, node_id)
        );

        CREATE TABLE IF NOT EXISTS readings (
            id              INTEGER PRIMARY KEY,
            tag_id          INTEGER NOT NULL REFERENCES tags(tag_id),
            ts_utc          INTEGER NOT NULL,   -- epoch milliseconds UTC (source timestamp)
            ts_received_utc INTEGER,            -- epoch milliseconds UTC (received at logger)
            value           REAL,
            value_text      TEXT,
            quality         INTEGER NOT NULL    -- 0=Good, 1=Uncertain, 2=Bad
        );

        CREATE INDEX IF NOT EXISTS idx_readings_tag_ts ON readings(tag_id, ts_utc);

        CREATE TABLE IF NOT EXISTS upload_log (
            id             INTEGER PRIMARY KEY,
            file_name      TEXT NOT NULL,
            file_path      TEXT NOT NULL,
            period_start   TEXT,
            period_end     TEXT,
            max_reading_id INTEGER NOT NULL,
            row_count      INTEGER NOT NULL,
            exported_at    TEXT NOT NULL,
            uploaded_at    TEXT,
            status         TEXT NOT NULL        -- Exported | Uploaded | UploadFailed
        );

        CREATE TABLE IF NOT EXISTS settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;
}
