namespace Kazo.Os.Platform.Core.ServerConnection;

/// <summary>
/// Decides when the OS conflict dialog (ADR 0101 section 2) is on screen. The shell renders the
/// dialog; this class only holds the request so platform code (the Settings app's "Résoudre" through
/// <c>KazoServerConnection.ShowConflictsAsync</c>) can open it without knowing the UI.
/// </summary>
/// <remarks>
/// The dialog opens by itself when a pass finds a conflict the user has not seen yet and when a
/// user logs in with conflicts still pending; "Plus tard" closes it until the next login or the
/// next new conflict.
/// </remarks>
public sealed class SyncConflictCenter
{
    private readonly object _sync = new();
    private readonly HashSet<Guid> _seen = [];
    private bool _isOpen;

    /// <summary>Creates the center over the FileSystem sync.</summary>
    /// <param name="fileSystemSync">Source of the pending conflicts.</param>
    public SyncConflictCenter(IFileSystemSyncService fileSystemSync)
    {
        FileSystemSync = fileSystemSync ?? throw new ArgumentNullException(nameof(fileSystemSync));
        FileSystemSync.ConflictsChanged += (_, _) => _ = RefreshAsync(openOnNew: true);
    }

    /// <summary>Raised when the dialog opens or closes, or the pending conflicts change.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the FileSystem sync the dialog resolves conflicts through.</summary>
    public IFileSystemSyncService FileSystemSync { get; }

    /// <summary>Gets whether the dialog should be on screen.</summary>
    public bool IsDialogOpen
    {
        get
        {
            lock (_sync)
            {
                return _isOpen;
            }
        }
    }

    /// <summary>Gets the conflicts known at the last refresh.</summary>
    public IReadOnlyList<SyncFileConflict> Conflicts { get; private set; } = [];

    /// <summary>
    /// Loads the pending conflicts; with <paramref name="openOnNew"/> the dialog opens when one of
    /// them was never shown in this session (a new conflict, or the first look after login).
    /// </summary>
    /// <param name="openOnNew">Whether an unseen conflict opens the dialog.</param>
    public async Task RefreshAsync(bool openOnNew)
    {
        IReadOnlyList<SyncFileConflict> conflicts;
        try
        {
            conflicts = await FileSystemSync.GetConflictsAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return;
        }

        lock (_sync)
        {
            Conflicts = conflicts;
            bool unseen = false;
            foreach (SyncFileConflict conflict in conflicts)
            {
                unseen |= _seen.Add(conflict.RecordId);
            }

            if (conflicts.Count == 0)
            {
                _isOpen = false;
            }
            else if (openOnNew && unseen)
            {
                _isOpen = true;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens the dialog when a conflict is pending (the Settings app's "Résoudre").</summary>
    public async Task OpenAsync()
    {
        await RefreshAsync(openOnNew: false).ConfigureAwait(false);
        lock (_sync)
        {
            _isOpen = Conflicts.Count > 0;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes the dialog without choosing ("Plus tard"): the conflicts stay pending.</summary>
    public void Close()
    {
        lock (_sync)
        {
            _isOpen = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets which conflicts were shown, so the next login opens the dialog again.</summary>
    public void ResetSession()
    {
        lock (_sync)
        {
            _seen.Clear();
            _isOpen = false;
            Conflicts = [];
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
