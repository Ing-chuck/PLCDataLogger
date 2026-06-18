using System.Globalization;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using PlcDataLogger.Configuration;
using PlcDataLogger.Health;
using PlcDataLogger.Storage;
using ISession = Opc.Ua.Client.ISession;
using SubscriptionOptions = PlcDataLogger.Configuration.SubscriptionOptions;

namespace PlcDataLogger.OpcUa;

/// <summary>
/// Owns a single OPC UA session to one PLC: connect, browse-based tag discovery
/// (§7, with continuation-point paging), report-by-exception subscriptions, and
/// independent reconnect with backoff (§5, §8). Read/subscribe only — no write path.
/// </summary>
public sealed class OpcUaPlcSession : IAsyncDisposable
{
    private const int ReconnectPeriodMs = 5000;
    private const int MaxBrowseDepth = 30;

    private readonly PlcOptions _plc;
    private readonly SubscriptionOptions _subOptions;
    private readonly TagFilter _filter;
    private readonly ApplicationConfiguration _appConfig;
    private readonly IReadingStore _db;
    private readonly ReadingBuffer _buffer;
    private readonly HealthMonitor _health;
    private readonly ILogger _log;

    private Session? _session;
    private Subscription? _subscription;
    private SessionReconnectHandler? _reconnectHandler;
    private int _plcId;

    public OpcUaPlcSession(
        PlcOptions plc,
        SubscriptionOptions subOptions,
        TagFilter filter,
        ApplicationConfiguration appConfig,
        IReadingStore db,
        ReadingBuffer buffer,
        HealthMonitor health,
        ILogger log)
    {
        _plc = plc;
        _subOptions = subOptions;
        _filter = filter;
        _appConfig = appConfig;
        _db = db;
        _buffer = buffer;
        _health = health;
        _log = log;
    }

    public string Name => _plc.Name;

    /// <summary>Connect, discover tags, and start the subscription.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _plcId = _db.UpsertPlc(_plc.Name, _plc.EndpointUrl, _plc.SecurityPolicy);

        var useSecurity = !string.Equals(_plc.SecurityPolicy, "None", StringComparison.OrdinalIgnoreCase);

        _log.LogInformation("[{Plc}] Connecting to {Endpoint} (security: {Sec})...",
            _plc.Name, _plc.EndpointUrl, _plc.SecurityPolicy);

