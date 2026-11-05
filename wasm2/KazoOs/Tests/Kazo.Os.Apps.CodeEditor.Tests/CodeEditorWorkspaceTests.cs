using Kazo.Os.App.Abstractions;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Apps.CodeEditor.Tests;

public sealed class CodeEditorWorkspaceTests
{
    private static readonly VirtualPath Root = VirtualPath.Parse("/home/user/project");

    private static FakeAppFileSystemGateway Project() => new FakeAppFileSystemGateway()
        .WithFile("/home/user/project/README.md", "# Project\nSee src.")
        .WithFile("/home/user/project/src/App.cs", "class App\n{\n    // TODO: start\n}\n")
        .WithFile("/home/user/project/src/Util/StringTools.cs", "static class StringTools { /* todo */ }")
        .WithFile("/home/user/project/src/site.js", "const todoList = [];")
        .WithFile("/home/user/project/node_modules/lib/index.js", "// TODO inside a dependency")
        .WithFile("/home/user/project/bin/Debug/App.dll", "binary")
        .WithFile("/home/user/elsewhere.txt", "TODO outside the folder");

    [Fact]
    public async Task Listing_walks_subfolders_and_skips_excluded_ones()
    {
        CodeEditorWorkspace workspace = new(Root, Project());

        CodeEditorWorkspaceListing listing = await workspace.ListFilesAsync();

        Assert.False(listing.Truncated);
        Assert.Equal(
            ["README.md", "src/App.cs", "src/site.js", "src/Util/StringTools.cs"],
            listing.Files.Select(file => file.RelativePath).ToArray());
        CodeEditorWorkspaceFile tools = listing.Files.Single(file => file.Name == "StringTools.cs");
        Assert.Equal("src/Util", tools.Directory);
        Assert.Equal(VirtualPath.Parse("/home/user/project/src/Util/StringTools.cs"), tools.Path);
    }

    [Fact]
    public async Task Creates_files_and_folders_and_refuses_duplicates_and_bad_names()
    {
        FakeAppFileSystemGateway fileSystem = Project();
        CodeEditorWorkspace workspace = new(Root, fileSystem);
        VirtualPath src = VirtualPath.Parse("/home/user/project/src");

        CodeEditorCreateResult file = await workspace.CreateFileAsync(src, " Program.cs ");
        CodeEditorCreateResult folder = await workspace.CreateDirectoryAsync(Root, "docs");
        CodeEditorCreateResult duplicate = await workspace.CreateFileAsync(src, "App.cs");
        CodeEditorCreateResult nested = await workspace.CreateFileAsync(src, "a/b.cs");

        Assert.True(file.Succeeded);
        Assert.Equal(VirtualPath.Parse("/home/user/project/src/Program.cs"), file.Path);
        Assert.True(fileSystem.Exists("/home/user/project/src/Program.cs"));
        Assert.True(folder.Succeeded);
        Assert.True(fileSystem.Exists("/home/user/project/docs"));
        Assert.False(duplicate.Succeeded);
        Assert.Contains("already exists", duplicate.Error);
        Assert.False(nested.Succeeded);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData(".gitignore", true)]
    [InlineData("main.cs", true)]
    public void Validates_typed_names(string name, bool valid) =>
        Assert.Equal(valid, CodeEditorWorkspace.ValidateName(name) is null);

    [Fact]
    public void Relative_paths_and_containment()
    {
        CodeEditorWorkspace workspace = new(Root, new FakeAppFileSystemGateway());

        Assert.Equal("src/App.cs", workspace.RelativePathOf(VirtualPath.Parse("/home/user/project/src/App.cs")));
        Assert.True(workspace.Contains(Root));
        Assert.True(workspace.Contains(VirtualPath.Parse("/home/user/project/src")));
        Assert.False(workspace.Contains(VirtualPath.Parse("/home/user/project2/a.cs")));
        Assert.Equal("project", workspace.Name);
    }

