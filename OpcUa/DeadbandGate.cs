namespace PlcDataLogger.OpcUa;

/// <summary>
/// Client-side deadband filter: for a numeric tag with a configured absolute deadband, suppresses
/// a reading whose value is within the deadband of the last <b>stored</b> value, so noise-driven
/// changes don't inflate stored volume. This is applied in the logger rather than via an OPC UA
/// server-side <c>DataChangeFilter</c> because Codesys only accepts server-side deadband on
/// AnalogItem nodes; client-side works on any server and is what actually reduces storage.
///
/// Non-numeric values and non-Good quality always pass through, so state transitions (e.g. Booleans)
/// and quality events are never lost. Thread-safe.
/// </summary>
public sealed class DeadbandGate
{
    private readonly object _lock = new();
    private readonly Dictionary<int, double> _deadbandByTag = new();
    private readonly Dictionary<int, double> _lastStored = new();

    /// <summary>Replace the per-tag deadbands (only positive values take effect). The last-stored
    /// baselines are preserved so an in-flight stream keeps compressing across a re-subscribe.</summary>
    public void SetDeadbands(IEnumerable<(int TagId, double Deadband)> entries)
    {
        lock (_lock)
        {
            _deadbandByTag.Clear();
            foreach (var (tagId, deadband) in entries)
                if (deadband > 0)
                    _deadbandByTag[tagId] = deadband;
        }
    }

    /// <summary>Number of tags currently carrying a deadband.</summary>
    public int Count
    {
        get { lock (_lock) return _deadbandByTag.Count; }
    }

    /// <summary>True if a reading should be stored; false if it's within the deadband and skipped.</summary>
    public bool Pass(int tagId, double? value, bool good)
    {
        if (value is not double v || !good)
            return true;

        lock (_lock)
        {
            if (!_deadbandByTag.TryGetValue(tagId, out var deadband) || deadband <= 0)
                return true;

            if (_lastStored.TryGetValue(tagId, out var last) && Math.Abs(v - last) < deadband)
                return false;

            _lastStored[tagId] = v;
            return true;
        }
    }
}
