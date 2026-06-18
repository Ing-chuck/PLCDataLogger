namespace PlcDataLogger.Configuration;

/// <summary>
/// Root configuration for the logger, bound from the "Logger" section of
/// appsettings.json. This is the per-site configuration described in the
/// architecture (§10); the binary is identical between sites, only this differs.
/// </summary>
public sealed class LoggerOptions
{
    public const string SectionName = "Logger";

    public string SiteName { get; set; } = "Site";

    /// <summary>Path to the local SQLite database (the source of truth).</summary>
    public string DatabasePath { get; set; } = "data/plcdata.db";

    public DiscoveryOptions Discovery { get; set; } = new();

    public SubscriptionOptions Subscription { get; set; } = new();

    public List<PlcOptions> Plcs { get; set; } = new();

    public StorageOptions Storage { get; set; } = new();

    public ExportOptions Export { get; set; } = new();

    public UploadOptions Upload { get; set; } = new();

    public WebUiOptions WebUi { get; set; } = new();
}

public sealed class WebUiOptions
{
    /// <summary>Local TCP port for the status/config web UI. Bound to localhost only (§11).</summary>
    public int Port { get; set; } = 5198;
}

public sealed class StorageOptions
{
    /// <summary>Readings older than this are eligible for pruning (§8). When upload is
    /// enabled, a reading is only pruned once it has been confirmed uploaded.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>How often the retention sweep runs.</summary>
    public int RetentionCheckIntervalMinutes { get; set; } = 60;
}

public sealed class ExportOptions
{
    /// <summary>Folder (relative to the executable) where CSV exports are written.</summary>
    public string DirectoryPath { get; set; } = "exports";

    /// <summary>Local time of day to run the daily export, "HH:mm".</summary>
    public string DailyAtLocalTime { get; set; } = "02:00";

    /// <summary>Run one export shortly after startup (handy for dev/verification).</summary>
    public bool RunOnStartup { get; set; } = false;
}

public sealed class UploadOptions
{
    /// <summary>"None" (default, fully supported permanent state for offline sites) or
    /// "GoogleDrive".</summary>
    public string Provider { get; set; } = "None";

    /// <summary>Destination folder/path within the provider.</summary>
    public string DestinationFolder { get; set; } = "PLCDataLogger";

    public GoogleDriveOptions GoogleDrive { get; set; } = new();
}

public sealed class GoogleDriveOptions
{
    /// <summary>Path to the OAuth client secrets JSON (downloaded from Google Cloud).</summary>
    public string CredentialsPath { get; set; } = "google_client.json";

    /// <summary>Directory where the DPAPI-encrypted OAuth token is cached.</summary>
    public string TokenStorePath { get; set; } = "google_token";
}

public sealed class DiscoveryOptions
{
    /// <summary>How often to re-browse each PLC's address space to pick up
    /// added/removed tags after a Codesys project update (§7).</summary>
    public int RescanIntervalMinutes { get; set; } = 1440;

    /// <summary>Which discovered variables actually get logged. Defaults to the
    /// known Codesys application pattern for these PLCs.</summary>
    public TagFilterOptions Filter { get; set; } = new();
}

/// <summary>
/// Restricts discovery to the tags we actually want to log. For these Codesys PLCs the
/// real data is always at <c>ns=4;s=|var|{PLC_name}.Application.*</c>, so the defaults
/// target namespace 4 + a node-id containing ".Application.". Adjust per site as needed.
/// </summary>
public sealed class TagFilterOptions
{
    /// <summary>Always drop namespace 0 (standard server/diagnostics nodes).</summary>
    public bool ExcludeNamespaceZero { get; set; } = true;

    /// <summary>Only keep variables in these namespace indices. Empty = any (still subject
    /// to <see cref="ExcludeNamespaceZero"/>).</summary>
    public List<int> IncludeNamespaceIndices { get; set; } = new() { 4 };

    /// <summary>Only keep variables whose node-id string contains <b>all</b> of these
    /// substrings (case-insensitive). Defaults to the Codesys application-variable pattern
    /// (the "|var|" marker under ".Application."), which excludes static "|vprop|" device-info
    /// properties. Empty = no constraint.</summary>
    public List<string> NodeIdMustContain { get; set; } = new() { "|var|", ".Application." };
}

public sealed class SubscriptionOptions
{
    /// <summary>Subscription publishing interval — how often the server sends a batch of changes.</summary>
    public int PublishingIntervalMs { get; set; } = 1000;

    /// <summary>Monitored-item sampling interval — how often the server samples each tag.</summary>
    public int SamplingIntervalMs { get; set; } = 1000;

    /// <summary>Server-side queue depth per monitored item, so bursts aren't dropped.</summary>
    public int QueueSize { get; set; } = 10;
}

public sealed class PlcOptions
{
    public string Name { get; set; } = "PLC";

    /// <summary>OPC UA endpoint, e.g. opc.tcp://192.168.1.10:4840</summary>
    public string EndpointUrl { get; set; } = "";

    /// <summary>"None" for commissioning, or e.g. "Basic256Sha256" in production (§11).</summary>
    public string SecurityPolicy { get; set; } = "None";
}
