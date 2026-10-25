using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Shell;

/// <summary>Whether the active platform follows automatic detection or an explicit user choice.</summary>
public enum UiPlatformSelectionSource
{
    /// <summary>Follows <see cref="IPlatformEnvironmentProbe"/>.</summary>
    Auto,

    /// <summary>Follows the persisted <see cref="UiPlatformPreferenceState.ExplicitPlatformId"/>.</summary>
    Explicit
}

/// <summary>Live UI-platform preference state.</summary>
/// <param name="SelectionSource">Whether the choice is automatic or explicit.</param>
/// <param name="ExplicitPlatformId">The explicit platform, when <see cref="SelectionSource"/> is <see cref="UiPlatformSelectionSource.Explicit"/>.</param>
/// <param name="ActivePlatform">
/// The platform effective right now: <see cref="ExplicitPlatformId"/> when explicit, otherwise the
/// environment probe's current suggestion.
/// </param>
public sealed record UiPlatformPreferenceState(
    UiPlatformSelectionSource SelectionSource,
    AppPlatformId? ExplicitPlatformId,
    AppPlatformId ActivePlatform);

/// <summary>
/// Owns the layered <c>ui-platform-preference</c> settings document (see
/// <see cref="UiPlatformPreferenceSettingsDocuments"/>) and exposes it live to the shell.
/// </summary>
/// <remarks>
/// <para>
/// This is a first Phase-0 slice of doc/en/wasm/dev/old/mobile-interface-platform-plan.md §6 — it persists the
/// Auto/Desktop/Mobile choice and recomputes <see cref="UiPlatformPreferenceState.ActivePlatform"/>
/// live, but does not itself change shell rendering. A consumer subscribes to <see cref="Changed"/>
/// and drives the switch sequence.
/// </para>
/// <para>
/// The choice is a file (ADR 0044): the machine default in
/// <c>/etc/hackeros/ui-platform-preference.config</c>, the session user's own choice in
/// <c>~/.config/hackeros/ui-platform-preference.config</c>. The shell writes the user layer with the
/// user's own rights through <see cref="IOsSettingsService"/>.
/// </para>
/// </remarks>
public sealed class UiPlatformPreferenceService : IDisposable
{
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly IOsSettingsService _settings;
    private readonly IPlatformEnvironmentProbe _probe;

    private UiPlatformSelectionSource _selectionSource = UiPlatformSelectionSource.Auto;
    private AppPlatformId? _explicitPlatformId;

    /// <summary>Creates the preference service over the OS settings files and the environment probe.</summary>
    public UiPlatformPreferenceService(IOsSettingsService settings, IPlatformEnvironmentProbe probe)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _probe.Changed += OnProbeChanged;
        _settings.DocumentChanged += OnDocumentChanged;
    }

    /// <summary>Gets the current preference state. Reflects the document's clean-profile default until <see cref="InitializeAsync"/> completes.</summary>
    public UiPlatformPreferenceState Current { get; private set; } =
        new(UiPlatformSelectionSource.Auto, null, WellKnownAppPlatforms.Desktop);

    /// <summary>Raised when <see cref="Current"/> changes, whether from a user choice, a file edit, or the environment probe.</summary>
    public event Action? Changed;

    /// <summary>Loads the persisted preference, starts the environment probe and watches the files.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _probe.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await ReloadAsync(raiseEvent: false, cancellationToken).ConfigureAwait(false);
        await _settings.WatchAsync(UiPlatformPreferenceSettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets an explicit platform choice and persists it in the user layer.</summary>
    public Task SetExplicitAsync(AppPlatformId platformId, CancellationToken cancellationToken = default) =>
        WriteAsync(UiPlatformSelectionSource.Explicit, platformId, cancellationToken);

    /// <summary>Clears any explicit choice and reverts to automatic detection.</summary>
    public Task ClearToAutoAsync(CancellationToken cancellationToken = default) =>
        WriteAsync(UiPlatformSelectionSource.Auto, null, cancellationToken);

    private async Task WriteAsync(
        UiPlatformSelectionSource selectionSource,
        AppPlatformId? explicitPlatformId,
        CancellationToken cancellationToken)
    {
        OsSettingsDocument document = UiPlatformPreferenceSettingsDocuments.Document;
        try
        {
            // Both keys in one edit, so a reader never sees "explicit" with the previous platform.
            await _settings.SetManyAsync(
                document,
                [
                    new KeyValuePair<string, string>(
                        UiPlatformPreferenceSettingsDocuments.SelectionSourceKey,
                        selectionSource == UiPlatformSelectionSource.Explicit ? "explicit" : "auto"),
                    new KeyValuePair<string, string>(
                        UiPlatformPreferenceSettingsDocuments.ExplicitPlatformKey,
                        explicitPlatformId?.Value ?? string.Empty)
                ],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // No session yet, or an administrator locked the choice: keep what the files say.
        }
        catch (UnauthorizedAccessException)
        {
            // The user may not write the file: keep what the files say.
        }

        await ReloadAsync(raiseEvent: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads and applies the files under a gate: a change notification arriving mid-write would
    /// otherwise let a stale load interleave with the fresh one and drop the new choice.
    /// </summary>
    private async Task ReloadAsync(bool raiseEvent, CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool changed;
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            changed = Recompute();
        }
        finally
        {
            _reloadGate.Release();
        }

        if (raiseEvent && changed)
        {
            Changed?.Invoke();
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        LayeredConfigSnapshot snapshot = await _settings
            .ReadAsync(UiPlatformPreferenceSettingsDocuments.Document, cancellationToken)
            .ConfigureAwait(false);
        _selectionSource = snapshot.Values[UiPlatformPreferenceSettingsDocuments.SelectionSourceKey] == "explicit"
            ? UiPlatformSelectionSource.Explicit
            : UiPlatformSelectionSource.Auto;
        _explicitPlatformId = _selectionSource == UiPlatformSelectionSource.Explicit
            && AppPlatformId.TryParse(snapshot.Values[UiPlatformPreferenceSettingsDocuments.ExplicitPlatformKey], out AppPlatformId parsed)
                ? parsed
                : null;
    }

    private void OnDocumentChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        if (args.Concerns(UiPlatformPreferenceSettingsDocuments.Document.DocumentId))
        {
            _ = ReloadInBackgroundAsync();
        }
    }

    private async Task ReloadInBackgroundAsync()
    {
        try
        {
            await ReloadAsync(raiseEvent: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Keep the last good state; the settings service already logged the read problem.
        }
    }

    private void OnProbeChanged()
    {
        if (Recompute())
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Recomputes <see cref="Current"/>; returns whether it changed.</summary>
    private bool Recompute()
    {
        AppPlatformId active = _selectionSource == UiPlatformSelectionSource.Explicit && _explicitPlatformId is { } explicitId
            ? explicitId
            : _probe.Current.SuggestedPlatform;

        UiPlatformPreferenceState next = new(_selectionSource, _explicitPlatformId, active);
        bool changed = next != Current;
        Current = next;
        return changed;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _probe.Changed -= OnProbeChanged;
        _settings.DocumentChanged -= OnDocumentChanged;
        _reloadGate.Dispose();
    }
}
