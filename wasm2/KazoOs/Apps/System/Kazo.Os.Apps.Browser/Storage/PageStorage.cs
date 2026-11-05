using System.Text.Json;
using Kazo.Os.Apps.Browser.Bridge;

namespace Kazo.Os.Apps.Browser.Storage;

/// <summary>Where <see cref="PageStorage"/> keeps its files; an interface so it is tested without an OS.</summary>
public interface IBrowserStorageFiles
{
    /// <summary>Reads a text file, or returns <see langword="null"/> when it does not exist.</summary>
    Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Writes a text file, creating its directory when needed.</summary>
    Task WriteTextAsync(string path, string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Web Storage of the browser (ADR 0075 §3, questionnaire 8): an opaque-origin frame may not use
/// the host's <c>localStorage</c>, so the bridge keeps a copy and the browser stores it, per site, in
/// the user's files: <c>~/.local/share/kazoos/browser/storage/&lt;site&gt;/local-storage.json</c>,
/// next to the cookies, as a real browser keeps its profile. <c>sessionStorage</c> lives in memory,
/// for the life of the tab.
/// </summary>
/// <remarks>Writes are coalesced: a page that sets ten items in a row costs one or two file writes.</remarks>
public sealed class PageStorage
{
    /// <summary>Characters a storage area may hold, as Chromium's 5 MiB per origin.</summary>
    public const int Quota = 5 * 1024 * 1024;

    /// <summary>Name of the file holding a site's <c>localStorage</c>.</summary>
    public const string LocalStorageFileName = "local-storage.json";

    private readonly IBrowserStorageFiles _files;
    private readonly string _root;
    private readonly Action<string>? _onError;
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, string>> _local = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _session = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private Task _saving = Task.CompletedTask;

    /// <summary>Initializes the storage.</summary>
    /// <param name="files">File access.</param>
    /// <param name="root">Directory holding one subdirectory per site.</param>
    /// <param name="onError">Called with a message when a file cannot be read or written.</param>
    public PageStorage(IBrowserStorageFiles files, string root, Action<string>? onError = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root.TrimEnd('/');
        _onError = onError;
    }

    /// <summary>Gets the storage directory of the current user: <c>~/.local/share/kazoos/browser/storage</c>.</summary>
    public static string GetRoot(string homeDirectory) =>
        $"{homeDirectory.TrimEnd('/')}/.local/share/kazoos/browser/storage";

    /// <summary>Gets the file holding the <c>localStorage</c> of <paramref name="siteKey"/>.</summary>
    public string GetLocalStoragePath(string siteKey) => $"{_root}/{siteKey}/{LocalStorageFileName}";

    /// <summary>Gets a copy of the site's <c>localStorage</c>, reading its file the first time.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetLocalAsync(string siteKey, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_local.TryGetValue(siteKey, out Dictionary<string, string>? cached))
            {
                return new Dictionary<string, string>(cached, StringComparer.Ordinal);
            }
        }

        Dictionary<string, string> loaded = new(StringComparer.Ordinal);
        try
        {
            string? json = await _files.ReadTextAsync(GetLocalStoragePath(siteKey), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(json)
                && JsonSerializer.Deserialize(json, PageBridgeJsonContext.Default.DictionaryStringString) is { } stored)
            {
                loaded = new Dictionary<string, string>(stored, StringComparer.Ordinal);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _onError?.Invoke($"localStorage of {siteKey} could not be read: {exception.Message}");
        }

        lock (_gate)
        {
            // Another page of the same site may have loaded it meanwhile: keep the first copy.
            if (!_local.TryGetValue(siteKey, out Dictionary<string, string>? existing))
            {
                existing = loaded;
                _local[siteKey] = existing;
            }

            return new Dictionary<string, string>(existing, StringComparer.Ordinal);
        }
    }

    /// <summary>Gets a copy of the tab's <c>sessionStorage</c> for the site.</summary>
    public IReadOnlyDictionary<string, string> GetSession(string siteKey)
    {
        lock (_gate)
        {
            return _session.TryGetValue(siteKey, out Dictionary<string, string>? data)
                ? new Dictionary<string, string>(data, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Applies a change made by the page, and schedules the file write for <c>localStorage</c>.</summary>
    public void Apply(string siteKey, BridgeStorageEvent change)
    {
        ArgumentNullException.ThrowIfNull(change);
        bool local = change.Area == "local";
        lock (_gate)
        {
            Dictionary<string, Dictionary<string, string>> areas = local ? _local : _session;
            if (!areas.TryGetValue(siteKey, out Dictionary<string, string>? data))
            {
                data = new Dictionary<string, string>(StringComparer.Ordinal);
                areas[siteKey] = data;
            }

            switch (change.Op)
            {
                case "set" when change.Key is not null:
                    data[change.Key] = change.Value ?? string.Empty;
                    break;
                case "remove" when change.Key is not null:
                    data.Remove(change.Key);
                    break;
                case "clear":
                    data.Clear();
                    break;
                default:
                    return;
            }

            if (local)
            {
                _dirty.Add(siteKey);
                if (_saving.IsCompleted)
                {
                    _saving = SaveAsync();
                }
            }
        }
    }

    /// <summary>Waits until every pending write is done (window close, tests).</summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            return _saving;
        }
    }

    private async Task SaveAsync()
    {
        // Let the page finish its burst of changes before the first write.
        await Task.Yield();
        while (true)
        {
            string siteKey;
            string json;
            lock (_gate)
            {
                if (_dirty.Count == 0)
                {
                    return;
                }

                siteKey = _dirty.First();
                _dirty.Remove(siteKey);
                json = JsonSerializer.Serialize(_local[siteKey], PageBridgeJsonContext.Default.DictionaryStringString);
            }

            try
            {
                await _files.WriteTextAsync(GetLocalStoragePath(siteKey), json).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _onError?.Invoke($"localStorage of {siteKey} could not be saved: {exception.Message}");
            }
        }
    }
}
