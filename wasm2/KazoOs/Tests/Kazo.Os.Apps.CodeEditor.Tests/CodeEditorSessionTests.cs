using Kazo.Os.App.Abstractions;
using Xunit;

namespace Kazo.Os.Apps.CodeEditor.Tests;

public sealed class CodeEditorSessionTests
{
    [Fact]
    public void Tabs_keep_independent_content_and_restore_deterministic_focus()
    {
        CodeEditorSession session = new();
        CodeEditorDocument first = session.NewDocument();
        Assert.True(first.TryEdit("first").Succeeded);
        CodeEditorDocument second = session.NewDocument();
        Assert.True(second.TryEdit("second").Succeeded);

        Assert.Equal(first.Id, session.Documents[0].Id);
        Assert.Equal("first", session.Documents[0].Content);
        Assert.Equal(second.Id, session.ActiveDocumentId);

        Assert.Equal(CodeEditorCloseResult.ConfirmationRequired, session.Close(second.Id));
        Assert.Equal(CodeEditorCloseResult.Closed, session.Close(second.Id, discardDirty: true));
        Assert.Equal(first.Id, session.ActiveDocumentId);
        Assert.Equal("first", session.ActiveDocument!.Content);
    }

    [Fact]
    public void Opening_same_path_activates_existing_tab_without_replacing_dirty_content()
    {
        CodeEditorSession session = new();
        VirtualPath path = VirtualPath.Parse("/home/user/app.cs");
        CodeEditorDocument first = session.OpenDocument(path, "class A {}", 4);
        Assert.True(first.TryEdit("class Dirty {}").Succeeded);
        session.NewDocument();

        CodeEditorDocument reopened = session.OpenDocument(path, "class External {}", 9);

        Assert.Same(first, reopened);
        Assert.Equal("class Dirty {}", reopened.Content);
        Assert.Equal(2, session.Documents.Count);
        Assert.Equal(first.Id, session.ActiveDocumentId);
    }

    [Fact]
    public void Recovery_round_trip_preserves_active_tab_dirty_baseline_and_modes()
    {
        CodeEditorSession session = new();
        CodeEditorDocument loaded = session.OpenDocument(
            VirtualPath.Parse("/home/user/site.js"), "const value = 1;", 12);
        loaded.SetSyntaxMode(CodeEditorSyntaxMode.TypeScript);
        Assert.True(loaded.TryEdit("const value: number = 2;").Succeeded);
        CodeEditorDocument draft = session.NewDocument();
        Assert.True(draft.TryEdit("# draft").Succeeded);
        session.Activate(loaded.Id);

        CodeEditorSession restored = CodeEditorSession.Restore(session.CaptureRecovery());

        Assert.Equal(2, restored.Documents.Count);
        Assert.Equal(loaded.Id, restored.ActiveDocumentId);
        Assert.True(restored.ActiveDocument!.IsDirty);
        Assert.Equal("const value: number = 2;", restored.ActiveDocument.Content);
        Assert.Equal(CodeEditorSyntaxMode.TypeScript, restored.ActiveDocument.SyntaxMode);
        restored.ActiveDocument.CompleteSave(VirtualPath.Parse("/home/user/site.js"), 13);
        Assert.False(restored.ActiveDocument.IsDirty);
    }

    [Fact]
    public void Oversized_edit_is_recoverable_and_does_not_replace_buffer()
    {
        CodeEditorDocument document = CodeEditorDocument.CreateNew(1);
        Assert.True(document.TryEdit("safe").Succeeded);

        CodeEditorEditResult result = document.TryEdit(
            new string('x', CodeEditorDocument.MaxDocumentBytes + 1));

        Assert.False(result.Succeeded);
        Assert.Equal("safe", document.Content);
        Assert.Contains("limit", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/home/user/source.cs", CodeEditorSyntaxMode.CSharp)]
    [InlineData("/home/user/app.ts", CodeEditorSyntaxMode.TypeScript)]
    [InlineData("/home/user/index.html", CodeEditorSyntaxMode.Html)]
    [InlineData("/home/user/data.json", CodeEditorSyntaxMode.Json)]
    [InlineData("/home/user/README.md", CodeEditorSyntaxMode.Markdown)]
    [InlineData("/home/user/LICENSE", CodeEditorSyntaxMode.PlainText)]
    public void Syntax_mode_is_selected_from_path(string path, CodeEditorSyntaxMode expected)
    {
        Assert.Equal(expected, CodeEditorSyntaxModeDetector.FromPath(VirtualPath.Parse(path)));
    }

    [Fact]
    public void Renaming_a_file_or_folder_moves_its_tabs_and_keeps_unsaved_edits()
    {
        CodeEditorSession session = new();
        CodeEditorDocument app = session.OpenDocument(VirtualPath.Parse("/p/src/App.cs"), "a", 1);
        CodeEditorDocument util = session.OpenDocument(VirtualPath.Parse("/p/src/util/Tools.cs"), "b", 1);
        CodeEditorDocument other = session.OpenDocument(VirtualPath.Parse("/p/srcx/Other.cs"), "c", 1);
        Assert.True(util.TryEdit("b edited").Succeeded);

        int renamedFile = session.RelocatePaths(VirtualPath.Parse("/p/src/App.cs"), VirtualPath.Parse("/p/src/Program.cs"));
        int renamedFolder = session.RelocatePaths(VirtualPath.Parse("/p/src"), VirtualPath.Parse("/p/source"));

        Assert.Equal(1, renamedFile);
        Assert.Equal(2, renamedFolder);
        Assert.Equal("/p/source/Program.cs", app.Path?.Value);
        Assert.Equal("Program.cs", app.Title);
        Assert.Equal("/p/source/util/Tools.cs", util.Path?.Value);
        Assert.True(util.IsDirty);
        Assert.Equal("/p/srcx/Other.cs", other.Path?.Value);
    }
}
