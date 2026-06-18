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
