using Opc.Ua;
using Opc.Ua.Configuration;

namespace PlcDataLogger.OpcUa;

/// <summary>
/// Builds the OPC UA <see cref="ApplicationConfiguration"/> shared by all PLC sessions
/// and ensures the client application instance certificate exists. Read/subscribe use
/// only — the client never issues write service calls (§11).
///
/// NOTE: AutoAcceptUntrustedCertificates is enabled for commissioning (security None).
/// For production secured policies this must be replaced with explicit certificate
/// trust handled from the config UI (architecture §11).
/// </summary>
public static class OpcUaApplication
{
    public static async Task<ApplicationConfiguration> CreateAsync()
    {
        var application = new ApplicationInstance
        {
            ApplicationName = "PlcDataLogger",
            ApplicationType = ApplicationType.Client,
        };

        var pkiRoot = Path.GetFullPath("pki");

        var config = await application
            .Build(
                applicationUri: $"urn:{Utils.GetHostName()}:PlcDataLogger",
                productUri: "urn:plcdatalogger")
            .AsClient()
            .AddSecurityConfiguration(
                subjectName: $"CN=PlcDataLogger, DC={Utils.GetHostName()}",
                pkiRoot: pkiRoot)
            .SetAutoAcceptUntrustedCertificates(true)
            .Create()
            .ConfigureAwait(false);

        // Belt-and-suspenders for commissioning: accept untrusted server certs.
        config.CertificateValidator.CertificateValidation += (_, e) =>
        {
            if (e.Error.StatusCode == Opc.Ua.StatusCodes.BadCertificateUntrusted)
                e.Accept = true;
        };

        await application.CheckApplicationInstanceCertificate(silent: true, minimumKeySize: 0)
            .ConfigureAwait(false);

        return config;
    }
}
