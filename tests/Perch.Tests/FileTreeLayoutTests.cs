using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class FileTreeLayoutTests
{
    private static readonly HashSet<string> None = new();

    private static string Show(IReadOnlyList<FileTreeLayout.Entry> rows) =>
        string.Join("\n", rows.Select(r => new string(' ', r.Depth * 2) + (r.IsFolder ? (r.Expanded ? "▾ " : "▸ ") : "") + r.Label));

    [Fact]
    public void Deep_single_chains_fold_into_one_folder_row()
    {
        var rows = FileTreeLayout.Build(
        [
            "frontend/spa/apps/client/src/dynamicForms/fields/Text.vue",
            "frontend/spa/apps/client/src/dynamicForms/fields/Select.vue",
            "frontend/spa/apps/client/src/composables/useForm.ts",
            "frontend/spa/apps/client/src/lang/en.json",
        ], None);

        Assert.Equal(
            """
            ▾ frontend/spa/apps/client/src
              ▾ composables
                useForm.ts
              ▾ dynamicForms/fields
                Select.vue
                Text.vue
              ▾ lang
                en.json
            """.ReplaceLineEndings("\n"), Show(rows));
        Assert.Equal("frontend/spa/apps/client/src/dynamicForms/fields", rows[3].Key);
        Assert.Equal(4, rows[0].Files);
    }

    [Fact]
    public void Folders_sort_before_files_and_root_files_sit_at_depth_zero()
    {
        var rows = FileTreeLayout.Build(["README.md", "src/b.cs", "src/a.cs", "docs/x.md", "Directory.Build.props"], None);
        Assert.Equal(
            """
            ▾ docs
              x.md
            ▾ src
              a.cs
              b.cs
            Directory.Build.props
            README.md
            """.ReplaceLineEndings("\n"), Show(rows));
    }

    [Fact]
    public void File_rows_point_back_at_their_input_index()
    {
        string[] paths = ["z/one.txt", "a/two.txt"];
        var rows = FileTreeLayout.Build(paths, None);
        foreach (var r in rows.Where(r => !r.IsFolder))
            Assert.Equal(paths[r.FileIndex], r.Key);
    }

    [Fact]
    public void A_collapsed_folder_hides_everything_under_it()
    {
        var rows = FileTreeLayout.Build(["a/b/one.txt", "a/c/two.txt", "top.txt"], new HashSet<string> { "a" });
        Assert.Equal(
            """
            ▸ a
            top.txt
            """.ReplaceLineEndings("\n"), Show(rows));
        Assert.Equal(2, rows[0].Files);
    }

    [Fact]
    public void An_untracked_directory_keeps_its_trailing_slash_as_a_file_row()
    {
        var rows = FileTreeLayout.Build(["src/new/", "src/old.cs"], None);
        Assert.Equal(
            """
            ▾ src
              new/
              old.cs
            """.ReplaceLineEndings("\n"), Show(rows));
        Assert.Equal("src/new/", rows[1].Key);
    }

    [Fact]
    public void FoldersOf_lists_every_ancestor_outermost_first()
    {
        Assert.Equal(["a", "a/b", "a/b/c"], FileTreeLayout.FoldersOf("a/b/c/d.txt"));
        Assert.Empty(FileTreeLayout.FoldersOf("root.txt"));
    }
}
