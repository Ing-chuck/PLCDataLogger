using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PlcDataLogger.Configuration;

/// <summary>How often the scheduled export+upload cycle runs (§5).</summary>
public sealed class ScheduleConfig
{
    /// <summary>"Interval" (every <see cref="IntervalMinutes"/>) or "Daily" (once at <see cref="DailyAtLocalTime"/>).</summary>
    public string Mode { get; set; } = "Daily";

    /// <summary>Minutes between runs when <see cref="Mode"/> is "Interval".</summary>
    public int IntervalMinutes { get; set; } = 360;

    /// <summary>Local time of day ("HH:mm") to run when <see cref="Mode"/> is "Daily".</summary>
    public string DailyAtLocalTime { get; set; } = "02:00";
}

/// <summary>The subset of configuration that is editable at runtime via the web UI.</summary>
public sealed class EditableConfig
{
    public string SiteName { get; set; } = "";
    public ScheduleConfig Schedule { get; set; } = new();

    /// <summary>Days of readings to keep locally. 0 disables pruning (keep everything).</summary>
    public int RetentionDays { get; set; } = 90;

    public List<PlcOptions> Plcs { get; set; } = new();
    public UploadOptions Upload { get; set; } = new();
}

/// <summary>
/// Runtime-editable configuration store (§5 Configuration Manager). Seeds from appsettings on
/// first run, then persists edits to <c>config.local.json</c> next to the executable and becomes
/// the source of truth for PLC connections and upload settings. Raises <see cref="Changed"/> so
/// the OPC UA client manager and upload component can react without a service restart.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly ILogger<ConfigStore> _log;
    private EditableConfig _config;

    /// <summary>Raised after the configuration changes (already persisted).</summary>
    public event Action? Changed;

    public ConfigStore(IOptions<LoggerOptions> seed, ILogger<ConfigStore> log)
    {
        _log = log;
        _path = Path.GetFullPath("config.local.json");

        if (File.Exists(_path))
        {
            _config = Load();
            // Backfill fields for configs persisted before they became runtime-editable.
            var backfilled = false;
            if (string.IsNullOrWhiteSpace(_config.SiteName))
            {
                _config.SiteName = seed.Value.SiteName;
                backfilled = true;
            }
            if (_config.Schedule is null || string.IsNullOrWhiteSpace(_config.Schedule.DailyAtLocalTime))
            {
                _config.Schedule = new ScheduleConfig { Mode = "Daily", DailyAtLocalTime = seed.Value.Export.DailyAtLocalTime };
                backfilled = true;
            }
            if (_config.RetentionDays <= 0)
            {
                _config.RetentionDays = seed.Value.Storage.RetentionDays;
                backfilled = true;
            }
            if (backfilled) Persist();
            _log.LogInformation("Loaded runtime configuration from {Path}.", _path);
        }
        else
        {
            _config = new EditableConfig
            {
                SiteName = seed.Value.SiteName,
                Schedule = new ScheduleConfig { Mode = "Daily", DailyAtLocalTime = seed.Value.Export.DailyAtLocalTime },
                RetentionDays = seed.Value.Storage.RetentionDays,
                Plcs = seed.Value.Plcs.Select(Clone).ToList(),
                Upload = Clone(seed.Value.Upload),
            };
            Persist();
            _log.LogInformation("Seeded runtime configuration from appsettings into {Path}.", _path);
        }
    }

    public ScheduleConfig GetSchedule()
    {
        lock (_gate) return Clone(_config.Schedule);
    }

    public void SetSchedule(ScheduleConfig schedule)
    {
        lock (_gate)
        {
            _config.Schedule = Clone(schedule);
            Persist();
        }
        Changed?.Invoke();
    }

    public int GetRetentionDays()
    {
        lock (_gate) return _config.RetentionDays;
    }

    public void SetRetentionDays(int days)
    {
        lock (_gate)
        {
            _config.RetentionDays = Math.Max(0, days);
            Persist();
        }
        Changed?.Invoke();
    }

    public string GetSiteName()
    {
        lock (_gate) return _config.SiteName;
    }

    public void SetSiteName(string siteName)
    {
        lock (_gate)
        {
            _config.SiteName = siteName?.Trim() ?? "";
            Persist();
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<PlcOptions> GetPlcs()
    {
        lock (_gate) return _config.Plcs.Select(Clone).ToList();
    }

    public UploadOptions GetUpload()
    {
        lock (_gate) return Clone(_config.Upload);
    }

    /// <summary>Add a PLC, or replace the existing one with the same name (case-insensitive).</summary>
    public void UpsertPlc(PlcOptions plc)
    {
        lock (_gate)
        {
            _config.Plcs.RemoveAll(p => p.Name.Equals(plc.Name, StringComparison.OrdinalIgnoreCase));
            _config.Plcs.Add(Clone(plc));
            Persist();
        }
        Changed?.Invoke();
    }

    public void RemovePlc(string name)
    {
        lock (_gate)
        {
            _config.Plcs.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Persist();
        }
        Changed?.Invoke();
    }

    public void SetUpload(UploadOptions upload)
    {
        lock (_gate)
        {
            _config.Upload = Clone(upload);
            Persist();
        }
        Changed?.Invoke();
    }

    private EditableConfig Load()
    {
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<EditableConfig>(json, JsonOptions) ?? new EditableConfig();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read {Path}; starting from empty config.", _path);
            return new EditableConfig();
        }
    }

    private void Persist()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_config, JsonOptions));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist configuration to {Path}.", _path);
        }
    }

    private static ScheduleConfig Clone(ScheduleConfig s) => new()
    {
        Mode = s.Mode,
        IntervalMinutes = s.IntervalMinutes,
        DailyAtLocalTime = s.DailyAtLocalTime,
    };

    private static PlcOptions Clone(PlcOptions p) => new()
    {
        Name = p.Name,
        EndpointUrl = p.EndpointUrl,
        SecurityPolicy = p.SecurityPolicy,
    };

    private static UploadOptions Clone(UploadOptions u) => new()
    {
        Provider = u.Provider,
        DestinationFolder = u.DestinationFolder,
        GoogleDrive = new GoogleDriveOptions
        {
            CredentialsPath = u.GoogleDrive.CredentialsPath,
            TokenStorePath = u.GoogleDrive.TokenStorePath,
        },
    };
}
