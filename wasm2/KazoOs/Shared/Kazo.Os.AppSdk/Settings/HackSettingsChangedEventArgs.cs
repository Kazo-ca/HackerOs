namespace Kazo.Os.AppSdk.Settings;

/// <summary>Describes a change of effective settings values, raised by <see cref="HackSettingsDocument.Changed"/>.</summary>
public sealed class HackSettingsChangedEventArgs : EventArgs
{
    /// <summary>Creates the event arguments.</summary>
    /// <param name="changedKeys">Qualified keys whose effective value, source layer or lock state changed.</param>
    public HackSettingsChangedEventArgs(IReadOnlyList<string> changedKeys)
    {
        ChangedKeys = changedKeys ?? throw new ArgumentNullException(nameof(changedKeys));
    }

    /// <summary>Gets the qualified keys whose effective value, source layer or lock state changed.</summary>
    public IReadOnlyList<string> ChangedKeys { get; }
}
