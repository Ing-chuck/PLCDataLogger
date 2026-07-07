using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PlcDataLogger.Configuration;

/// <summary>
/// Logs configuration validation issues once at startup so misconfiguration is obvious in the logs
/// at a site. Non-fatal by design (§13): the service still starts and the web UI stays reachable so
/// the problem can be corrected on site.
/// </summary>
public sealed class StartupValidationService : IHostedService
{
    private readonly LoggerOptions _options;
    private readonly ConfigStore _configStore;
    private readonly ILogger<StartupValidationService> _log;

    public StartupValidationService(
        IOptions<LoggerOptions> options,
        ConfigStore configStore,
        ILogger<StartupValidationService> log)
    {
        _options = options.Value;
        _configStore = configStore;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var issues = ConfigValidation.Validate(_options, _configStore.GetSiteName(), _configStore.GetPlcs(), _configStore.GetUpload());

        var errors = issues.Count(i => i.IsError);
        var warnings = issues.Count - errors;

        if (issues.Count == 0)
        {
            _log.LogInformation("Configuration validated: no issues.");
            return Task.CompletedTask;
        }

        _log.LogWarning("Configuration validation found {Errors} error(s) and {Warnings} warning(s):",
            errors, warnings);
        foreach (var issue in issues)
        {
            if (issue.IsError)
                _log.LogError("  [config] {Message}", issue.Message);
            else
                _log.LogWarning("  [config] {Message}", issue.Message);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
