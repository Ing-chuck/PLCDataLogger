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
/// NOTE: implemented but not yet end-to-end tested — requires a Google Cloud OAuth client and a
/// completed consent. The default provider is "None"; enable via Upload:Provider = "GoogleDrive".
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

        var metadata = new DriveFile
        {
            Name = Path.GetFileName(localFilePath),
            Parents = folderId is null ? null : new[] { folderId },
        };

        await using var fs = File.OpenRead(localFilePath);
        var request = service.Files.Create(metadata, fs, "text/csv");
        request.Fields = "id";
        var progress = await request.UploadAsync(ct).ConfigureAwait(false);

        if (progress.Status != Google.Apis.Upload.UploadStatus.Completed)
            throw progress.Exception ?? new InvalidOperationException("Google Drive upload did not complete.");
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
