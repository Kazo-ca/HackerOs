using System.Globalization;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Blazor.ContextMenus;
using HackerOs.AppSdk.Blazor.Files;
using HackerOs.AppSdk.Files;
using HackerOs.AppSdk.Icons;
using HackerOs.Platform.Core;
using HackerOs.Platform.Core.ContextMenus;
using Microsoft.AspNetCore.Components;

namespace HackerOs.Platform.Blazor.ContextMenus;

/// <summary>
/// Adds the entries apps registered (ADR 0151) to a file menu the OS is about to draw (ADR 0159 §5).
/// The context menu service calls it after the menu's own <c>Opening</c> handler, for every menu whose
/// <see cref="HackMenuDefinition.FileMenu"/> the provider of the file control set: the entries are
/// therefore the same whichever app provides the control, in the explorer, on the desktop, in the
/// file dialogs and in any app that shows <c>HackFileView</c>.
/// </summary>
public interface IFileMenuEntrySource
{
    /// <summary>Places the registered entries that apply to <paramref name="menu"/>'s file context.</summary>
    /// <param name="menu">This opening's copy of the menu; <see cref="HackMenuDefinition.FileMenu"/> is set.</param>
    /// <param name="cancellationToken">Cancels the read of the user's preferences.</param>
    Task ApplyAsync(HackMenuDefinition menu, CancellationToken cancellationToken = default);
}

/// <summary>
/// The OS side of the registered file menu entries: filters the entries of
/// <see cref="FileContextMenuRegistry"/> (scope, types, selection, open screens, apps installed,
/// entries the user hid), places them (<see cref="FileMenuEntryPlacement"/>) and binds each one to
/// <see cref="FileContextMenuLauncher"/>.
/// </summary>
public sealed class FileMenuEntries : IFileMenuEntrySource
{
    private readonly AppCatalog _catalog;
    private readonly FileContextMenuRegistry _registry;
    private readonly FileContextMenuLauncher _launcher;
    private readonly Func<CancellationToken, ValueTask<IReadOnlySet<string>>> _hidden;
    private readonly Func<CultureInfo> _culture;
    private readonly Func<string, AppContextMenuEntryManifest, string, IReadOnlyList<string>, Task> _launch;

