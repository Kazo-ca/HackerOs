namespace Kazo.Os.AppSdk.Settings;

/// <summary>Describes a change of effective settings values, raised by <see cref="KazoSettingsDocument.Changed"/>.</summary>
public sealed class KazoSettingsChangedEventArgs : EventArgs
{
    /// <summary>Creates the event arguments.</summary>
    /// <param name="changedKeys">Qualified keys whose effective value, source layer or lock state changed.</param>
    public KazoSettingsChangedEventArgs(IReadOnlyList<string> changedKeys)
    {
        ChangedKeys = changedKeys ?? throw new ArgumentNullException(nameof(changedKeys));
    }

    /// <summary>Gets the qualified keys whose effective value, source layer or lock state changed.</summary>
    public IReadOnlyList<string> ChangedKeys { get; }
}