    [Fact]
    public async Task Search_finds_matches_across_the_folder_case_insensitively_by_default()
    {
        CodeEditorWorkspace workspace = new(Root, Project());

        CodeEditorSearchResult result = await new CodeEditorWorkspaceSearch(workspace)
            .SearchAsync(new CodeEditorSearchQuery("todo"));

        Assert.Null(result.Error);
        Assert.Equal(3, result.MatchCount);
        Assert.Equal(
            ["src/App.cs", "src/site.js", "src/Util/StringTools.cs"],
            result.Files.Select(file => file.File.RelativePath).ToArray());
        CodeEditorSearchMatch app = result.Files[0].Matches.Single();
        Assert.Equal(3, app.Line);
        Assert.Equal(8, app.Column);
        Assert.Equal("// TODO: start", app.LineText);
        Assert.Equal(3, app.PreviewOffset);
    }

    [Fact]
    public async Task Search_honours_match_case_whole_word_and_globs()
    {
        CodeEditorWorkspaceSearch search = new(new CodeEditorWorkspace(Root, Project()));

        CodeEditorSearchResult caseSensitive = await search.SearchAsync(new CodeEditorSearchQuery("TODO", MatchCase: true));
        CodeEditorSearchResult wholeWord = await search.SearchAsync(new CodeEditorSearchQuery("todo", WholeWord: true));
        CodeEditorSearchResult included = await search.SearchAsync(new CodeEditorSearchQuery("todo", Include: "*.cs"));
        CodeEditorSearchResult excluded = await search.SearchAsync(new CodeEditorSearchQuery("todo", Exclude: "src/Util/**"));

        Assert.Equal(["src/App.cs"], caseSensitive.Files.Select(file => file.File.RelativePath).ToArray());
        Assert.Equal(2, wholeWord.MatchCount);
        Assert.DoesNotContain(wholeWord.Files, file => file.File.Name == "site.js");
        Assert.All(included.Files, file => Assert.EndsWith(".cs", file.File.Name));
        Assert.DoesNotContain(excluded.Files, file => file.File.Name == "StringTools.cs");
    }

    [Fact]
    public async Task The_background_search_finds_what_the_inline_search_finds()
    {
        // ADR 0140: the Search view runs the search as a KazoBackground task; without the OS (a unit
        // test) the task runs inline, with the same JSON crossing of its input and result.
        FakeAppFileSystemGateway fileSystem = Project();
        CodeEditorWorkspaceSearch search = new(new CodeEditorWorkspace(Root, fileSystem));
        VirtualPath readme = VirtualPath.Parse("/home/user/project/README.md");
        Func<VirtualPath, string?> open = path => path == readme ? "todo typed but unsaved" : null;
        using IDisposable scope = Kazo.Os.AppSdk.KazoExecutionContext.Enter(new MinimalAppExecutionContext(
            AppDefinitionMapper.ToManifest(new CodeEditorApp()), fileSystem: fileSystem));
        List<Kazo.Os.AppSdk.Threading.KazoBackgroundProgress> reports = [];

        CodeEditorSearchResult inline = await search.SearchAsync(new CodeEditorSearchQuery("todo"), open);
        CodeEditorSearchResult background = await search.SearchInBackgroundAsync(
            new CodeEditorSearchQuery("todo"), open, new Collect(reports), CancellationToken.None);
        CodeEditorSearchResult invalid = await search.SearchInBackgroundAsync(
            new CodeEditorSearchQuery("(", UseRegex: true), null, null, CancellationToken.None);

        Assert.Equal(inline.MatchCount, background.MatchCount);
        Assert.Equal(inline.FilesSearched, background.FilesSearched);
        Assert.Equal(
            inline.Files.Select(file => (file.File, string.Join('|', file.Matches))).ToArray(),
            background.Files.Select(file => (file.File, string.Join('|', file.Matches))).ToArray());
        Assert.Contains(background.Files, file => file.File.RelativePath == "README.md");
        Assert.Equal(1d, reports[^1].Fraction);
        Assert.NotNull(invalid.Error);
    }

