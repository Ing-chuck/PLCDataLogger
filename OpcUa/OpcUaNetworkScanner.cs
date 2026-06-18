using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace PlcDataLogger.OpcUa;

/// <summary>An OPC UA server found on the network during a setup scan.</summary>
public sealed record DiscoveredServer(string EndpointUrl, string? ApplicationName, string? ApplicationUri);

/// <summary>
/// Scans the local network for OPC UA servers to ease setup (user-requested). Sweeps the host's
/// IPv4 /24 subnet(s) for an open OPC UA port, then best-effort enriches each hit with the
/// server's application name via the OPC UA FindServers discovery service.
/// </summary>
public sealed class OpcUaNetworkScanner
{
    private const int MaxParallelism = 64;

    private readonly ILogger<OpcUaNetworkScanner> _log;

    public OpcUaNetworkScanner(ILogger<OpcUaNetworkScanner> log) => _log = log;

    public async Task<IReadOnlyList<DiscoveredServer>> ScanAsync(
        int port = 4840, int connectTimeoutMs = 400, CancellationToken ct = default)
    {
        var hosts = EnumerateSubnetHosts().ToList();
        _log.LogInformation("Network scan: probing {Count} addresses on port {Port}.", hosts.Count, port);

        var found = new System.Collections.Concurrent.ConcurrentBag<DiscoveredServer>();

        await Parallel.ForEachAsync(
            hosts,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = ct },
            async (ip, token) =>
            {
                if (!await IsPortOpenAsync(ip, port, connectTimeoutMs, token).ConfigureAwait(false))
                    return;

                var url = $"opc.tcp://{ip}:{port}";
                found.Add(TryFindServer(url) ?? new DiscoveredServer(url, null, null));
            }).ConfigureAwait(false);

        var results = found.OrderBy(s => s.EndpointUrl, StringComparer.OrdinalIgnoreCase).ToList();
        _log.LogInformation("Network scan complete: {Count} OPC UA server(s) found.", results.Count);
        return results;
    }

    private static async Task<bool> IsPortOpenAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(ip, port, ct).AsTask();
            var completed = await Task.WhenAny(connect, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
            return completed == connect && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private DiscoveredServer? TryFindServer(string url)
    {
        try
        {
            var config = EndpointConfiguration.Create();
            config.OperationTimeout = 3000;
            using var client = DiscoveryClient.Create(new Uri(url), config);
            var servers = client.FindServers(null);
            var server = servers?.FirstOrDefault();
            if (server is null)
                return new DiscoveredServer(url, null, null);

            return new DiscoveredServer(url, server.ApplicationName?.Text, server.ApplicationUri);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "FindServers failed for {Url}; returning bare endpoint.", url);
            return new DiscoveredServer(url, null, null);
        }
    }

    /// <summary>Enumerate candidate host addresses on each up, non-loopback IPv4 /24 subnet.</summary>
    private static IEnumerable<IPAddress> EnumerateSubnetHosts()
    {
        var seen = new HashSet<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                var ipBytes = addr.Address.GetAddressBytes();
                var maskBytes = addr.IPv4Mask?.GetAddressBytes();

                // Only handle /24-or-narrower to keep the scan bounded; treat anything else as /24.
                if (maskBytes is null || maskBytes[0] != 255 || maskBytes[1] != 255 || maskBytes[2] != 255)
                    maskBytes = new byte[] { 255, 255, 255, 0 };

                for (var host = 1; host <= 254; host++)
                {
                    var candidate = new byte[] { ipBytes[0], ipBytes[1], ipBytes[2], (byte)host };
                    var ip = new IPAddress(candidate);
                    if (seen.Add(ip.ToString()))
                        yield return ip;
                }
            }
        }
    }
}
