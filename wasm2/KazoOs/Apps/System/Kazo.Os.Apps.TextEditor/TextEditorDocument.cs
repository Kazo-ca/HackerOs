using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.TextEditor;

/// <summary>
/// Discriminated union describing the loading state of the editor's current file.
/// </summary>
public enum TextEditorLoadState
{
    /// <summary>No file is loaded; editor shows a new untitled buffer.</summary>
    New,
    /// <summary>A file path is known and the content is fully loaded.</summary>
    Loaded,
    /// <summary>Content is being read from the filesystem.</summary>
    Loading,
    /// <summary>A read or write operation failed.</summary>
    Error
}

/// <summary>
/// Represents one open file in the Text Editor, tracking path, revision, dirty
/// state, and document content. A single editor window holds one document at a time.
/// </summary>
/// <remarks>
/// Revision tracking enables optimistic concurrency: a write is attempted with the
/// observed revision. If the filesystem returns RevisionConflict the caller must
/// re-read the file and present the user with a conflict resolution prompt.
/// </remarks>
public sealed class TextEditorDocument
{
    // The last-known filesystem revision, or null if this is a new (unsaved) buffer.
    private long? _loadedRevision;

    // The editor's texts, for the "Untitled" and "(recovered)" titles (ADR 0104).
    private readonly IStringLocalizer _text;

    /// <summary>Creates an empty, unsaved "Untitled" buffer in the <see cref="TextEditorLoadState.New"/> state.</summary>
    /// <param name="text">
    /// The editor's texts in the window's language; <see langword="null"/> reads them in the language of
    /// the process running the code (English outside any app, as in unit tests).
    /// </param>
    public TextEditorDocument(IStringLocalizer? text = null)
    {
        _text = text ?? KazoLocalization.GetLocalizer<Strings>();
        Title = _text["Title.Untitled"];
        Content = string.Empty;
        LoadState = TextEditorLoadState.New;
    }

    // ──────────────────────────────────── Observed state ──────────────────────────

    /// <summary>Gets the display title for this document (file name or "Untitled", in the editor's language).</summary>
    public string Title { get; private set; }

    /// <summary>Gets the canonical virtual path, or <c>null</c> for an unsaved buffer.</summary>
    public VirtualPath? Path { get; private set; }

    /// <summary>Gets the current editable text content.</summary>
    public string Content { get; private set; }

    /// <summary>Gets the media type of the loaded content (e.g. "text/plain").</summary>
    public string MediaType { get; private set; } = "text/plain";

    /// <summary>Gets the current load state of the document.</summary>
    public TextEditorLoadState LoadState { get; private set; }

    /// <summary>Gets whether the content has been modified since the last save or load.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>Gets the most recent error message, or <c>null</c> when there is none.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Gets the revision observed at the last successful read or save, or <see langword="null"/>
    /// for a buffer that has never been saved (an unconditional write is then correct — there is
    /// no prior revision to require).
    /// </summary>
    public long? LoadedRevision => _loadedRevision;

    // ──────────────────────────────────── Mutations ───────────────────────────────

    /// <summary>Resets the document to an empty, untitled, clean new buffer.</summary>
    public void ResetToNew()
    {
        Path = null;
        Title = _text["Title.Untitled"];
        Content = string.Empty;
        MediaType = "text/plain";
        LoadState = TextEditorLoadState.New;
        IsDirty = false;
        ErrorMessage = null;
        _loadedRevision = null;
    }

    /// <summary>Records that a file load has started.</summary>
    public void BeginLoading(VirtualPath path)
    {
        Path = path;
        Title = ExtractFileName(path.ToString());
        LoadState = TextEditorLoadState.Loading;
        ErrorMessage = null;
    }

    /// <summary>Completes a successful file load.</summary>
    public void CompleteLoading(string content, string mediaType, long revision)
    {
        Content = content;
        MediaType = mediaType;
        LoadState = TextEditorLoadState.Loaded;
        IsDirty = false;
        ErrorMessage = null;
        _loadedRevision = revision;
    }

    /// <summary>Marks the document as failed to load with a reason.</summary>
    public void FailLoading(string errorMessage)
    {
        LoadState = TextEditorLoadState.Error;
        ErrorMessage = errorMessage;
    }

    /// <summary>Updates the in-memory content and marks the buffer dirty.</summary>
    public void Edit(string newContent)
    {
        if (Content != newContent)
        {
            Content = newContent;
            IsDirty = true;
        }
    }

    /// <summary>Records a successful save operation, updating path, title, and revision.</summary>
    public void CompleteSave(VirtualPath path, long newRevision)
    {
        Path = path;
        Title = ExtractFileName(path.ToString());
        LoadState = TextEditorLoadState.Loaded;
        IsDirty = false;
        ErrorMessage = null;
        _loadedRevision = newRevision;
    }

    /// <summary>
    /// Restores the unsaved text of a recovery draft (ADR 0069) as a new, dirty buffer: it is never
    /// written back over the original file without the user choosing where to save it.
    /// </summary>
    /// <param name="content">The recovered text.</param>
    /// <param name="originalPath">The file the text was being edited from, if any, for the title.</param>
    public void RestoreDraft(string content, string? originalPath)
    {
        ResetToNew();
        Title = originalPath is null
            ? _text["Title.RecoveredUntitled"]
            : _text["Title.Recovered", ExtractFileName(originalPath)];
        Content = content;
        IsDirty = true;
    }

    /// <summary>Returns a display title suffix that shows the dirty marker when needed.</summary>
    public string GetWindowTitle() => IsDirty ? $"• {Title}" : Title;

    private static string ExtractFileName(string fullPath)
    {
        // VirtualPath segments use '/' as separator
        int lastSlash = fullPath.LastIndexOf('/');
        return lastSlash >= 0 && lastSlash < fullPath.Length - 1
            ? fullPath[(lastSlash + 1)..]
            : fullPath;
    }
}
