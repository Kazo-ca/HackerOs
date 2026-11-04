using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Apps.CodeEditor;

/// <summary>Describes the result of a requested tab close.</summary>
public enum CodeEditorCloseResult
{
    /// <summary>The tab was closed.</summary>
    Closed,
    /// <summary>The dirty tab requires explicit discard confirmation.</summary>
    ConfirmationRequired,
    /// <summary>No matching tab exists.</summary>
    NotFound
}

/// <summary>Serializable C#-owned recovery state for an editor window.</summary>
public sealed record CodeEditorSessionRecovery(
    IReadOnlyList<CodeEditorDocumentRecovery> Documents,
    Guid? ActiveDocumentId,
    int NextUntitledSequence);

/// <summary>Owns multiple independent editor tabs and deterministic activation behavior.</summary>
public sealed class CodeEditorSession
{
    private readonly List<CodeEditorDocument> _documents = [];
    private int _nextUntitledSequence = 1;

    /// <summary>Gets the open documents in display order.</summary>
    public IReadOnlyList<CodeEditorDocument> Documents => _documents;

    /// <summary>Gets the active document, when one is open.</summary>
    public CodeEditorDocument? ActiveDocument =>
        ActiveDocumentId is Guid id ? _documents.SingleOrDefault(document => document.Id == id) : null;

    /// <summary>Gets the active document identifier.</summary>
    public Guid? ActiveDocumentId { get; private set; }

    /// <summary>Creates and activates a new independent buffer.</summary>
    public CodeEditorDocument NewDocument()
    {
        CodeEditorDocument document = CodeEditorDocument.CreateNew(_nextUntitledSequence++);
        _documents.Add(document);
        ActiveDocumentId = document.Id;
        return document;
    }

    /// <summary>
    /// Adds and activates a loaded document, or activates the existing tab for the same path.
    /// </summary>
    public CodeEditorDocument OpenDocument(
        VirtualPath path,
        string content,
        long revision,
        CodeEditorSyntaxMode? syntaxMode = null)
    {
        CodeEditorDocument? existing = _documents.FirstOrDefault(document => document.Path == path);
        if (existing is not null)
        {
            ActiveDocumentId = existing.Id;
            return existing;
        }

        CodeEditorDocument document = CodeEditorDocument.CreateLoaded(path, content, revision, syntaxMode);
        _documents.Add(document);
        ActiveDocumentId = document.Id;
        return document;
    }

    /// <summary>Activates an existing tab.</summary>
    public bool Activate(Guid documentId)
    {
        if (_documents.All(document => document.Id != documentId))
        {
            return false;
        }

        ActiveDocumentId = documentId;
        return true;
    }

    /// <summary>Closes a tab, requiring explicit confirmation when it is dirty.</summary>
    public CodeEditorCloseResult Close(Guid documentId, bool discardDirty = false)
    {
        int index = _documents.FindIndex(document => document.Id == documentId);
        if (index < 0)
        {
            return CodeEditorCloseResult.NotFound;
        }

        if (_documents[index].IsDirty && !discardDirty)
        {
            return CodeEditorCloseResult.ConfirmationRequired;
        }

        bool wasActive = ActiveDocumentId == documentId;
        _documents.RemoveAt(index);
        if (wasActive)
        {
            ActiveDocumentId = _documents.Count == 0
                ? null
                : _documents[Math.Min(index, _documents.Count - 1)].Id;
        }

        return CodeEditorCloseResult.Closed;
    }

    /// <summary>
    /// Re-points the tabs of a renamed file, or of every file under a renamed folder, from
    /// <paramref name="oldPath"/> to <paramref name="newPath"/>. Returns how many tabs moved.
    /// </summary>
    public int RelocatePaths(VirtualPath oldPath, VirtualPath newPath)
    {
        string oldPrefix = oldPath.Value == "/" ? "/" : oldPath.Value + "/";
        int moved = 0;
        foreach (CodeEditorDocument document in _documents)
        {
            if (document.Path is not { } path)
            {
                continue;
            }

            if (path == oldPath)
            {
                document.Relocate(newPath);
                moved++;
            }
            else if (path.Value.StartsWith(oldPrefix, StringComparison.Ordinal))
            {
                string rest = path.Value[oldPrefix.Length..];
                document.Relocate(VirtualPath.Parse(newPath.Value == "/" ? "/" + rest : newPath.Value + "/" + rest));
                moved++;
            }
        }

        return moved;
    }

    /// <summary>Captures all buffers for a host-provided persistence boundary.</summary>
    public CodeEditorSessionRecovery CaptureRecovery() => new(
        _documents.Select(document => document.CaptureRecovery()).ToArray(),
        ActiveDocumentId,
        _nextUntitledSequence);

    /// <summary>Restores an editor session without sharing mutable tab state.</summary>
    public static CodeEditorSession Restore(CodeEditorSessionRecovery recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        CodeEditorSession session = new()
        {
            _nextUntitledSequence = Math.Max(1, recovery.NextUntitledSequence)
        };
        session._documents.AddRange(recovery.Documents.Select(CodeEditorDocument.Restore));
        session.ActiveDocumentId = recovery.ActiveDocumentId is Guid active
                                   && session._documents.Any(document => document.Id == active)
            ? active
            : session._documents.FirstOrDefault()?.Id;
        return session;
    }
}
