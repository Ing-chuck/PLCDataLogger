namespace PlcDataLogger.Components;

/// <summary>
/// A node in the tag-selection tree. Interior nodes are "folders" (subtree segments such as
/// <c>GLOBALES</c> or <c>fb_BREC1</c>); leaf nodes are individual tags with a <see cref="TagId"/>.
/// </summary>
public sealed class TagNode
{
    public string Name { get; init; } = "";

    /// <summary>Set on leaf nodes only.</summary>
    public int? TagId { get; init; }

    public bool Expanded { get; set; } = true;

    /// <summary>Folders first then leaves (sorted) for rendering.</summary>
    public List<TagNode> Children { get; } = new();

    /// <summary>Every descendant leaf tag id (a leaf contains just itself). Drives folder
    /// checked/indeterminate state and select-all.</summary>
    public List<int> LeafIds { get; } = new();

    public bool IsLeaf => TagId.HasValue;

    // Build-time only: fast lookup of child folders by segment name.
    private Dictionary<string, TagNode>? _folders;

    /// <summary>Get or create the child folder named <paramref name="name"/>.</summary>
    public TagNode Folder(string name)
    {
        _folders ??= new(StringComparer.Ordinal);
        if (!_folders.TryGetValue(name, out var child))
        {
            child = new TagNode { Name = name };
            _folders[name] = child;
            Children.Add(child);
        }
        return child;
    }

    /// <summary>Build a tree from tag selections. Returns the root and the total leaf count.</summary>
    public static (TagNode Root, int Leaves) Build(IEnumerable<Storage.TagSelection> tags)
    {
        var root = new TagNode();
        foreach (var t in tags)
        {
            var parts = t.TagName.Split('.');
            var node = root;
            for (var i = 0; i < parts.Length - 1; i++)
                node = node.Folder(parts[i]);
            node.Children.Add(new TagNode { Name = parts[^1], TagId = t.TagId });
        }
        Finalize(root);
        return (root, root.LeafIds.Count);
    }

    private static void Finalize(TagNode node)
    {
        node.Children.Sort(static (a, b) =>
            a.IsLeaf != b.IsLeaf ? (a.IsLeaf ? 1 : -1) : string.CompareOrdinal(a.Name, b.Name));

        if (node.IsLeaf)
        {
            node.LeafIds.Add(node.TagId!.Value);
            return;
        }
        foreach (var child in node.Children)
        {
            Finalize(child);
            node.LeafIds.AddRange(child.LeafIds);
        }
    }

    public void SetExpandedRecursive(bool expanded)
    {
        Expanded = expanded;
        foreach (var child in Children)
            child.SetExpandedRecursive(expanded);
    }
}
