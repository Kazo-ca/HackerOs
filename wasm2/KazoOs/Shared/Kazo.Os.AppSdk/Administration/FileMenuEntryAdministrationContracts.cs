namespace Kazo.Os.AppSdk.Administration;

/// <summary>One entry an app added to the file menus (ADR 0151), as Settings lists it (ADR 0159 §8).</summary>
/// <param name="AppId">The app that registered it.</param>
/// <param name="AppName">That app's display name.</param>
/// <param name="Name">The entry's short id, unique within the app.</param>
/// <param name="Title">Its title in the caller's language.</param>
/// <param name="Parent">The entry whose submenu holds it, or <see langword="null"/> at the top of the menu.</param>
/// <param name="Scopes">Where it shows: <c>file</c>, <c>directory</c>, <c>background</c>, <c>desktop</c>.</param>
/// <param name="IsHidden">Whether the session user hid it (a hidden parent hides its submenu too).</param>
public sealed record HackFileMenuEntryInfo(
    string AppId,
    string AppName,
    string Name,
    string Title,
    string? Parent,
    IReadOnlyList<string> Scopes,
    bool IsHidden);

/// <summary>
/// Trusted OS gateway behind <see cref="HackFileMenuEntries"/> (ADR 0159 §8). Every member requires
/// the capability <c>apps.manage</c>.
/// </summary>
public interface IAppFileMenuEntryAdministrationGateway
{
    /// <summary>Lists the entries every installed app registered, in installation rank then declaration order.</summary>
    /// <param name="cancellationToken">Cancels the read of the user's preferences.</param>
    ValueTask<IReadOnlyList<HackFileMenuEntryInfo>> GetEntriesAsync(CancellationToken cancellationToken = default);

    /// <summary>Hides or shows one entry in the session user's file menus.</summary>
    /// <param name="appId">The app that registered it.</param>
    /// <param name="name">The entry's short id.</param>
    /// <param name="hidden">Whether to hide it.</param>
    /// <param name="cancellationToken">Cancels the read and the write.</param>
    ValueTask SetHiddenAsync(string appId, string name, bool hidden, CancellationToken cancellationToken = default);
}
