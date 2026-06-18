namespace PlcDataLogger.Upload;

/// <summary>
/// Pluggable cloud upload destination (§9). The export job runs regardless of which
/// provider is active; only this interface knows where files actually go, so swapping
/// destinations (Google Drive, S3, SFTP, …) never touches the rest of the system.
/// </summary>
public interface ICloudUploadProvider
{
    string ProviderName { get; }

    /// <summary>True if the provider has everything it needs to upload (credentials, token).
    /// "Not configured" is an expected, neutral state for offline sites — not an error.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);

    /// <summary>Verify the destination is reachable/authorized.</summary>
    Task<bool> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>Upload a local file to <paramref name="destinationPath"/> within the provider.</summary>
    Task UploadAsync(string localFilePath, string destinationPath, CancellationToken ct = default);
}
