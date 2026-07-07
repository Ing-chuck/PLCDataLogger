using System.Globalization;

namespace PlcDataLogger.Configuration;

/// <summary>A single configuration problem found during validation.</summary>
public sealed record ConfigIssue(string Severity, string Message)
{
    public const string Error = "Error";
    public const string Warning = "Warning";
    public bool IsError => Severity == Error;
}

/// <summary>
/// Validates configuration for multi-site deployment (§13). Validation is <b>non-fatal</b>: issues
/// are reported (logs, web UI, <c>/api/config/validate</c>) and invalid PLCs are skipped, but the
/// service still starts so the web UI stays reachable to fix the problem on site.
/// </summary>
public static class ConfigValidation
{
    private static readonly string[] KnownSecurityPolicies =
        { "None", "Basic256Sha256", "Basic128Rsa15", "Aes128_Sha256_RsaOaep", "Aes256_Sha256_RsaPss" };

    private static readonly string[] KnownProviders = { "None", "GoogleDrive" };

    /// <summary>Validate a single PLC connection; returns an error message or null if valid.</summary>
    public static string? ValidatePlc(PlcOptions plc)
    {
        if (string.IsNullOrWhiteSpace(plc.Name))
            return "Name is required.";
        if (string.IsNullOrWhiteSpace(plc.EndpointUrl))
            return "Endpoint URL is required.";
        if (!plc.EndpointUrl.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(plc.EndpointUrl, UriKind.Absolute, out _))
            return $"Endpoint must be a valid opc.tcp:// URL (got '{plc.EndpointUrl}').";
        if (!KnownSecurityPolicies.Contains(plc.SecurityPolicy, StringComparer.OrdinalIgnoreCase))
            return $"Unknown security policy '{plc.SecurityPolicy}'.";
        return null;
    }

    /// <summary>Validate the full effective configuration. Site name, PLCs and upload come from the
    /// runtime config store; the rest from appsettings-bound options.</summary>
    public static List<ConfigIssue> Validate(LoggerOptions options, string siteName, IReadOnlyList<PlcOptions> plcs, UploadOptions upload)
    {
        var issues = new List<ConfigIssue>();
        void Error(string m) => issues.Add(new ConfigIssue(ConfigIssue.Error, m));
        void Warn(string m) => issues.Add(new ConfigIssue(ConfigIssue.Warning, m));

        if (string.IsNullOrWhiteSpace(siteName))
            Warn("Site name is empty — exports and the dashboard will be unlabeled. Set it on the Settings page.");

        // PLCs
        if (plcs.Count == 0)
            Warn("No PLCs are configured — nothing will be logged until one is added.");

        foreach (var plc in plcs)
        {
            var error = ValidatePlc(plc);
            if (error is not null)
                Error($"PLC '{plc.Name}': {error}");
        }

        var dupes = plcs.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
        foreach (var dup in dupes)
            Error($"Duplicate PLC name '{dup.Key}' — names must be unique.");

        // Subscription
        if (options.Subscription.PublishingIntervalMs <= 0)
            Error("Subscription.PublishingIntervalMs must be greater than 0.");
        if (options.Subscription.SamplingIntervalMs < 0)
            Error("Subscription.SamplingIntervalMs must be 0 or greater.");
        if (options.Subscription.QueueSize < 1)
            Error("Subscription.QueueSize must be at least 1.");
        if (options.Subscription.DefaultDeadband < 0)
            Error("Subscription.DefaultDeadband must be 0 or greater.");

        // Discovery
        if (options.Discovery.RescanIntervalMinutes < 1)
            Error("Discovery.RescanIntervalMinutes must be at least 1.");

        // Storage
        if (options.Storage.RetentionDays < 0)
            Error("Storage.RetentionDays must be 0 (keep forever) or greater.");
        if (options.Storage.RetentionCheckIntervalMinutes < 1)
            Error("Storage.RetentionCheckIntervalMinutes must be at least 1.");

        // Export
        if (string.IsNullOrWhiteSpace(options.Export.DirectoryPath))
            Error("Export.DirectoryPath is required.");
        if (!TimeSpan.TryParseExact(options.Export.DailyAtLocalTime, @"hh\:mm", CultureInfo.InvariantCulture, out _))
            Error($"Export.DailyAtLocalTime must be HH:mm (got '{options.Export.DailyAtLocalTime}').");

        // Upload
        if (!KnownProviders.Contains(upload.Provider, StringComparer.OrdinalIgnoreCase))
            Error($"Unknown upload provider '{upload.Provider}' (expected None or GoogleDrive).");
        if (upload.Provider.Equals("GoogleDrive", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(upload.GoogleDrive.CredentialsPath))
                Error("Upload.GoogleDrive.CredentialsPath is required when the provider is GoogleDrive.");
            else if (!File.Exists(Path.GetFullPath(upload.GoogleDrive.CredentialsPath)))
                Warn($"Google Drive credentials file not found at '{upload.GoogleDrive.CredentialsPath}'.");
        }

        // Web UI
        if (options.WebUi.Port is < 1 or > 65535)
            Error($"WebUi.Port must be 1–65535 (got {options.WebUi.Port}).");

        return issues;
    }
}
