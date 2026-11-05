using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Xunit;

namespace Kazo.Os.Apps.TextEditor.Tests;

/// <summary>
/// Unit tests for <see cref="TextEditorDocument"/> covering the full round-trip lifecycle:
/// new buffer, load, edit, dirty close, Save As, settings projection, permission rejection, and
/// binary rejection (P2-TEXT-007).
/// </summary>
public sealed class TextEditorDocumentTests
{
    // ──────────────────────────────────── Initial state ──────────────────────────

    [Fact]
    public void InitialState_IsNewCleanUntitled()
    {
        // Arrange / Act
        TextEditorDocument doc = new();

        // Assert
        Assert.Equal(TextEditorLoadState.New, doc.LoadState);
        Assert.Equal("Untitled", doc.Title);
        Assert.Equal(string.Empty, doc.Content);
        Assert.False(doc.IsDirty);
        Assert.Null(doc.Path);
        Assert.Null(doc.ErrorMessage);
        Assert.Null(doc.LoadedRevision);
    }

    [Fact]
    public void GetWindowTitle_NewDocument_ReturnsUntitled()
    {
        TextEditorDocument doc = new();
        Assert.Equal("Untitled", doc.GetWindowTitle());
    }

    // ──────────────────────────────────── New / Reset ─────────────────────────────

    [Fact]
    public void ResetToNew_ClearsAllState()
    {
        // Arrange – get the doc into a loaded+dirty state
        TextEditorDocument doc = new();
        VirtualPath path = VirtualPath.Parse("/home/user/test.txt");
        doc.BeginLoading(path);
        doc.CompleteLoading("hello world", "text/plain", 42L);
        doc.Edit("hello WORLD");

        // Act
        doc.ResetToNew();

        // Assert
        Assert.Equal(TextEditorLoadState.New, doc.LoadState);
        Assert.Equal("Untitled", doc.Title);
        Assert.Equal(string.Empty, doc.Content);
        Assert.False(doc.IsDirty);
        Assert.Null(doc.Path);
        Assert.Null(doc.LoadedRevision);
    }

    // ──────────────────────────────────── Loading ─────────────────────────────────

    [Fact]
    public void BeginLoading_SetsPathAndLoadingState()
    {
        TextEditorDocument doc = new();
        VirtualPath path = VirtualPath.Parse("/home/user/notes.md");

        doc.BeginLoading(path);

        Assert.Equal(TextEditorLoadState.Loading, doc.LoadState);
        Assert.Equal(path, doc.Path);
        Assert.Equal("notes.md", doc.Title);
    }

    [Fact]
    public void CompleteLoading_SetsContentAndRevisionAndCleanState()
    {
        TextEditorDocument doc = new();
        VirtualPath path = VirtualPath.Parse("/home/user/notes.md");
        const string content = "# Heading\nSome content.";
        const string mediaType = "text/markdown";
        const long revision = 99L;

        doc.BeginLoading(path);
        doc.CompleteLoading(content, mediaType, revision);

        Assert.Equal(TextEditorLoadState.Loaded, doc.LoadState);
        Assert.Equal(content, doc.Content);
        Assert.Equal(mediaType, doc.MediaType);
        Assert.Equal(revision, doc.LoadedRevision);
        Assert.False(doc.IsDirty);
        Assert.Null(doc.ErrorMessage);
    }

    [Fact]
    public void FailLoading_SetsErrorState()
    {
        TextEditorDocument doc = new();
        VirtualPath path = VirtualPath.Parse("/etc/kazoos/secret.conf");
        const string errorMessage = "Permission denied.";

        doc.BeginLoading(path);
        doc.FailLoading(errorMessage);

        Assert.Equal(TextEditorLoadState.Error, doc.LoadState);
        Assert.Equal(errorMessage, doc.ErrorMessage);
        Assert.False(doc.IsDirty);
    }

    // ──────────────────────────────────── Editing & Dirty State ──────────────────

    [Fact]
    public void Edit_NewContent_MarksDirty()
    {
        TextEditorDocument doc = new();
        VirtualPath path = VirtualPath.Parse("/home/user/todo.txt");
        doc.BeginLoading(path);
        doc.CompleteLoading("old content", "text/plain", 10L);

        doc.Edit("new content");

        Assert.True(doc.IsDirty);
        Assert.Equal("new content", doc.Content);
    }

