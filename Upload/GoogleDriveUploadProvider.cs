using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using PlcDataLogger.Configuration;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace PlcDataLogger.Upload;

/// <summary>
/// Uploads export files to Google Drive (§9). Authentication uses the OAuth installed-app
/// flow; the refresh token is cached encrypted via <see cref="DpapiDataStore"/>. The running
/// service only ever refreshes a previously-stored token silently — the one-time interactive
/// consent is performed by <see cref="AuthorizeInteractiveAsync"/> (wired into the Phase 4 setup UI).
///
/// Each PLC has one rolling export file that is re-uploaded every cycle; the upload overwrites the
/// existing same-named Drive file (via <c>Files.Update</c>) instead of creating dated duplicates.
/// The default provider is "None"; enable via Upload:Provider = "GoogleDrive".
/// </summary>
public sealed class GoogleDriveUploadProvider : ICloudUploadProvider
{
    private static readonly string[] Scopes = { DriveService.Scope.DriveFile };
    private const string UserId = "user";

    private readonly GoogleDriveOptions _options;
    private readonly ILogger<GoogleDriveUploadProvider> _log;
    private readonly DpapiDataStore _tokenStore;

    private DriveService? _service;

    public GoogleDriveUploadProvider(GoogleDriveOptions options, ILogger<GoogleDriveUploadProvider> log)
    {
        _options = options;
        _log = log;
        _tokenStore = new DpapiDataStore(_options.TokenStorePath);
    }

    public string ProviderName => "GoogleDrive";

    public Task<bool> IsConfiguredAsync(CancellationToken ct = default) =>
        Task.FromResult(File.Exists(_options.CredentialsPath) && _tokenStore.HasToken());

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var service = await EnsureServiceAsync(ct).ConfigureAwait(false);
            var about = service.About.Get();
            about.Fields = "user";
            await about.ExecuteAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Google Drive connection test failed.");
            return false;
        }
    }

    public async Task UploadAsync(string localFilePath, string destinationPath, CancellationToken ct = default)
    {
        var service = await EnsureServiceAsync(ct).ConfigureAwait(false);
        var folderId = await EnsureFolderAsync(service, destinationPath, ct).ConfigureAwait(false);
        var fileName = Path.GetFileName(localFilePath);

        // The logger keeps one rolling file per PLC and re-uploads it each cycle, so overwrite the
        // existing Drive file (update its content) instead of piling up dated duplicates.
        var existingId = await FindFileIdAsync(service, folderId, fileName, ct).ConfigureAwait(false);
        var contentType = ContentTypeFor(fileName);

        await using var fs = File.OpenRead(localFilePath);
        Google.Apis.Upload.IUploadProgress progress;
        if (existingId is null)
        {
            _log.LogInformation("Creating Drive file {Name}.", fileName);
            var metadata = new DriveFile
            {
                Name = fileName,
                Parents = folderId is null ? null : new[] { folderId },
            };
            var create = service.Files.Create(metadata, fs, contentType);
            create.Fields = "id";
            progress = await create.UploadAsync(ct).ConfigureAwait(false);
        }
        else
        {
            _log.LogInformation("Overwriting existing Drive file {Name} ({Id}).", fileName, existingId);
            // Update content only — don't resend Parents/Name (Drive rejects re-parenting here).
            var update = service.Files.Update(new DriveFile(), existingId, fs, contentType);
            update.Fields = "id";
            progress = await update.UploadAsync(ct).ConfigureAwait(false);
        }

        if (progress.Status != Google.Apis.Upload.UploadStatus.Completed)
            throw progress.Exception ?? new InvalidOperationException("Google Drive upload did not complete.");
    }

    private static string ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".csv" => "text/csv",
            ".db" or ".sqlite" => "application/x-sqlite3",
            _ => "application/octet-stream",
        };

    private static async Task<string?> FindFileIdAsync(DriveService service, string? folderId, string fileName, CancellationToken ct)
    {
        var list = service.Files.List();
        var escapedName = fileName.Replace("\\", "\\\\").Replace("'", "\\'");
        list.Q = folderId is null
            ? $"name = '{escapedName}' and trashed = false"
            : $"'{folderId}' in parents and name = '{escapedName}' and trashed = false";
        list.Fields = "files(id)";
        list.PageSize = 1;
        var existing = await list.ExecuteAsync(ct).ConfigureAwait(false);
        return existing.Files is { Count: > 0 } ? existing.Files[0].Id : null;
    }

    /// <summary>One-time interactive consent (opens a browser). Intended for the setup UI, not
    /// the unattended service.</summary>
    public async Task AuthorizeInteractiveAsync(CancellationToken ct = default)
    {
        await BuildCredentialAsync(ct).ConfigureAwait(false);
    }

    private async Task<DriveService> EnsureServiceAsync(CancellationToken ct)
    {
        if (_service is not null)
            return _service;

        if (!_tokenStore.HasToken())
            throw new InvalidOperationException(
                "Google Drive is not authorized yet. Complete the one-time consent flow first.");

        var credential = await BuildCredentialAsync(ct).ConfigureAwait(false);
        _service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "PLC Data Logger",
        });
        return _service;
    }

    private async Task<UserCredential> BuildCredentialAsync(CancellationToken ct)
    {
        await using var stream = File.OpenRead(_options.CredentialsPath);
        var secrets = (await GoogleClientSecrets.FromStreamAsync(stream, ct).ConfigureAwait(false)).Secrets;

        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets, Scopes, UserId, ct, _tokenStore).ConfigureAwait(false);
    }

    private static async Task<string?> EnsureFolderAsync(DriveService service, string folderName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return null;

        var list = service.Files.List();
        list.Q = $"mimeType = 'application/vnd.google-apps.folder' and name = '{folderName.Replace("'", "\\'")}' and trashed = false";
        list.Fields = "files(id)";
        list.PageSize = 1;
        var existing = await list.ExecuteAsync(ct).ConfigureAwait(false);
        if (existing.Files is { Count: > 0 })
            return existing.Files[0].Id;

        var metadata = new DriveFile { Name = folderName, MimeType = "application/vnd.google-apps.folder" };
        var create = service.Files.Create(metadata);
        create.Fields = "id";
        var created = await create.ExecuteAsync(ct).ConfigureAwait(false);
        return created.Id;
    }
}