    private sealed class Collect(List<Kazo.Os.AppSdk.Threading.KazoBackgroundProgress> reports) : IProgress<Kazo.Os.AppSdk.Threading.KazoBackgroundProgress>
    {
        public void Report(Kazo.Os.AppSdk.Threading.KazoBackgroundProgress value) => reports.Add(value);
    }

    [Fact]
    public async Task Search_uses_unsaved_tab_content_and_reports_invalid_patterns()
    {
        CodeEditorWorkspaceSearch search = new(new CodeEditorWorkspace(Root, Project()));
        VirtualPath readme = VirtualPath.Parse("/home/user/project/README.md");

        CodeEditorSearchResult unsaved = await search.SearchAsync(
            new CodeEditorSearchQuery("unsaved"),
            path => path == readme ? "typed but unsaved" : null);
        CodeEditorSearchResult regex = await search.SearchAsync(new CodeEditorSearchQuery(@"class \w+", UseRegex: true));
        CodeEditorSearchResult invalid = await search.SearchAsync(new CodeEditorSearchQuery("(", UseRegex: true));

        Assert.Equal("README.md", Assert.Single(unsaved.Files).File.RelativePath);
        Assert.Equal(2, regex.MatchCount);
        Assert.NotNull(invalid.Error);
        Assert.Empty(invalid.Files);
    }

    [Fact]
    public void Text_search_skips_empty_matches_and_handles_crlf()
    {
        System.Text.RegularExpressions.Regex matcher = CodeEditorTextSearch.BuildMatcher(
            new CodeEditorSearchQuery("x*", UseRegex: true), out _)!;

        IReadOnlyList<CodeEditorSearchMatch> matches = CodeEditorTextSearch.Search("ab\r\nxx\r\n", matcher);

        CodeEditorSearchMatch match = Assert.Single(matches);
        Assert.Equal(2, match.Line);
        Assert.Equal(1, match.Column);
        Assert.Equal(2, match.Length);
    }

    [Theory]
    [InlineData("*.cs", "src/App.cs", true)]
    [InlineData("*.cs", "src/site.js", false)]
    [InlineData("src/**", "src/Util/StringTools.cs", true)]
    [InlineData("src/*.cs", "src/Util/StringTools.cs", false)]
    [InlineData("**/Util/*", "src/Util/StringTools.cs", true)]
    [InlineData("*.js, *.md", "README.md", true)]
    public void Globs_match_names_and_relative_paths(string glob, string relativePath, bool expected)
    {
        CodeEditorWorkspaceFile file = new(VirtualPath.Parse("/p/" + relativePath), relativePath, 0);

        Assert.Equal(expected, CodeEditorGlob.Parse(glob).Matches(file));
    }

    [Fact]
    public void Quick_open_ranks_file_name_matches_first()
    {
        CodeEditorWorkspaceFile[] files =
        [
            new(VirtualPath.Parse("/p/src/CodeEditorWindow.razor"), "src/CodeEditorWindow.razor", 0),
            new(VirtualPath.Parse("/p/docs/editor/window-notes.md"), "docs/editor/window-notes.md", 0),
            new(VirtualPath.Parse("/p/README.md"), "README.md", 0)
        ];

        IReadOnlyList<CodeEditorWorkspaceFile> ranked = CodeEditorQuickOpen.Filter(files, "edwin");
        IReadOnlyList<CodeEditorWorkspaceFile> all = CodeEditorQuickOpen.Filter(files, "");

        Assert.Equal("CodeEditorWindow.razor", ranked[0].Name);
        Assert.DoesNotContain(ranked, file => file.Name == "README.md");
        Assert.Equal(3, all.Count);
    }
}