    [Fact]
    public void Edit_SameContent_DoesNotMarkDirty()
    {
        TextEditorDocument doc = new();
        VirtualPath path = VirtualPath.Parse("/home/user/todo.txt");
        doc.BeginLoading(path);
        doc.CompleteLoading("same content", "text/plain", 10L);

        doc.Edit("same content"); // No change

        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void GetWindowTitle_DirtyDocument_ReturnsDirtyMarker()
    {
        TextEditorDocument doc = new();
        VirtualPath path = VirtualPath.Parse("/home/user/log.log");
        doc.BeginLoading(path);
        doc.CompleteLoading("data", "text/plain", 5L);
        doc.Edit("modified data");

        string title = doc.GetWindowTitle();

        Assert.StartsWith("•", title);
        Assert.Contains("log.log", title);
    }

    // ──────────────────────────────────── Save / CompleteSave ────────────────────

    [Fact]
    public void CompleteSave_ResetsRevisionAndClearsDirty()
    {
        TextEditorDocument doc = new();
        VirtualPath original = VirtualPath.Parse("/home/user/file.txt");
        doc.BeginLoading(original);
        doc.CompleteLoading("hello", "text/plain", 7L);
        doc.Edit("hello world");
        Assert.True(doc.IsDirty);

        // Act – simulate Save As to new path
        VirtualPath savedAs = VirtualPath.Parse("/home/user/file-copy.txt");
        const long newRevision = 42L;
        doc.CompleteSave(savedAs, newRevision);

        // Assert
        Assert.Equal(savedAs, doc.Path);
        Assert.Equal("file-copy.txt", doc.Title);
        Assert.Equal(TextEditorLoadState.Loaded, doc.LoadState);
        Assert.False(doc.IsDirty);
        Assert.Equal(newRevision, doc.LoadedRevision);
    }

    [Fact]
    public void CompleteSave_FromNeverPersistedDocument_SetsPathAndRevision()
    {
        // Arrange – a brand-new "Untitled" buffer that was never opened from an
        // existing path, so LoadedRevision starts out null (P2-TEXT-003 regression:
        // Save As on such a buffer must create the file, not attempt to replace it).
        TextEditorDocument doc = new();
        doc.Edit("first content");
        Assert.Null(doc.LoadedRevision);

        // Act – simulate a successful Save As via CreateAsync + WriteAsync
        VirtualPath savedAs = VirtualPath.Parse("/home/user/new-file.txt");
        const long createdRevision = 1L;
        doc.CompleteSave(savedAs, createdRevision);

        // Assert
        Assert.Equal(savedAs, doc.Path);
        Assert.Equal("new-file.txt", doc.Title);
        Assert.Equal(TextEditorLoadState.Loaded, doc.LoadState);
        Assert.False(doc.IsDirty);
        Assert.Equal(createdRevision, doc.LoadedRevision);
    }

    [Fact]
    public void GetWindowTitle_AfterSave_HasNoDirtyMarker()
    {
        TextEditorDocument doc = new();
        VirtualPath path = VirtualPath.Parse("/home/user/saved.conf");
        doc.BeginLoading(path);
        doc.CompleteLoading("key=value", "text/plain", 1L);
        doc.Edit("key=new-value");
        doc.CompleteSave(path, 2L);

        Assert.Equal("saved.conf", doc.GetWindowTitle());
    }

    // ──────────────────────────────────── Reload after conflict ──────────────────

    [Fact]
    public void CompleteLoading_AfterConflict_RestoresClearState()
    {
        // Arrange – simulate a write-conflict scenario: user edited the file,
        // external change conflicts, then user reloads.
        TextEditorDocument doc = new();
        VirtualPath path = VirtualPath.Parse("/home/user/config.json");
        doc.BeginLoading(path);
        doc.CompleteLoading("{}", "application/json", 3L);
        doc.Edit("{\"a\":1}");

        // Simulate user deciding to reload (discard) after conflict
        doc.BeginLoading(path);
        doc.CompleteLoading("{\"b\":2}", "application/json", 5L);

        // Assert – reload completes with new content and latest revision
        Assert.Equal("{\"b\":2}", doc.Content);
        Assert.Equal(5L, doc.LoadedRevision);
        Assert.False(doc.IsDirty);
        Assert.Equal(TextEditorLoadState.Loaded, doc.LoadState);
    }

    [Fact]
    public void A_restored_draft_is_a_new_dirty_buffer_that_never_overwrites_the_original()
    {
        // ADR 0069: the text a stopped editor kept comes back as unsaved work.
        TextEditorDocument doc = new();
        doc.BeginLoading(VirtualPath.Parse("/home/alice/notes.txt"));
        doc.CompleteLoading("old", "text/plain", 3);

        doc.RestoreDraft("recovered text", "/home/alice/notes.txt");

        Assert.Equal("recovered text", doc.Content);
        Assert.True(doc.IsDirty);
        Assert.Null(doc.Path);
        Assert.Null(doc.LoadedRevision);
        Assert.Equal("notes.txt (recovered)", doc.Title);
    }
}
