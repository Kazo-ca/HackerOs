using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Lifecycle;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>Represents a resolved command target from the AppCatalog.</summary>
public sealed record ShellCommandResolution(
    string AppId,
    AppManifest Manifest,
    string CommandName,
    IReadOnlyList<string> Arguments);

/// <summary>
/// Resolves executable commands and static catalog aliases against registered catalog manifests
/// (`P2-TERM-004`, `P2-TERM-004A`): the shell's equivalent of a <c>PATH</c> lookup. Used by
/// <see cref="ShellInterpreter"/> to run a command and by the terminal for <c>help</c> and completion.
/// </summary>
public sealed class ShellCommandResolver
{
    private readonly AppCatalog _catalog;
    private readonly IAppEnablementRegistry? _enablement;

    /// <summary>Initializes a command resolver over the application catalog.</summary>
    /// <param name="catalog">Application catalog.</param>
    /// <param name="enablement">
    /// Optional launch checkpoint: an uninstalled command (ADR 0048) is then not resolved, so the
    /// shell answers <c>command not found</c> like a real shell whose binary was removed.
    /// </param>
    public ShellCommandResolver(AppCatalog catalog, IAppEnablementRegistry? enablement = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _enablement = enablement;
    }

    /// <summary>Attempts to resolve a command line or command name to an installed terminal app manifest.</summary>
    public ShellCommandResolution? Resolve(string commandName, IReadOnlyList<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return null;
        }

        // ADR 0098 §4: a command name belongs to one app (the catalog decided which at load).
        if (_catalog.FindCommandOwner(commandName) is { } owner
            && _catalog.Manifests.TryGetValue(owner, out AppManifest? owned)
            && IsInstalledCommand(owned))
        {
            return new ShellCommandResolution(owned.Id, owned, commandName, arguments);
        }

        // A terminal app can also be run by its full id.
        if (_catalog.Manifests.Values.FirstOrDefault(manifest =>
                IsInstalledCommand(manifest) && string.Equals(manifest.Id, commandName, StringComparison.OrdinalIgnoreCase)) is { } byId)
        {
            return new ShellCommandResolution(byId.Id, byId, commandName, arguments);
        }

        return null;
    }

    /// <summary>
    /// Finds a window app by its short name (ADR 0064): the last segment of its id, without the
    /// publisher namespace (<c>text-editor</c> for <c>org.hackeros.text-editor</c>), or its full
    /// id. Used by the shell when no command has the name, like a GUI program found on
    /// <c>PATH</c>.
    /// </summary>
    /// <param name="name">The name typed as the command.</param>
    /// <returns>The app's manifest, or <see langword="null"/> when none (or several) match.</returns>
    public AppManifest? ResolveApplication(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/'))
        {
            return null;
        }

        List<AppManifest> matches = [.. _catalog.Manifests.Values.Where(manifest =>
            manifest.Kind == AppKind.Window
            && (_enablement is null || _enablement.IsEnabled(manifest.Id))
            && (string.Equals(manifest.Id, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ShortName(manifest.Id), name, StringComparison.OrdinalIgnoreCase)))];
        return matches.Count == 1 ? matches[0] : matches.FirstOrDefault(m => string.Equals(m.Id, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Gets an app id without its namespace: the text after its last dot.</summary>
    public static string ShortName(string appId) =>
        appId.LastIndexOf('.') is var dot and >= 0 ? appId[(dot + 1)..] : appId;

    /// <summary>Gets the window apps the shell can start by short name, ordered by that name (completion).</summary>
    public IReadOnlyList<AppManifest> GetApplications() =>
        [.. _catalog.Manifests.Values
            .Where(manifest => manifest.Kind == AppKind.Window && (_enablement is null || _enablement.IsEnabled(manifest.Id)))
            .OrderBy(manifest => ShortName(manifest.Id), StringComparer.OrdinalIgnoreCase)];

    /// <summary>Gets every registered terminal command manifest, ordered by command name.</summary>
    public IReadOnlyList<AppManifest> GetTerminalCommands() =>
        _catalog.Manifests.Values
            .Where(IsInstalledCommand)
            .OrderBy(m => m.Terminal!.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private bool IsInstalledCommand(AppManifest manifest) =>
        manifest.Kind == AppKind.Terminal
        && manifest.Terminal is not null
        && (_enablement is null || _enablement.IsEnabled(manifest.Id))
        // ADR 0098 §4: an app whose every command name went to another app registers no command.
        && manifest.Terminal.Aliases.Prepend(manifest.Terminal.Name).Any(name => _catalog.FindCommandOwner(name) == manifest.Id);
}