        var selectedEndpoint = CoreClientUtils.SelectEndpoint(_appConfig, _plc.EndpointUrl, useSecurity);
        var endpointConfig = EndpointConfiguration.Create(_appConfig);
        var endpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfig);

        _session = await Session.Create(
            _appConfig,
            endpoint,
            updateBeforeConnect: false,
            sessionName: $"PlcDataLogger:{_plc.Name}",
            sessionTimeout: 60_000,
            identity: new UserIdentity(new AnonymousIdentityToken()),
            preferredLocales: null).ConfigureAwait(false);

        _session.KeepAlive += OnKeepAlive;
        _health.SetConnected(_plc.Name, _plc.EndpointUrl);
        _log.LogInformation("[{Plc}] Connected.", _plc.Name);

        await RunDiscoveryAndSubscribeAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Re-browse the address space and (re)build the subscription.</summary>
    public async Task RunDiscoveryAndSubscribeAsync(CancellationToken ct)
    {
        if (_session is null) return;

        var discovered = BrowseTags(_session, ct);
        _log.LogInformation("[{Plc}] Discovery found {Count} variable tags.", _plc.Name, discovered.Count);

        var bindings = _db.SyncTags(_plcId, discovered);
        var nameByNode = discovered
            .GroupBy(d => d.NodeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        await BuildSubscriptionAsync(bindings, nameByNode, ct).ConfigureAwait(false);
        _health.SetDiscovery(_plc.Name, discovered.Count, (int)(_subscription?.MonitoredItemCount ?? 0));
    }

    private async Task BuildSubscriptionAsync(
        IReadOnlyList<TagBinding> bindings,
        IReadOnlyDictionary<string, string> nameByNode,
        CancellationToken ct)
    {
        if (_session is null) return;

        // Drop any previous subscription before rebuilding (e.g. after a rescan).
        if (_subscription is not null)
        {
            _session.RemoveSubscription(_subscription);
            _subscription.Dispose();
            _subscription = null;
        }

        var subscription = new Subscription(_session.DefaultSubscription)
        {
            DisplayName = $"{_plc.Name}-sub",
            PublishingInterval = _subOptions.PublishingIntervalMs,
            PublishingEnabled = true,
        };

        _session.AddSubscription(subscription);
        subscription.Create();

        var items = new List<MonitoredItem>(bindings.Count);
        foreach (var binding in bindings)
        {
            var item = new MonitoredItem(subscription.DefaultItem)
            {
                StartNodeId = NodeId.Parse(binding.NodeId),
                AttributeId = Attributes.Value,
                DisplayName = nameByNode.GetValueOrDefault(binding.NodeId, binding.NodeId),
                SamplingInterval = _subOptions.SamplingIntervalMs,
                QueueSize = (uint)_subOptions.QueueSize,
                DiscardOldest = true,
                MonitoringMode = MonitoringMode.Reporting,
                Handle = binding.TagId,
            };
            item.Notification += OnNotification;
            items.Add(item);
        }

        subscription.AddItems(items);
        subscription.ApplyChanges();
        _subscription = subscription;

        _log.LogInformation("[{Plc}] Subscribed to {Count} monitored items.", _plc.Name, items.Count);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Recursively browse from the Objects folder, collecting every Variable node and
    /// recursing into Objects. Handles continuation points so large symbol tables aren't
    /// silently truncated (§7).
    /// </summary>
    private List<DiscoveredTag> BrowseTags(Session session, CancellationToken ct)
    {
        var tags = new List<DiscoveredTag>();
        var visited = new HashSet<string>();
        var seenTags = new HashSet<string>();
        BrowseNode(session, ObjectIds.ObjectsFolder, tags, visited, seenTags, depth: 0, ct);
        return tags;
    }

    private void BrowseNode(
        Session session,
        NodeId nodeId,
        List<DiscoveredTag> tags,
        HashSet<string> visited,
        HashSet<string> seenTags,
        int depth,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested || depth > MaxBrowseDepth)
            return;
        if (!visited.Add(nodeId.ToString()))
            return;

        const uint nodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable);

        session.Browse(
            requestHeader: null,
            view: null,
            nodeToBrowse: nodeId,
            maxResultsToReturn: 0,
            browseDirection: BrowseDirection.Forward,
            referenceTypeId: ReferenceTypeIds.HierarchicalReferences,
            includeSubtypes: true,
            nodeClassMask: nodeClassMask,
            out var continuationPoint,
            out var references);

        var children = new List<(NodeId Id, NodeClass Class, string Name)>();
        Collect(references, session, children, tags, seenTags);

        while (continuationPoint != null && continuationPoint.Length > 0 && !ct.IsCancellationRequested)
        {
            var cps = new ByteStringCollection { continuationPoint };
            session.BrowseNext(null, releaseContinuationPoints: false, cps,
                out BrowseResultCollection results, out DiagnosticInfoCollection _);

            var result = (results != null && results.Count > 0) ? results[0] : null;
            if (result != null)
            {
                Collect(result.References, session, children, tags, seenTags);
                continuationPoint = result.ContinuationPoint;
            }
            else
            {
                continuationPoint = null;
            }
        }

        foreach (var child in children)
        {
            if ((child.Class == NodeClass.Object || child.Class == NodeClass.Variable)
                && TagFilter.ShouldRecurse(child.Id))
                BrowseNode(session, child.Id, tags, visited, seenTags, depth + 1, ct);
        }
    }

    private void Collect(
        ReferenceDescriptionCollection references,
        Session session,
        List<(NodeId, NodeClass, string)> children,
        List<DiscoveredTag> tags,
        HashSet<string> seenTags)
    {
        if (references is null) return;

        foreach (var reference in references)
        {
            var nodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            if (nodeId is null)
                continue;

            var name = reference.DisplayName?.Text ?? reference.BrowseName?.Name ?? nodeId.ToString();
            children.Add((nodeId, reference.NodeClass, name));

            if (reference.NodeClass == NodeClass.Variable
                && _filter.IncludeVariable(nodeId)
                && seenTags.Add(nodeId.ToString()))
                tags.Add(new DiscoveredTag(nodeId.ToString(), name));
        }
    }

    private void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        if (item.Handle is not int tagId)
            return;

        foreach (var value in item.DequeueValues())
        {
            var reading = ToReading(tagId, value);
            if (!_buffer.TryWrite(reading))
                _log.LogWarning("[{Plc}] Buffer rejected a reading for tag {TagId}.", _plc.Name, tagId);
        }
        _health.MarkSample(_plc.Name);
    }

    private static Reading ToReading(int tagId, DataValue value)
    {
        double? numeric = null;
        string? text = null;
        var raw = value.Value;
        var typeName = raw?.GetType().Name ?? "Null";

        switch (raw)
        {
            case null:
                break;
            case bool b:
                numeric = b ? 1d : 0d;
                break;
            case string s:
                text = s;
                break;
            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                numeric = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                break;
            default:
                text = raw.ToString();
                break;
        }

        var source = value.SourceTimestamp != DateTime.MinValue ? value.SourceTimestamp : DateTime.UtcNow;

        return new Reading(
            TagId: tagId,
            TsSourceUtc: source.ToUniversalTime(),
            TsReceivedUtc: DateTime.UtcNow,
            Value: numeric,
            ValueText: text,
            Quality: Classify(value.StatusCode),
            DataTypeName: typeName);
    }

    private static string Classify(StatusCode status)
    {
        if (StatusCode.IsGood(status)) return "Good";
        if (StatusCode.IsBad(status)) return "Bad";
        return "Uncertain";
    }

    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (!ServiceResult.IsBad(e.Status))
            return;

        if (_reconnectHandler is null)
        {
            _log.LogWarning("[{Plc}] Keep-alive failed ({Status}); starting reconnect.", _plc.Name, e.Status);
            _health.SetDisconnected(_plc.Name, $"Keep-alive failed: {e.Status}");
            _reconnectHandler = new SessionReconnectHandler(reconnectAbort: true);
            _reconnectHandler.BeginReconnect(_session, ReconnectPeriodMs, OnReconnectComplete);
        }
    }

    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _reconnectHandler))
            return;

        if (_reconnectHandler?.Session is Session reconnected)
            _session = reconnected;

        _reconnectHandler?.Dispose();
        _reconnectHandler = null;
        _health.SetConnected(_plc.Name, _plc.EndpointUrl);
        _log.LogInformation("[{Plc}] Reconnected.", _plc.Name);
    }

    public async ValueTask DisposeAsync()
    {
        _reconnectHandler?.Dispose();

        if (_subscription is not null)
        {
            try { _subscription.Delete(silent: true); } catch { /* best effort */ }
            _subscription.Dispose();
        }

        if (_session is not null)
        {
            _session.KeepAlive -= OnKeepAlive;
            try { await _session.CloseAsync().ConfigureAwait(false); } catch { /* best effort */ }
            _session.Dispose();
        }
    }
}
