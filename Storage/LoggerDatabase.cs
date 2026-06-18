using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PlcDataLogger.Configuration;

namespace PlcDataLogger.Storage;

/// <summary>
/// A discovered OPC UA variable node that should be logged.
/// </summary>
public sealed record DiscoveredTag(string NodeId, string Name);

/// <summary>An export file awaiting (or retrying) upload.</summary>
public sealed record PendingUpload(long UploadLogId, string FileName, string FilePath, long MaxReadingId);

/// <summary>
/// Owns the local SQLite store — the source of truth (§6). Uses WAL mode for crash
/// safety. A single connection is kept open for the process lifetime; all access is
/// serialized through a lock since one PLC discovery run and the storage writer can
/// touch the DB concurrently.
/// </summary>
public sealed class LoggerDatabase : IDisposable
{
    private readonly object _gate = new();
    private readonly string _databasePath;
    private SqliteConnection? _connection;
    private string? _fullPath;

    public LoggerDatabase(IOptions<LoggerOptions> options)
    {
        _databasePath = options.Value.DatabasePath;
    }

    /// <summary>Absolute path to the database file (valid after <see cref="Initialize"/>).</summary>
    public string FullPath => _fullPath ?? throw new InvalidOperationException("Database not initialized.");

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

            Execute("PRAGMA journal_mode=WAL;");
            Execute("PRAGMA synchronous=NORMAL;");
            Execute("PRAGMA foreign_keys=ON;");

            Execute(SchemaSql);
        }
    }

    /// <summary>Insert the PLC if new, returning its plc_id.</summary>
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

    /// <summary>
    /// Reconcile the discovered tag set for a PLC: upsert each tag (active=1, refreshed
    /// last_seen), mark any tag no longer present as inactive (never deleted, so history
    /// survives — §7), and return a node-id → tag_id map for subscription setup.
    /// </summary>
    public Dictionary<string, int> SyncTags(int plcId, IReadOnlyCollection<DiscoveredTag> tags)
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

            var map = new Dictionary<string, int>();
            using (var select = Conn.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = "SELECT node_id, tag_id FROM tags WHERE plc_id = $plc;";
                select.Parameters.AddWithValue("$plc", plcId);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                    map[reader.GetString(0)] = reader.GetInt32(1);
            }

            tx.Commit();
            return map;
        }
    }

    /// <summary>Batched insert of buffered readings inside a single transaction (§5 Storage Writer).</summary>
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
            var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
            var pRx = cmd.Parameters.Add("$rx", SqliteType.Text);
            var pVal = cmd.Parameters.Add("$val", SqliteType.Real);
            var pTxt = cmd.Parameters.Add("$txt", SqliteType.Text);
            var pQ = cmd.Parameters.Add("$q", SqliteType.Text);

            foreach (var r in batch)
            {
                pTag.Value = r.TagId;
                pTs.Value = Iso(r.TsSourceUtc);
                pRx.Value = Iso(r.TsReceivedUtc);
                pVal.Value = (object?)r.Value ?? DBNull.Value;
                pTxt.Value = (object?)r.ValueText ?? DBNull.Value;
                pQ.Value = r.Quality;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>Record a tag's data type once it is known from the first received value.</summary>
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

    /// <summary>Open a separate read-only connection. WAL mode allows this to run
    /// concurrently with the writer, so large CSV exports never block ingestion.</summary>
    public SqliteConnection OpenReadOnlyConnection()
    {
        var conn = new SqliteConnection($"Data Source={FullPath};Mode=ReadOnly");
        conn.Open();
        return conn;
    }

    /// <summary>Highest reading id that has been written to any export file.</summary>
    public long GetMaxExportedReadingId() => ScalarLong("SELECT MAX(max_reading_id) FROM upload_log;");

    /// <summary>Highest reading id confirmed uploaded — the watermark below which
    /// upload-gated pruning is allowed (§8).</summary>
    public long GetMaxUploadedReadingId() =>
        ScalarLong("SELECT MAX(max_reading_id) FROM upload_log WHERE status = 'Uploaded';");

    /// <summary>Record a freshly written export file (status 'Exported'), returning its id.</summary>
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

    /// <summary>Export files not yet uploaded (status != 'Uploaded'), oldest first.</summary>
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

    /// <summary>Delete readings older than <paramref name="cutoffUtc"/>. When
    /// <paramref name="maxReadingIdInclusive"/> is non-null, only readings at or below that
    /// id are pruned (upload-confirmed watermark). Returns the number of rows deleted.</summary>
    public int PruneReadings(DateTime cutoffUtc, long? maxReadingIdInclusive)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = maxReadingIdInclusive is null
                ? "DELETE FROM readings WHERE ts_utc < $cutoff;"
                : "DELETE FROM readings WHERE ts_utc < $cutoff AND id <= $maxId;";
            cmd.Parameters.AddWithValue("$cutoff", Iso(cutoffUtc));
            if (maxReadingIdInclusive is not null)
                cmd.Parameters.AddWithValue("$maxId", maxReadingIdInclusive.Value);
            return cmd.ExecuteNonQuery();
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

    private static string IsoNow() => Iso(DateTime.UtcNow);

    private static string Iso(DateTime dt) =>
        dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public void Dispose()
    {
        _connection?.Dispose();
    }

    // Long/narrow schema (§6.1): one row per reading, so adding/removing tags never
    // requires a schema migration. UNIQUE constraints added to make upserts clean.
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
            ts_utc          TEXT NOT NULL,
            ts_received_utc TEXT,
            value           REAL,
            value_text      TEXT,
            quality         TEXT NOT NULL
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
