using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Shell;

/// <summary>
/// Maintains and persists the session user's ordered quick-launch pins in
/// <c>~/.config/hackeros/start-menu.json</c> (ADR 0044 section 8). The service deliberately knows
/// nothing about the live app catalog: syntactically valid unavailable IDs are retained so
/// temporarily disabled or unmounted applications regain their pins when available.
/// </summary>
/// <remarks>
/// Each user's pins live in that user's own home, so file permissions isolate profiles: only the
/// session user's profile is ever loaded, and mutating another user's profile is refused.
/// </remarks>
public sealed class StartMenuPreferencesService : IDisposable
{
    private static readonly IReadOnlyList<string> EmptyPins = Array.AsReadOnly(Array.Empty<string>());

    private readonly IOsSettingsService _settings;
    private readonly ISessionService _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IReadOnlyList<string> _defaultPins;
    private Dictionary<LocalUserId, IReadOnlyList<string>> _profiles = [];
    private Dictionary<LocalUserId, IReadOnlyList<string>> _defaultPinsApplied = [];
    private bool _initialized;

    /// <summary>Creates a scoped start-menu preference service over the session user's settings file.</summary>
    /// <param name="settings">OS settings files.</param>
    /// <param name="session">Session service identifying whose pins are loaded.</param>
    public StartMenuPreferencesService(IOsSettingsService settings, ISessionService session)
        : this(settings, session, StartMenuSettingsDocuments.DefaultPinnedAppIds)
    {
    }

