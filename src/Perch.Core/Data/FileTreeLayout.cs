namespace Perch.Data;

/// <summary>
/// Lays a flat list of repo-relative paths out as a folder tree, one row per visible folder or file, ready for a
/// plain list control to show with indentation. Folders sort before files, each alphabetically. A folder whose only
/// child is another folder is folded into it ("src/app/views"), as VS Code's compact folders do, so a deep path
/// with nothing beside it doesn't cost a row and an indent per segment.
/// </summary>
public static class FileTreeLayout
{
    /// <summary>One row. A folder's <see cref="Key"/> is its full path ("a/b/c") and <see cref="FileIndex"/> is -1;
    /// a file's <see cref="Key"/> is its path as given and <see cref="FileIndex"/> its index in the input.
    /// <see cref="Files"/> counts the files under a folder (1 for a file).</summary>
    public sealed record Entry(bool IsFolder, string Key, string Label, int Depth, int FileIndex, int Files, bool Expanded);

    /// <summary>Builds the rows for <paramref name="paths"/>; a folder whose key is in <paramref name="collapsed"/>
    /// shows as one row with nothing under it. A path ending in "/" (git's collapsed untracked directory) is a file
    /// row labelled with that slash.</summary>
    public static IReadOnlyList<Entry> Build(IReadOnlyList<string> paths, IReadOnlySet<string> collapsed)
    {
        var root = new Node("");
        for (int i = 0; i < paths.Count; i++)
        {
            var norm = (paths[i] ?? "").Replace('\\', '/');
            bool dirLike = norm.EndsWith('/');
            var parts = norm.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { root.FileRows.Add((norm, i)); continue; }
            var node = root;
            for (int p = 0; p < parts.Length - 1; p++) node = node.Child(parts[p]);
            node.FileRows.Add((parts[^1] + (dirLike ? "/" : ""), i));
        }
        root.CountFiles();

        var rows = new List<Entry>(paths.Count);
        Emit(root, "", 0, paths, collapsed, rows);
        return rows;
    }

    /// <summary>The folder keys (as <see cref="Build"/> would name them) that hold <paramref name="path"/>, outermost
    /// first; a caller removes them from its collapsed set to bring that file back into view.</summary>
    public static IEnumerable<string> FoldersOf(string path)
    {
        var parts = path.Replace('\\', '/').TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int p = 1; p < parts.Length; p++) yield return string.Join('/', parts, 0, p);
    }

    private static void Emit(Node node, string prefix, int depth, IReadOnlyList<string> paths,
        IReadOnlySet<string> collapsed, List<Entry> rows)
    {
        foreach (var child in node.Children.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            // Fold single-folder chains into one row; the key is the deepest folder's full path.
            var folder = child;
            string label = folder.Name;
            while (folder.FileRows.Count == 0 && folder.Children.Count == 1)
            {
                folder = folder.Children.Values.First();
                label += "/" + folder.Name;
            }
            string key = prefix.Length == 0 ? label : prefix + "/" + label;
            bool open = !collapsed.Contains(key);
            rows.Add(new Entry(true, key, label, depth, -1, folder.FileCount, open));
            if (open) Emit(folder, key, depth + 1, paths, collapsed, rows);
        }
        foreach (var (name, index) in node.FileRows.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            rows.Add(new Entry(false, paths[index], name, depth, index, 1, true));
    }

    private sealed class Node(string name)
    {
        public string Name { get; } = name;
        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
        public List<(string Name, int Index)> FileRows { get; } = new();
        public int FileCount { get; private set; }

        public Node Child(string name)
        {
            if (!Children.TryGetValue(name, out var c)) Children[name] = c = new Node(name);
            return c;
        }

        public int CountFiles() => FileCount = FileRows.Count + Children.Values.Sum(c => c.CountFiles());
    }
}
