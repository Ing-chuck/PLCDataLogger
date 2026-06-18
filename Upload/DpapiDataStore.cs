using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace PlcDataLogger.Upload;

/// <summary>
/// Google Auth <see cref="IDataStore"/> that persists the OAuth token to disk encrypted with
/// Windows DPAPI (§11). Uses LocalMachine scope so the token authorized interactively during
/// setup can still be read by the (possibly different) service account at runtime.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiDataStore : IDataStore
{
    // App-specific entropy mixed into DPAPI so the blob isn't trivially decryptable by other apps.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PlcDataLogger.GoogleDrive.v1");

    private readonly string _directory;

    public DpapiDataStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public Task StoreAsync<T>(string key, T value)
    {
        var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(PathFor(key), encrypted);
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
            return Task.FromResult<T>(default!);

        var decrypted = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.LocalMachine);
        var json = Encoding.UTF8.GetString(decrypted);
        return Task.FromResult(NewtonsoftJsonSerializer.Instance.Deserialize<T>(json));
    }

    public Task DeleteAsync<T>(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        if (Directory.Exists(_directory))
            foreach (var file in Directory.EnumerateFiles(_directory, "*.dpapi"))
                File.Delete(file);
        return Task.CompletedTask;
    }

    public bool HasToken() =>
        Directory.Exists(_directory) && Directory.EnumerateFiles(_directory, "*.dpapi").Any();

    private string PathFor(string key)
    {
        var safe = string.Concat(key.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c));
        return Path.Combine(_directory, safe + ".dpapi");
    }
}
