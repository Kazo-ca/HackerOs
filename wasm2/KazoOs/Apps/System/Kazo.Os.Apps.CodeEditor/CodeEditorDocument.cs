using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Apps.CodeEditor;

/// <summary>Identifies syntax modes supported by the locally bundled editor.</summary>
public enum CodeEditorSyntaxMode
{
    /// <summary>Unhighlighted plain text.</summary>
    PlainText,
    /// <summary>C# source.</summary>
    CSharp,
    /// <summary>JavaScript source.</summary>
    JavaScript,
    /// <summary>TypeScript source.</summary>
    TypeScript,
    /// <summary>HTML markup.</summary>
    Html,
    /// <summary>CSS stylesheets.</summary>
    Css,
    /// <summary>JSON data.</summary>
    Json,
    /// <summary>Markdown documents.</summary>
    Markdown
}

/// <summary>Describes a recoverable edit rejection.</summary>
public sealed record CodeEditorEditResult(bool Succeeded, string? Error = null);

/// <summary>Serializable C#-owned recovery state for one editor document.</summary>
public sealed record CodeEditorDocumentRecovery(
    Guid Id,
    string? Path,
    string Content,
    string SavedContent,
    long? Revision,
    CodeEditorSyntaxMode SyntaxMode,
    string UntitledName);

/// <summary>
/// Owns one independent editor buffer, its optimistic VFS revision, syntax mode,
/// and dirty-state baseline.
/// </summary>
public sealed class CodeEditorDocument
{
    /// <summary>Maximum UTF-8 document size accepted by the editor.</summary>
    public const int MaxDocumentBytes = 1024 * 1024;

    private string _savedContent;

    private CodeEditorDocument(
        Guid id,
        string untitledName,
        VirtualPath? path,
        string content,
        string savedContent,
        long? revision,
        CodeEditorSyntaxMode syntaxMode)
    {
        Id = id;
        UntitledName = untitledName;
        Path = path;
        Content = content;
        _savedContent = savedContent;
        Revision = revision;
        SyntaxMode = syntaxMode;
    }

    /// <summary>Gets the stable tab/document identifier.</summary>
    public Guid Id { get; }

    /// <summary>Gets the canonical VFS path, or <see langword="null"/> for a new buffer.</summary>
    public VirtualPath? Path { get; private set; }

    /// <summary>Gets the current editable content.</summary>
    public string Content { get; private set; }

    /// <summary>Gets the revision used for optimistic VFS writes.</summary>
    public long? Revision { get; private set; }

    /// <summary>Gets the syntax mode selected for this document.</summary>
    public CodeEditorSyntaxMode SyntaxMode { get; private set; }

    /// <summary>Gets the deterministic display name for an unsaved buffer.</summary>
    public string UntitledName { get; }

    /// <summary>Gets the file name or deterministic untitled-buffer name.</summary>
    public string Title => Path is null ? UntitledName : GetFileName(Path.Value);

    /// <summary>Gets whether the current buffer differs from its load/save baseline.</summary>
    public bool IsDirty => !string.Equals(Content, _savedContent, StringComparison.Ordinal);

    /// <summary>Gets the current UTF-8 buffer size.</summary>
    public int Utf8ByteCount => Encoding.UTF8.GetByteCount(Content);

    /// <summary>Creates a clean, unsaved editor document.</summary>
    public static CodeEditorDocument CreateNew(int sequence) =>
        new(Guid.NewGuid(), Strings.Ambient["Document.Untitled", sequence], null, string.Empty, string.Empty, null,
            CodeEditorSyntaxMode.PlainText);

    /// <summary>Creates a clean document from a successful VFS load.</summary>
    public static CodeEditorDocument CreateLoaded(
        VirtualPath path,
        string content,
        long revision,
        CodeEditorSyntaxMode? syntaxMode = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureWithinLimit(content);
        return new CodeEditorDocument(
            Guid.NewGuid(), "Untitled", path, content, content, revision,
            syntaxMode ?? CodeEditorSyntaxModeDetector.FromPath(path));
    }

    /// <summary>Restores a document from an opaque recovery record owned by C#.</summary>
    public static CodeEditorDocument Restore(CodeEditorDocumentRecovery recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        EnsureWithinLimit(recovery.Content);
        EnsureWithinLimit(recovery.SavedContent);
        VirtualPath? path = recovery.Path is null ? null : VirtualPath.Parse(recovery.Path);
        return new CodeEditorDocument(
            recovery.Id,
            recovery.UntitledName,
            path,
            recovery.Content,
            recovery.SavedContent,
            recovery.Revision,
            recovery.SyntaxMode);
    }

    /// <summary>Updates the buffer without allowing the configured size bound to be exceeded.</summary>
    public CodeEditorEditResult TryEdit(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        int size = Encoding.UTF8.GetByteCount(content);
        if (size > MaxDocumentBytes)
        {
            return new CodeEditorEditResult(
                false,
                Strings.Ambient["Document.ExceedsLimit", MaxDocumentBytes / 1024]);
        }

        Content = content;
        return new CodeEditorEditResult(true);
    }

    /// <summary>Changes only the display/highlighting mode; it never executes content.</summary>
    public void SetSyntaxMode(CodeEditorSyntaxMode syntaxMode) => SyntaxMode = syntaxMode;

    /// <summary>Commits the successful VFS write as the new clean baseline.</summary>
    public void CompleteSave(VirtualPath path, long revision)
    {
        Path = path;
        Revision = revision;
        _savedContent = Content;
    }

    /// <summary>
    /// Follows a rename or move of the file (or of a folder above it) made in the explorer: the
    /// buffer, its dirty state and revision are kept, only the path changes.
    /// </summary>
    public void Relocate(VirtualPath path) => Path = path;

    /// <summary>Captures C#-owned recovery state without browser or editor-framework types.</summary>
    public CodeEditorDocumentRecovery CaptureRecovery() => new(
        Id,
        Path?.Value,
        Content,
        _savedContent,
        Revision,
        SyntaxMode,
        UntitledName);

    private static void EnsureWithinLimit(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxDocumentBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(content),
                Strings.Ambient["Document.ExceedsLimit", MaxDocumentBytes / 1024]);
        }
    }

    private static string GetFileName(VirtualPath path)
    {
        string value = path.Value;
        int separator = value.LastIndexOf('/');
        return separator >= 0 ? value[(separator + 1)..] : value;
    }
}

/// <summary>Maps virtual file extensions to deterministic editor syntax modes.</summary>
public static class CodeEditorSyntaxModeDetector
{
    /// <summary>Returns the supported syntax mode for a virtual path.</summary>
    public static CodeEditorSyntaxMode FromPath(VirtualPath path)
    {
        string extension = System.IO.Path.GetExtension(path.Value);
        return extension.ToLowerInvariant() switch
        {
            ".cs" => CodeEditorSyntaxMode.CSharp,
            ".js" or ".mjs" or ".cjs" => CodeEditorSyntaxMode.JavaScript,
            ".ts" or ".tsx" => CodeEditorSyntaxMode.TypeScript,
            ".html" or ".htm" or ".razor" => CodeEditorSyntaxMode.Html,
            ".css" => CodeEditorSyntaxMode.Css,
            ".json" => CodeEditorSyntaxMode.Json,
            ".md" or ".markdown" => CodeEditorSyntaxMode.Markdown,
            _ => CodeEditorSyntaxMode.PlainText
        };
    }
}
