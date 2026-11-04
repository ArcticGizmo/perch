namespace Perch.Data.GitGraph;

/// <summary>
/// Tags grouped by name into a tree, so a long list (<c>spa/v0.2.40</c>, <c>spa/v0.2.39</c>, <c>api/v1.0.0</c>…) can be
/// opened, closed and hidden a group at a time. Names split at <c>/ . - _</c>; a group holding only one thing is
/// replaced by that thing, so there are no chains of single-child groups and no group of one tag.
/// </summary>
internal static class TagTree
{
    private static readonly char[] Delimiters = ['/', '.', '-', '_'];

    /// <summary>A group (<see cref="Tag"/> null, labelled with the shared prefix) or one tag (labelled with its name).</summary>
    public sealed record Node(string Label, GraphRef? Tag, IReadOnlyList<Node> Children)
    {
        public bool IsGroup => Tag is null;

        /// <summary>Every tag at or under this node.</summary>
        public IEnumerable<GraphRef> Tags => Tag is not null ? [Tag] : Children.SelectMany(c => c.Tags);
    }

    private sealed class Trie(string prefix, string segment)
    {
        public readonly string Prefix = prefix, Segment = segment;
        public readonly Dictionary<string, Trie> Kids = new(StringComparer.Ordinal);
        public readonly List<GraphRef> Leaves = [];
    }

    /// <summary>The top level of the tree. Within a level, text sorts A→Z and numbers newest-first (high to low), so
    /// version tags read latest at the top.</summary>
    public static List<Node> Build(IEnumerable<GraphRef> tags)
    {
        var root = new Trie("", "");
        foreach (var t in tags)
        {
            var node = root;
            int start = 0;
            for (int i; (i = t.Name.IndexOfAny(Delimiters, start)) > 0; start = i + 1)
            {
                var seg = t.Name[start..i];
                if (!node.Kids.TryGetValue(seg, out var kid))
                    node.Kids[seg] = kid = new Trie(t.Name[..i], seg);
                node = kid;
            }
            node.Leaves.Add(t);
        }
        return Level(root);
    }

    private static List<Node> Level(Trie t)
    {
        var items = new List<(string Key, Node Node)>();
        foreach (var kid in t.Kids.Values)
        {
            var children = Level(kid);
            items.Add((kid.Segment, children.Count == 1 ? children[0] : new Node(kid.Prefix, null, children)));
        }
        foreach (var leaf in t.Leaves)
            items.Add((LastSegment(leaf.Name), new Node(leaf.Name, leaf, [])));
        items.Sort((a, b) => Natural(a.Key, b.Key));
        return items.Select(i => i.Node).ToList();
    }

    private static string LastSegment(string name) =>
        name.LastIndexOfAny(Delimiters) is var i and >= 0 ? name[(i + 1)..] : name;

    /// <summary>Compares run by run: digit runs by value, highest first; everything else A→Z ignoring case.</summary>
    internal static int Natural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            bool da = char.IsAsciiDigit(a[i]), db = char.IsAsciiDigit(b[j]);
            int ei = i, ej = j;
            while (ei < a.Length && char.IsAsciiDigit(a[ei]) == da) ei++;
            while (ej < b.Length && char.IsAsciiDigit(b[ej]) == db) ej++;
            var (ra, rb) = (a[i..ei], b[j..ej]);
            int c = da && db
                ? CompareNumbers(rb, ra)
                : string.Compare(ra, rb, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            (i, j) = (ei, ej);
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    // Digit strings of any length, without overflowing: fewer significant digits is smaller.
    private static int CompareNumbers(string x, string y)
    {
        x = x.TrimStart('0');
        y = y.TrimStart('0');
        return x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
    }
}
