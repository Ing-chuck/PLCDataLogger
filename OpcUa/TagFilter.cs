using Opc.Ua;
using PlcDataLogger.Configuration;

namespace PlcDataLogger.OpcUa;

/// <summary>
/// Decides which browsed nodes are worth logging / descending into, based on
/// <see cref="TagFilterOptions"/>. Keeps discovery focused on the Codesys application
/// tags (ns=4;s=|var|{PLC}.Application.*) and away from server-diagnostics noise.
/// </summary>
public sealed class TagFilter
{
    private readonly TagFilterOptions _options;

    public TagFilter(TagFilterOptions options) => _options = options;

    /// <summary>True if a Variable node should be logged as a tag.</summary>
    public bool IncludeVariable(NodeId nodeId)
    {
        if (_options.ExcludeNamespaceZero && nodeId.NamespaceIndex == 0)
            return false;

        if (_options.IncludeNamespaceIndices.Count > 0 &&
            !_options.IncludeNamespaceIndices.Contains(nodeId.NamespaceIndex))
            return false;

        if (_options.NodeIdMustContain.Count > 0)
        {
            var identifier = nodeId.Identifier?.ToString();
            if (identifier is null)
                return false;

            foreach (var token in _options.NodeIdMustContain)
            {
                if (identifier.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }
        }

        return true;
    }

    /// <summary>True if we should browse into a node's children. We always avoid the
    /// standard Server object subtree (huge, pure diagnostics), but otherwise descend
    /// broadly so the path to application tags is never pruned.</summary>
    public static bool ShouldRecurse(NodeId nodeId) => !Equals(nodeId, ObjectIds.Server);
}
