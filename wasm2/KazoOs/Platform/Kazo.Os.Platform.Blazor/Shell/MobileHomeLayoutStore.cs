using Kazo.Os.Platform.Core.Settings;

namespace Kazo.Os.Platform.Blazor.Shell;

/// <summary>Serializes Home edits to the session user's hidden VFS configuration, never browser storage.</summary>
internal sealed class MobileHomeLayoutStore(IOsSettingsService settings)
{
    internal static readonly OsSettingsFile File = new(OsSettingsLocation.UserHome, "Desktop/.mobile/layout.conf");
    internal static readonly OsSettingsFile LegacyFile = new(OsSettingsLocation.UserConfig, ".mobile/layout.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _home;
    internal MobileHomeLayout Current { get; private set; } = MobileHomeLayout.Default();

    internal async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            string? home = settings.CurrentHomeDirectory;
            string? content = await settings.ReadFileAsync(File);
            MobileHomeLayout? legacy = content is null
                ? MobileHomeLayout.DecodeLegacyJson(await settings.ReadFileAsync(LegacyFile)) : null;
            MobileHomeLayout layout = MobileHomeLayout.Decode(content) ?? legacy ?? MobileHomeLayout.Default();
            if (home != settings.CurrentHomeDirectory) throw new InvalidOperationException("The session changed while loading Home.");
            _home = home;
            Current = layout;
            // The new file always wins, even when corrupt. Migrate only an absent destination,
            // keeping the old file untouched so a failed write never loses the user's layout.
            if (legacy is not null)
            {
                if (_home is null) throw new InvalidOperationException("A user settings file needs an active session.");
                await settings.WriteFileAsync(File, layout.Encode());
                if (_home != settings.CurrentHomeDirectory) throw new InvalidOperationException("The session changed while migrating Home.");
            }
        }
        finally { _gate.Release(); }
    }

    internal async Task<bool> EditAsync(Func<MobileHomeLayout, bool> edit)
    {
        await _gate.WaitAsync();
        try
        {
            if (_home is null || _home != settings.CurrentHomeDirectory)
                throw new InvalidOperationException("The Home layout belongs to another session.");
            MobileHomeLayout next = Current.Clone();
            if (!edit(next)) return false;
            string content = next.Encode();
            if (MobileHomeLayout.Decode(content) is null) throw new InvalidOperationException("Invalid Home layout.");
            // Publish only after the single atomic file replacement succeeds. Failed writes retain
            // the last good projection and cannot turn into a queue of saves on pointer movement.
            await settings.WriteFileAsync(File, content);
            if (_home != settings.CurrentHomeDirectory) throw new InvalidOperationException("The session changed while saving Home.");
            Current = next;
            return true;
        }
        finally { _gate.Release(); }
    }
}