    /// <summary>Creates the source.</summary>
    /// <param name="catalog">The catalog, for the assembly of an image icon.</param>
    /// <param name="registry">The registered entries, read from <c>/etc/hackeros/context-menus.d</c>.</param>
    /// <param name="launcher">Decides which entries can run.</param>
    /// <param name="hidden">Reads the entries the session user hid (<c>&lt;appId&gt;.&lt;name&gt;</c> keys).</param>
    /// <param name="culture">The language of the titles.</param>
    /// <param name="launch">Runs a click: owner app, entry, payload scope, paths.</param>
    public FileMenuEntries(
        AppCatalog catalog,
        FileContextMenuRegistry registry,
        FileContextMenuLauncher launcher,
        Func<CancellationToken, ValueTask<IReadOnlySet<string>>> hidden,
        Func<CultureInfo> culture,
        Func<string, AppContextMenuEntryManifest, string, IReadOnlyList<string>, Task> launch)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _hidden = hidden ?? throw new ArgumentNullException(nameof(hidden));
        _culture = culture ?? throw new ArgumentNullException(nameof(culture));
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
    }

    /// <summary>
    /// Gets the numbers of the screens open now, for the screen filter of ADR 0218 §2; screen 1 alone
    /// when the host has no extended desktop.
    /// </summary>
    public Func<IReadOnlySet<int>> OpenScreens { get; init; } = static () => SingleScreen;

    private static readonly IReadOnlySet<int> SingleScreen = new HashSet<int> { 1 };

    /// <inheritdoc />
    public async Task ApplyAsync(HackMenuDefinition menu, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menu);
        if (menu.FileMenu is not { } context || _registry.Apps.Count == 0)
        {
            return;
        }

        IReadOnlySet<string> hidden = await _hidden(cancellationToken).ConfigureAwait(false);
        string scope = ScopeName(context.Scope);
        FileMenuTarget[] targets = [.. context.Items.Select(static item => new FileMenuTarget(item.Path, item.IsDirectory, item.MediaType))];
        IReadOnlyList<string> paths = context.Items.Count > 0 ? [.. context.Items.Select(static item => item.Path)] : [context.Directory];
        string payloadScope = context.Scope.ToString();
        CultureInfo culture = _culture();
        IReadOnlySet<int> screens = OpenScreens();

        List<FileMenuEntryPlacement.Candidate> candidates = [];
        foreach (RegisteredFileMenuApp app in _registry.Apps)
        {
            bool Visible(AppContextMenuEntryManifest entry) =>
                !hidden.Contains(FileContextMenuPreferences.KeyOf(app.AppId, entry.Name))
                && FileContextMenuMatcher.Matches(entry, scope, targets)
                && FileContextMenuMatcher.MatchesScreens(entry, screens)
                && _launcher.IsAvailable(app.AppId, entry);

            foreach (AppContextMenuEntryManifest entry in app.Entries.Where(static entry => entry.Parent is null))
            {
                if (Visible(entry)
                    && Build(app, entry, Visible, payloadScope, paths, culture, depth: 1) is { } item)
                {
                    candidates.Add(new FileMenuEntryPlacement.Candidate(app.AppId, entry, item));
                }
            }
        }

        FileMenuEntryPlacement.Place(menu, candidates);
    }

    /// <summary>The id of a registered entry in a menu, unique across apps: <c>app:&lt;appId&gt;/&lt;name&gt;</c>.</summary>
    /// <param name="appId">The app.</param>
    /// <param name="name">The entry's name.</param>
    public static string IdOf(string appId, string name) => $"app:{appId}/{name}";

    /// <summary>The matcher's scope name of a menu.</summary>
    /// <param name="scope">The menu's scope.</param>
    public static string ScopeName(FileMenuScope scope) => scope switch
    {
        FileMenuScope.Directory => AppContextMenus.DirectoryScope,
        FileMenuScope.Background => AppContextMenus.BackgroundScope,
        FileMenuScope.Desktop => AppContextMenus.DesktopScope,
        _ => AppContextMenus.FileScope
    };

    /// <summary>Converts a registered icon to the menu's description.</summary>
    /// <param name="assembly">The app's entry-point assembly, whose static assets (<c>_content/&lt;assembly&gt;/</c>) an image path is relative to.</param>
    /// <param name="icon">The registered icon.</param>
    public static HackIcon? IconOf(string? assembly, AppContextMenuIconManifest? icon)
    {
        if (icon is null)
        {
            return null;
        }

        if (icon.Kind == AppContextMenus.VectorIcon)
        {
            IconLibrary library = icon.Library switch
            {
                "bootstrap" => IconLibrary.Bootstrap,
                "fontawesome" => IconLibrary.FontAwesome,
                "simpleicons" => IconLibrary.SimpleIcons,
                _ => IconLibrary.Lucide
            };
            return HackIcons.Vector(library, icon.Source);
        }

        // An asset path is relative to the app's wwwroot/, published as _content/<assembly>/ (ADR 0098 §5); a URI is kept.
        string source = icon.Source.Contains(':', StringComparison.Ordinal) || assembly is null
            ? icon.Source
            : $"_content/{assembly}/{icon.Source.TrimStart('/')}";
        return HackIcons.FromImage(source);
    }

    private string? AssemblyOf(string appId) => _catalog.Manifests.TryGetValue(appId, out AppManifest? manifest)
        ? manifest.EntryPoint?.Assembly ?? manifest.Platform?.EntryPoints.FirstOrDefault()?.Assembly
        : null;

    /// <summary>Builds the menu item of an entry and its visible submenu; <see langword="null"/> for a submenu left empty.</summary>
    private HackMenuItemDefinition? Build(
        RegisteredFileMenuApp app,
        AppContextMenuEntryManifest entry,
        Func<AppContextMenuEntryManifest, bool> visible,
        string payloadScope,
        IReadOnlyList<string> paths,
        CultureInfo culture,
        int depth)
    {
        HackMenuItemDefinition item = new(IdOf(app.AppId, entry.Name), FileContextMenuMatcher.TitleOf(entry, culture))
        {
            Icon = IconOf(AssemblyOf(app.AppId), entry.Icon)
        };

        AppContextMenuEntryManifest[] children = [.. app.Entries.Where(child => child.Parent == entry.Name)];
        if (children.Length == 0)
        {
            item.OnClick = new EventCallback<object?>(null, (Func<object?, Task>)(_ => _launch(app.AppId, entry, payloadScope, paths)));
            return item;
        }

        if (depth >= AppContextMenus.MaxDepth)
        {
            return null;
        }

        HackMenuGroupDefinition group = new();
        foreach (AppContextMenuEntryManifest child in children.Where(visible))
        {
            if (Build(app, child, visible, payloadScope, paths, culture, depth + 1) is { } childItem)
            {
                group.Items.Add(childItem);
            }
        }

        // A parent whose children are all filtered out disappears (ADR 0150 §3).
        if (group.Items.Count == 0)
        {
            return null;
        }

        item.Children.Add(group);
        return item;
    }
}
