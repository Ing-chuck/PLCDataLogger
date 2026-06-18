namespace PlcDataLogger.Upload;

/// <summary>
/// The default provider for sites with no internet access (§9). It does nothing and always
/// reports "not configured" — this is a fully supported, permanent end state, not a degraded
/// mode. CSV exports are still produced locally for manual pickup (USB/RDP).
/// </summary>
public sealed class NoneUploadProvider : ICloudUploadProvider
{
    public string ProviderName => "None";

    public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task UploadAsync(string localFilePath, string destinationPath, CancellationToken ct = default) =>
        Task.CompletedTask;
}