    /// <summary>Creates the service with the apps pinned by default (ADR 0212 §6).</summary>
    /// <param name="settings">OS settings files.</param>
    /// <param name="session">Session service identifying whose pins are loaded.</param>
    /// <param name="defaultPins">
    /// Apps pinned once to each profile, new or existing, at its first load; an app the user unpins
    /// afterwards stays unpinned.
    /// </param>
    public StartMenuPreferencesService(IOsSettingsService settings, ISessionService session, IReadOnlyList<string> defaultPins)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _defaultPins = defaultPins ?? throw new ArgumentNullException(nameof(defaultPins));
        _settings.DocumentChanged += OnDocumentChanged;
    }

    /// <summary>Raised whenever an optimistic update or reconciliation changes any user's pins.</summary>
    public event Action? Changed;

    /// <summary>Gets an immutable snapshot of one user's ordered pinned app IDs.</summary>
    /// <param name="userId">Opaque local user whose isolated profile is requested.</param>
    /// <returns>The ordered pins, or an empty list when that user has no stored profile.</returns>
    public IReadOnlyList<string> GetPinnedAppIds(LocalUserId userId)
    {
        ValidateUserId(userId);
        return _profiles.TryGetValue(userId, out IReadOnlyList<string>? pins) ? pins : EmptyPins;
    }

    /// <summary>Loads all persisted user profiles without emitting an initial change notification.</summary>
    /// <param name="cancellationToken">Signals cancellation of the settings read.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(raiseEvent: false, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Appends a syntactically valid app ID to one user's quick-launch section.</summary>
    /// <param name="userId">Owner of the isolated pin profile.</param>
    /// <param name="appId">Manifest-compatible app ID; catalog presence is intentionally not required.</param>
    /// <param name="cancellationToken">Signals cancellation of persistence.</param>
    /// <returns><see langword="true"/> when a new pin was committed.</returns>
    public Task<bool> PinAsync(
        LocalUserId userId,
        string appId,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);
        ValidateAppId(appId);
        return MutateAsync(userId, pins =>
        {
            if (pins.Contains(appId, StringComparer.Ordinal)
                || pins.Count >= StartMenuSettingsDocuments.MaximumPinnedAppCount)
            {
                return false;
            }

            pins.Add(appId);
            return true;
        }, cancellationToken);
    }

    /// <summary>Removes an app ID from one user's quick-launch section, including unavailable apps.</summary>
    /// <param name="userId">Owner of the isolated pin profile.</param>
    /// <param name="appId">Exact persisted app ID to remove.</param>
    /// <param name="cancellationToken">Signals cancellation of persistence.</param>
    /// <returns><see langword="true"/> when an existing pin was committed as removed.</returns>
    public Task<bool> UnpinAsync(
        LocalUserId userId,
        string appId,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);
        ValidateAppId(appId);
        return MutateAsync(userId, pins => pins.Remove(appId), cancellationToken);
    }

    /// <summary>Toggles one syntactically valid app ID while preserving every other ordered pin.</summary>
    /// <param name="userId">Owner of the isolated pin profile.</param>
    /// <param name="appId">Exact manifest-compatible app ID to toggle.</param>
    /// <param name="cancellationToken">Signals cancellation of persistence.</param>
    /// <returns><see langword="true"/> when the toggle was committed.</returns>
    public Task<bool> ToggleAsync(
        LocalUserId userId,
        string appId,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);
        ValidateAppId(appId);
        return MutateAsync(userId, pins =>
        {
            int existingIndex = pins.FindIndex(pin => string.Equals(pin, appId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                pins.RemoveAt(existingIndex);
                return true;
            }

            if (pins.Count >= StartMenuSettingsDocuments.MaximumPinnedAppCount)
            {
                return false;
            }

            pins.Add(appId);
            return true;
        }, cancellationToken);
    }

    /// <summary>Moves one persisted pin to a zero-based target index without disturbing other profiles.</summary>
    /// <param name="userId">Owner of the isolated pin profile.</param>
    /// <param name="appId">Exact persisted app ID to move.</param>
    /// <param name="targetIndex">Zero-based destination within the existing list.</param>
    /// <param name="cancellationToken">Signals cancellation of persistence.</param>
    /// <returns><see langword="true"/> when an order change was committed; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The destination is negative or outside an existing list.</exception>
    public Task<bool> MoveAsync(
        LocalUserId userId,
        string appId,
        int targetIndex,
        CancellationToken cancellationToken = default)
    {
        ValidateUserId(userId);
        ValidateAppId(appId);
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);

        return MutateAsync(userId, pins =>
        {
            int currentIndex = pins.FindIndex(pin => string.Equals(pin, appId, StringComparison.Ordinal));
            if (currentIndex < 0)
            {
                return false;
            }

            if (targetIndex >= pins.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(targetIndex));
            }

            if (currentIndex == targetIndex)
            {
                return false;
            }

            pins.RemoveAt(currentIndex);
            pins.Insert(targetIndex, appId);
            return true;
        }, cancellationToken);
    }

    private async Task<bool> MutateAsync(
        LocalUserId userId,
        Func<List<string>, bool> mutation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_initialized)
            {
                await RefreshCoreAsync(raiseEvent: false, cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }

            if (_session.CurrentPrincipal?.UserId != userId)
            {
                // Another user's pins live in that user's home, which this session cannot write.
                return false;
            }

            Dictionary<LocalUserId, IReadOnlyList<string>> persistedProfiles = _profiles;
            Dictionary<LocalUserId, IReadOnlyList<string>> candidate = CloneProfiles(_profiles);
            List<string> pins = candidate.TryGetValue(userId, out IReadOnlyList<string>? existing)
                ? [.. existing]
                : [];

            if (!mutation(pins))
            {
                return false;
            }

            if (pins.Count == 0)
            {
                candidate.Remove(userId);
            }
            else
            {
                candidate[userId] = Array.AsReadOnly(pins.ToArray());
            }

            // Apply before I/O for an immediately responsive shell; a failed write rolls back visibly.
            ApplyProfiles(candidate, raiseEvent: true);
            try
            {
                await _settings.WriteFileAsync(
                    StartMenuSettingsDocuments.File,
                    StartMenuSettingsCodec.Encode(candidate, _defaultPinsApplied),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Cancellation and storage failures must not leave the projection claiming an
                // uncommitted pin order.
                ApplyProfiles(persistedProfiles, raiseEvent: true);
                throw;
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> RefreshCoreAsync(bool raiseEvent, CancellationToken cancellationToken)
    {
        AuthenticatedPrincipal? principal = _session.CurrentPrincipal;
        if (principal is null)
        {
            ApplyProfiles([], raiseEvent);
            return true;
        }

        string content = await _settings.ReadValidatedFileAsync(
            StartMenuSettingsDocuments.File,
            new StartMenuSettingsValidator(),
            StartMenuSettingsDocuments.EmptyDocumentContent,
            cancellationToken).ConfigureAwait(false);
        if (!StartMenuSettingsCodec.TryDecode(content, out Dictionary<LocalUserId, IReadOnlyList<string>> profiles))
        {
            return false;
        }

        // Only the session user's own profile counts, even if the file names others.
        Dictionary<LocalUserId, IReadOnlyList<string>> own = profiles.TryGetValue(principal.UserId, out IReadOnlyList<string>? pins)
            ? new() { [principal.UserId] = pins }
            : [];
        IReadOnlyList<string> applied = StartMenuSettingsCodec.DecodeDefaultPinsApplied(content)
            .TryGetValue(principal.UserId, out IReadOnlyList<string>? done) ? done : EmptyPins;
        _defaultPinsApplied = new() { [principal.UserId] = applied };
        string[] missing = [.. _defaultPins.Where(appId => !applied.Contains(appId, StringComparer.Ordinal))];
        if (missing.Length > 0)
        {
            await ApplyDefaultPinsAsync(principal.UserId, own, applied, missing, cancellationToken).ConfigureAwait(false);
        }

        ApplyProfiles(own, raiseEvent);
        return true;
    }

    /// <summary>
    /// ADR 0212 §6 (ADR 0188 §1, Q2 A): pins the default apps not yet added to this profile, after its
    /// own pins, and records them in <c>defaultPinsApplied</c> so an unpin is kept. A new profile
    /// starts with them; an existing one gets them once. A failed write keeps them in memory only,
    /// and the next load tries again.
    /// </summary>
    private async Task ApplyDefaultPinsAsync(
        LocalUserId userId,
        Dictionary<LocalUserId, IReadOnlyList<string>> own,
        IReadOnlyList<string> applied,
        IReadOnlyList<string> missing,
        CancellationToken cancellationToken)
    {
        List<string> pins = own.TryGetValue(userId, out IReadOnlyList<string>? existing) ? [.. existing] : [];
        foreach (string appId in missing)
        {
            if (!pins.Contains(appId, StringComparer.Ordinal) && pins.Count < StartMenuSettingsDocuments.MaximumPinnedAppCount)
            {
                pins.Add(appId);
            }
        }

        if (pins.Count > 0)
        {
            own[userId] = Array.AsReadOnly(pins.ToArray());
        }

        _defaultPinsApplied = new() { [userId] = Array.AsReadOnly(applied.Concat(missing).ToArray()) };
        try
        {
            await _settings.WriteFileAsync(
                StartMenuSettingsDocuments.File,
                StartMenuSettingsCodec.Encode(own, _defaultPinsApplied),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Shown for this session; written at the next load or the next pin change.
        }
    }

    private void OnDocumentChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        if (args.Concerns(StartMenuSettingsDocuments.File.RelativePath))
        {
            _ = RefreshInBackgroundAsync();
        }
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await RefreshCoreAsync(raiseEvent: true, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            // Keep the last good pins.
        }
    }

    private void ApplyProfiles(Dictionary<LocalUserId, IReadOnlyList<string>> profiles, bool raiseEvent)
    {
        bool changed = !ProfilesEqual(_profiles, profiles);
        _profiles = profiles;
        if (raiseEvent && changed)
        {
            Changed?.Invoke();
        }
    }

    private static Dictionary<LocalUserId, IReadOnlyList<string>> CloneProfiles(
        IReadOnlyDictionary<LocalUserId, IReadOnlyList<string>> profiles) =>
        profiles.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)Array.AsReadOnly(pair.Value.ToArray()));

    private static bool ProfilesEqual(
        IReadOnlyDictionary<LocalUserId, IReadOnlyList<string>> left,
        IReadOnlyDictionary<LocalUserId, IReadOnlyList<string>> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach ((LocalUserId userId, IReadOnlyList<string> leftPins) in left)
        {
            if (!right.TryGetValue(userId, out IReadOnlyList<string>? rightPins)
                || !leftPins.SequenceEqual(rightPins, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateUserId(LocalUserId userId)
    {
        if (userId.Value == Guid.Empty)
        {
            throw new ArgumentException("A non-empty local user ID is required.", nameof(userId));
        }
    }

    private static void ValidateAppId(string appId)
    {
        if (!StartMenuAppIdSyntax.IsValid(appId))
        {
            throw new ArgumentException(
                "A lowercase reverse-domain app ID with at least three segments is required.",
                nameof(appId));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _settings.DocumentChanged -= OnDocumentChanged;
        _gate.Dispose();
    }
}
