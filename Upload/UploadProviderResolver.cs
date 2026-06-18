using Microsoft.Extensions.Logging;
using PlcDataLogger.Configuration;

namespace PlcDataLogger.Upload;

/// <summary>
/// Holds the currently-active <see cref="ICloudUploadProvider"/>, rebuilt from
/// <see cref="ConfigStore"/> whenever the upload configuration changes — so switching provider
/// (e.g. None → GoogleDrive) takes effect without a service restart (§5, §9).
/// </summary>
public sealed class UploadProviderResolver
{
    private readonly ConfigStore _store;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _gate = new();

    private ICloudUploadProvider _current;
    private string _signature = "";

    public UploadProviderResolver(ConfigStore store, ILoggerFactory loggerFactory)
    {
        _store = store;
        _loggerFactory = loggerFactory;
        _current = Build(store.GetUpload(), out _signature);
        _store.Changed += Refresh;
    }

    public ICloudUploadProvider Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Remote destination folder for uploads (from current config).</summary>
    public string DestinationFolder => _store.GetUpload().DestinationFolder;

    private void Refresh()
    {
        var upload = _store.GetUpload();
        var signature = Signature(upload);
        lock (_gate)
        {
            if (signature == _signature)
                return;
            _current = Build(upload, out _signature);
        }
    }

    private ICloudUploadProvider Build(UploadOptions upload, out string signature)
    {
        signature = Signature(upload);
        if (upload.Provider.Equals("GoogleDrive", StringComparison.OrdinalIgnoreCase))
            return new GoogleDriveUploadProvider(upload.GoogleDrive, _loggerFactory.CreateLogger<GoogleDriveUploadProvider>());
        return new NoneUploadProvider();
    }

    private static string Signature(UploadOptions u) =>
        $"{u.Provider}|{u.GoogleDrive.CredentialsPath}|{u.GoogleDrive.TokenStorePath}";
}
