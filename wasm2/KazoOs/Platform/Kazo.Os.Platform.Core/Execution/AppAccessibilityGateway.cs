using Kazo.Os.AppSdk.Accessibility;
using Kazo.Os.Platform.Core.Appearance;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Trusted <see cref="IAppAccessibilityGateway"/> (ADR 0105 section 3): one app instance's view of
/// the session's <see cref="AccessibilityPreferenceService"/>. No capability is checked: the
/// preferences say how to draw, nothing about the user's data.
/// </summary>
internal sealed class AppAccessibilityGateway : IAppAccessibilityGateway
{
    private readonly AccessibilityPreferenceService _preferences;
    private readonly AppAmbientContextBinding _ambient = new();
    private readonly Lock _gate = new();
    private EventHandler? _changed;

    /// <summary>Creates the gateway for one app instance.</summary>
    /// <param name="preferences">The session's accessibility preferences.</param>
    public AppAccessibilityGateway(AccessibilityPreferenceService preferences)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
    }

    /// <inheritdoc />
    public event EventHandler? Changed
    {
        // The service is subscribed only while the app listens, so a closed app leaves nothing behind
        // once its components unsubscribed.
        add
        {
            lock (_gate)
            {
                bool first = _changed is null;
                _changed += value;
                if (first && _changed is not null)
                {
                    _preferences.Changed += OnPreferencesChanged;
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                bool had = _changed is not null;
                _changed -= value;
                if (had && _changed is null)
                {
                    _preferences.Changed -= OnPreferencesChanged;
                }
            }
        }
    }

    /// <summary>Binds the app context <see cref="Changed"/> handlers run under; called once by the factory.</summary>
    internal void BindAmbientContext(Kazo.Os.AppSdk.IAppExecutionContext context) => _ambient.Bind(context);

    /// <inheritdoc />
    public ValueTask<KazoAccessibilityPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_preferences.Effective);
    }

    private void OnPreferencesChanged()
    {
        EventHandler? handlers;
        lock (_gate)
        {
            handlers = _changed;
        }

        if (handlers is not null)
        {
            _ambient.Wrap(() => handlers(this, EventArgs.Empty))();
        }
    }
}
