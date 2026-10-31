using System.Text.RegularExpressions;

namespace HackerOs.App.Abstractions;

/// <summary>
/// Validates application manifests before build-time registration or installation.
/// </summary>
public static partial class AppManifestValidator
{
    private const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Validates a manifest without loading or executing its application assembly.
    /// </summary>
    /// <param name="manifest">Manifest to validate.</param>
    /// <returns>Every validation failure found in deterministic order.</returns>
    public static ManifestValidationResult Validate(AppManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        List<ManifestValidationError> errors = [];

        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            AddError(errors, "manifest.schema.unsupported", "schemaVersion", "Only manifest schema version 1 is supported.");
        }

        ValidateAppId(manifest.Id, "id", errors);
        RequireText(manifest.Name, "name", errors);
        ValidateSemanticVersion(manifest.Version, "version", errors);
        ValidatePublisherId(manifest.PublisherId, errors);
        RequireText(manifest.Description, "description", errors);
        ValidatePlatformAndEntryPoint(manifest, errors);
        ValidateVersionRange(
            manifest.SdkCompatibility.MinimumVersion,
            manifest.SdkCompatibility.MaximumVersion,
            "sdkCompatibility",
            errors);
        if (manifest.OsCompatibility is not null)
        {
            ValidateVersionRange(
                manifest.OsCompatibility.MinimumVersion,
                manifest.OsCompatibility.MaximumVersion,
                "osCompatibility",
                errors);
        }

        ValidateCapabilities(manifest.Capabilities, errors);
        ValidateCapabilityKindCompatibility(manifest.Kind, manifest.Capabilities, errors);
        ValidateDeclaredTopicPermissions(manifest.Id, manifest.DeclaredTopicPermissions, errors);
        ValidateDependencies(manifest.Dependencies, errors);
        ValidateAssets(manifest.Assets, errors);
        ValidatePresentation(manifest.Presentation, manifest.Assets, errors);
        ValidateLocalizations(manifest.Localizations, errors);
        ValidateResources(manifest.Resources, errors);
        if (manifest.Settings is not null)
        {
            ValidateSettingsSchema(manifest.Settings, errors);
        }

        ValidateIntents(manifest.Intents, manifest.Assets, errors);
        if (manifest.Update is not null)
        {
            ValidateUpdate(manifest.Update, errors);
        }

        if (manifest.AutoStart && manifest.Kind != AppKind.Service)
        {
            AddError(errors, "manifest.autoStart.forbidden", "autoStart", "Only service apps can declare autoStart.");
        }

        if (manifest.ServiceStartup is not null && manifest.Kind != AppKind.Service)
        {
            // ADR 0097 section 4: boot or login only means something for a service.
            AddError(errors, "manifest.serviceStartup.forbidden", "serviceStartup", "Only service apps can declare serviceStartup.");
        }

        if (manifest.ServiceStartup is { } startup && !Enum.IsDefined(startup))
        {
            AddError(errors, "manifest.serviceStartup.invalid", "serviceStartup", "serviceStartup must be 'login' or 'boot'.");
        }

        ValidateKindSpecificFields(manifest, errors);

        return new ManifestValidationResult(errors);
    }

    /// <summary>
    /// Validates the mutually exclusive <see cref="AppManifest.EntryPoint"/> (legacy) /
    /// <see cref="AppManifest.Platform"/> (<c>MOB-003</c>) declaration, per plan §4.2.
    /// </summary>
    private static void ValidatePlatformAndEntryPoint(AppManifest manifest, List<ManifestValidationError> errors)
    {
        bool hasEntryPoint = manifest.EntryPoint is not null;
        bool hasPlatform = manifest.Platform is not null;

        if (!hasEntryPoint && !hasPlatform)
        {
            AddError(errors, "manifest.platform.required", "entryPoint", "Either 'entryPoint' or 'platform' must be declared.");
            return;
        }

        if (hasEntryPoint && hasPlatform)
        {
            AddError(errors, "manifest.platform.ambiguous", "platform", "Declare either 'entryPoint' or 'platform', not both.");
            return;
        }

        if (hasEntryPoint)
        {
            RequireText(manifest.EntryPoint!.Assembly, "entryPoint.assembly", errors);
            RequireText(manifest.EntryPoint!.Type, "entryPoint.type", errors);
            return;
        }

        ValidatePlatform(manifest.Platform!, errors);
    }

    private static void ValidatePlatform(AppManifestPlatform platform, List<ManifestValidationError> errors)
    {
        if (platform.Supported is null || platform.Supported.Count == 0)
        {
            AddError(errors, "manifest.platform.supported.empty", "platform.supported", "At least one supported platform is required.");
            return;
        }

        ValidateUniqueValues(platform.Supported, "platform.supported", errors);

        HashSet<AppPlatformId> supportedPlatforms = [];
        foreach (string rawPlatformId in platform.Supported)
        {
            if (!AppPlatformId.TryParse(rawPlatformId, out AppPlatformId platformId))
            {
                AddError(errors, "manifest.platform.id.invalid", "platform.supported", $"'{rawPlatformId}' is not a valid platform identifier.");
                continue;
            }

            supportedPlatforms.Add(platformId);
        }

        if (platform.EntryPoints is null || platform.EntryPoints.Count == 0)
        {
            AddError(errors, "manifest.platform.entryPoints.empty", "platform.entryPoints", "At least one entry point is required.");
            return;
        }

        Dictionary<AppPlatformId, int> coverage = [];
        foreach (AppPlatformEntryPointManifest entryPoint in platform.EntryPoints)
        {
            RequireText(entryPoint.Assembly, "platform.entryPoints.assembly", errors);
            RequireText(entryPoint.Type, "platform.entryPoints.type", errors);

            if (entryPoint.Platforms is null || entryPoint.Platforms.Count == 0)
            {
                AddError(errors, "manifest.platform.entryPoint.platforms.empty", "platform.entryPoints.platforms", "An entry point must cover at least one platform.");
                continue;
            }

            foreach (string rawPlatformId in entryPoint.Platforms)
            {
                if (!AppPlatformId.TryParse(rawPlatformId, out AppPlatformId platformId))
                {
                    AddError(errors, "manifest.platform.id.invalid", "platform.entryPoints.platforms", $"'{rawPlatformId}' is not a valid platform identifier.");
                    continue;
                }

                if (!supportedPlatforms.Contains(platformId))
                {
                    AddError(
                        errors,
                        "manifest.platform.entryPoint.platform.unsupported",
                        "platform.entryPoints.platforms",
                        $"Entry point references platform '{platformId}', which is not in 'platform.supported'.");
                    continue;
                }

                coverage[platformId] = coverage.GetValueOrDefault(platformId) + 1;
            }
        }

        foreach ((AppPlatformId platformId, int entryPointCount) in coverage)
        {
            if (entryPointCount > 1)
            {
                AddError(
                    errors,
                    "manifest.platform.entryPoint.platform.duplicate",
                    "platform.entryPoints",
                    $"Platform '{platformId}' is covered by more than one entry point.");
            }
        }

        foreach (AppPlatformId platformId in supportedPlatforms)
        {
            if (!coverage.ContainsKey(platformId))
            {
                AddError(
                    errors,
                    "manifest.platform.coverage.missing",
                    "platform.entryPoints",
                    $"Supported platform '{platformId}' is not covered by any entry point.");
            }
        }
    }

    /// <summary>
    /// ADR 0070, section 1: a window app needs the DOM and only runs on the main thread; the
    /// <c>default</c> placeholder of the attribute is never a manifest value.
    /// </summary>
    private static void ValidateExecution(AppManifest manifest, List<ManifestValidationError> errors)
    {
        if (manifest.Execution is not { } execution)
        {
            return;
        }

        if (!Enum.IsDefined(execution) || execution == AppExecutionHost.Default)
        {
            AddError(errors, "manifest.execution.invalid", "execution", "Execution must be mainThread, pool, dedicated or web.");
        }
        else if (manifest.Kind == AppKind.Window && execution != AppExecutionHost.MainThread)
        {
            AddError(errors, "manifest.execution.window", "execution", "Window apps run on the main thread: they need the DOM.");
        }
        else if (manifest.Kind == AppKind.Web && execution is not (AppExecutionHost.Web or AppExecutionHost.MainThread))
        {
            // ADR 0073, section 1: the server code of every site shares the web worker.
            AddError(errors, "manifest.execution.web", "execution", "Web apps run in the shared web worker (web) or on the main thread.");
        }
        else if (manifest.Kind != AppKind.Web && execution == AppExecutionHost.Web)
        {
            AddError(errors, "manifest.execution.web-only", "execution", "Only web apps run in the shared web worker.");
        }
    }

    /// <summary>
    /// Checks the web part of ADR 0073: a web app declares at least one valid, distinct URL; no
    /// other kind declares any; and a site never asks for the file system (its data lives in the
    /// host browser's IndexedDB, section 3).
    /// </summary>
    private static void ValidateWeb(AppManifest manifest, List<ManifestValidationError> errors)
    {
        if (manifest.Kind != AppKind.Web)
        {
            if (manifest.Web is not null)
            {
                AddError(errors, "manifest.web.forbidden", "web", "Only web apps can declare web URLs.");
            }

            return;
        }

        if (manifest.Web is not { Urls.Count: > 0 } web)
        {
            AddError(errors, "manifest.web.urls.required", "web.urls", "Web apps must declare at least one URL, e.g. 'http://intranet.hack/'.");
        }
        else
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (string url in web.Urls)
            {
                if (!WebAppUrls.TryParse(url, allowWildcardHost: false, out WebBinding binding, out string? error))
                {
                    AddError(errors, "manifest.web.url.invalid", "web.urls", error!);
                }
                else if (!seen.Add(binding.ToString()))
                {
                    AddError(errors, "manifest.web.url.duplicate", "web.urls", $"URL '{url}' is declared twice.");
                }
            }
        }

        if (manifest.Web?.Blazor is { } blazor)
        {
            // ADR 0084, section 1: lazy loading fetches the entry-point assembly only, as for components.
            RequireText(blazor.Type, "web.blazor.type", errors);
            if (!string.Equals(blazor.Assembly, manifest.EntryPoint?.Assembly, StringComparison.Ordinal))
            {
                AddError(errors, "manifest.web.blazor.assembly", "web.blazor.assembly", $"The Blazor root '{blazor.Type}' must live in the app's entry-point assembly.");
            }
        }

        foreach (string capability in manifest.Capabilities)
        {
            if (capability.StartsWith("filesystem.", StringComparison.Ordinal))
            {
                AddError(errors, "manifest.web.filesystem", "capabilities", $"A web site cannot request '{capability}': its data lives in the browser's database, not in the OS file system (ADR 0073).");
            }
        }
    }

    private static void ValidateKindSpecificFields(AppManifest manifest, List<ManifestValidationError> errors)
    {
        if (manifest.Kind == AppKind.Terminal)
        {
            if (manifest.Terminal is null)
            {
                AddError(errors, "manifest.terminal.required", "terminal", "Terminal apps must declare command metadata.");
            }
            else
            {
                ValidateCommandName(manifest.Terminal.Name, "terminal.name", errors);
                ValidateUniqueValues(manifest.Terminal.Aliases, "terminal.aliases", errors);
                foreach (string alias in manifest.Terminal.Aliases)
                {
                    ValidateCommandName(alias, "terminal.aliases", errors);
                }

                RequireText(manifest.Terminal.Usage, "terminal.usage", errors);
            }
        }
        else if (manifest.Terminal is not null)
        {
            AddError(errors, "manifest.terminal.forbidden", "terminal", "Only terminal apps can declare command metadata.");
        }

        if (manifest.Kind != AppKind.Window && manifest.FileHandlers.Count > 0)
        {
            AddError(errors, "manifest.fileHandlers.forbidden", "fileHandlers", "Only window apps can declare file handlers.");
        }

        if (manifest.Kind != AppKind.Window && manifest.SupportsBack)
        {
            AddError(errors, "manifest.supportsBack.forbidden", "supportsBack", "Only window apps can declare Back navigation support.");
        }

        ValidateExecution(manifest, errors);
        if (manifest.OomScoreAdj is < -1000 or > 1000)
        {
            AddError(errors, "manifest.oomScoreAdj.range", "oomScoreAdj", "oomScoreAdj must be between -1000 and 1000 (ADR 0126).");
        }

        ValidateWindow(manifest, errors);
        ValidateWeb(manifest, errors);
        ValidateComponents(manifest, errors);
        ValidateContextMenus(manifest, errors);

        foreach (FileHandlerManifest handler in manifest.FileHandlers)
        {
            if (string.IsNullOrWhiteSpace(handler.MediaType) && handler.Extensions.Count == 0)
            {
                AddError(errors, "manifest.fileHandler.type.required", "fileHandlers", "A file handler needs a media type or extension.");
            }

            if (handler.Priority < 0)
            {
                AddError(errors, "manifest.fileHandler.priority.invalid", "fileHandlers.priority", "File handler priority must not be negative.");
            }

            ValidateUniqueValues(handler.Extensions, "fileHandlers.extensions", errors);
            ValidateUniqueValues(handler.Actions, "fileHandlers.actions", errors);
        }
    }

    /// <summary>
    /// Checks the declared window sizes of ADR 0095 §2: only a window app has a window, every size is
    /// positive, the window opens no smaller than its minimum and no larger than its maximum.
    /// </summary>
    private static void ValidateWindow(AppManifest manifest, List<ManifestValidationError> errors)
    {
        if (manifest.Window is not { } window)
        {
            return;
        }

        if (manifest.Kind != AppKind.Window)
        {
            AddError(errors, "manifest.window.forbidden", "window", "Only window apps can declare window sizes.");
            return;
        }

        bool sizesValid = true;
        foreach ((int? value, string field) in new (int?, string)[]
        {
            (window.DefaultWidth, "window.defaultWidth"),
            (window.DefaultHeight, "window.defaultHeight"),
            (window.MinWidth, "window.minWidth"),
            (window.MinHeight, "window.minHeight"),
            (window.MaxWidth, "window.maxWidth"),
            (window.MaxHeight, "window.maxHeight")
        })
        {
            if (value is <= 0)
            {
                AddError(errors, "manifest.window.size.invalid", field, "Window sizes must be positive.");
                sizesValid = false;
            }
        }

        if (!sizesValid)
        {
            return;
        }

        if (window.MinWidth > window.DefaultWidth || window.MinHeight > window.DefaultHeight)
        {
            AddError(errors, "manifest.window.minimum.exceedsDefault", "window", "The minimum window size cannot be larger than the default size.");
        }

        if (window.DefaultWidth > window.MaxWidth || window.DefaultHeight > window.MaxHeight)
        {
            AddError(errors, "manifest.window.default.exceedsMaximum", "window", "The default window size cannot be larger than the maximum size.");
        }
    }

    /// <summary>
    /// Checks the embeddable components of ADR 0063: only window apps render inside other windows,
    /// a contract is registered once per app, and the component lives in the app's entry-point
    /// assembly, because lazy loading fetches exactly that one DLL.
    /// </summary>
    private static void ValidateComponents(AppManifest manifest, List<ManifestValidationError> errors)
    {
        if (manifest.Components is not { Count: > 0 } components)
        {
            return;
        }

        if (manifest.Kind != AppKind.Window)
        {
            AddError(errors, "manifest.components.forbidden", "components", "Only window apps can register embeddable components.");
        }

        ValidateUniqueValues(components.Select(component => component.Contract ?? string.Empty), "components.contract", errors);
        HashSet<string> entryAssemblies = new(StringComparer.Ordinal);
        if (manifest.EntryPoint is not null)
        {
            entryAssemblies.Add(manifest.EntryPoint.Assembly);
        }

        foreach (AppPlatformEntryPointManifest entryPoint in manifest.Platform?.EntryPoints ?? [])
        {
            entryAssemblies.Add(entryPoint.Assembly);
        }

        foreach (AppComponentManifest component in components)
        {
            if (!ComponentContractPattern().IsMatch(component.Contract ?? string.Empty))
            {
                AddError(errors, "manifest.component.contract.invalid", "components.contract", $"Component contract '{component.Contract}' must be lower-case dotted words, e.g. 'hackeros.terminal'.");
            }

            // ADR 0148 §3.3: the platform renders its own context menu; no app may stand in for it.
            if (component.Contract is { } contract
                && AppComponentContracts.GetProviderScope(contract) == AppComponentProviderScope.Platform)
            {
                AddError(errors, "manifest.component.contract.platform", "components.contract", $"Component contract '{contract}' is provided by the platform itself; an app cannot register it.");
            }

            RequireText(component.Type, "components.type", errors);
            if (!entryAssemblies.Contains(component.Assembly ?? string.Empty))
            {
                AddError(errors, "manifest.component.assembly.invalid", "components.assembly", $"Component '{component.Contract}' must live in the app's entry-point assembly.");
            }
        }
    }

    /// <summary>
    /// Checks the file menu entries of ADR 0151 §5 that one manifest can check by itself: the
    /// capability, unique names, a parent of the same app without intent, three levels at most, one
    /// position, a valid screen filter (ADR 0218), anchors among the default ids or the app's own entries, an intent on every entry
    /// without children, and an English title. Whether another app declares the intent of a
    /// <c>target</c> is checked where every manifest is known (aggregation, and the OS at boot).
    /// </summary>
    private static void ValidateContextMenus(AppManifest manifest, List<ManifestValidationError> errors)
    {
        if (manifest.ContextMenus is not { Count: > 0 } entries)
        {
            return;
        }

        if (manifest.Kind is not (AppKind.Window or AppKind.Service))
        {
            AddError(errors, "manifest.context-menu.forbidden", "contextMenus", "Only window and service apps can add entries to the file menus: an entry launches the app.");
        }

        if (!manifest.Capabilities.Contains(AppCapabilities.ShellContextMenu, StringComparer.Ordinal))
        {
            AddError(errors, "manifest.context-menu.capability", "contextMenus", $"An app adding file menu entries must request the '{AppCapabilities.ShellContextMenu}' capability (ADR 0151 §6).");
        }

        ValidateUniqueValues(entries.Select(entry => entry.Name ?? string.Empty), "contextMenus.name", errors);
        Dictionary<string, AppContextMenuEntryManifest> byName = new(StringComparer.Ordinal);
        foreach (AppContextMenuEntryManifest entry in entries)
        {
            if (entry.Name is { } name)
            {
                byName.TryAdd(name, entry);
            }
        }

        HashSet<string> parents = new(entries.Where(entry => entry.Parent is not null).Select(entry => entry.Parent!), StringComparer.Ordinal);
        foreach (AppContextMenuEntryManifest entry in entries)
        {
            string name = entry.Name ?? string.Empty;
            if (!ContextMenuNamePattern().IsMatch(name))
            {
                AddError(errors, "manifest.context-menu.name.invalid", "contextMenus.name", $"Menu entry name '{name}' must be lower-case words joined by dashes, e.g. 'extract-here'.");
            }

            RequireText(entry.Title, "contextMenus.title", errors);
            if (!(entry.Titles ?? []).Any(title => string.Equals(title.Culture, AppContextMenus.EnglishCulture, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(title.Text)))
            {
                AddError(errors, "manifest.context-menu.title.english", "contextMenus.titles", $"Menu entry '{name}' has no English title: add '{entry.Title}' to Resources/AppManifest.resx (ADR 0159 §3).");
            }

            string target = entry.Target ?? string.Empty;
            if (target != AppContextMenus.SelfTarget && !AppIdPattern().IsMatch(target))
            {
                AddError(errors, "manifest.context-menu.target.invalid", "contextMenus.target", $"Menu entry '{name}' targets '{target}': use 'self' or an app id.");
            }

            bool hasChildren = parents.Contains(name);
            if (hasChildren && entry.Intent is not null)
            {
                AddError(errors, "manifest.context-menu.parent.intent", "contextMenus.intent", $"Menu entry '{name}' opens a submenu: it cannot also have an intent.");
            }
            else if (!hasChildren && string.IsNullOrWhiteSpace(entry.Intent))
            {
                AddError(errors, "manifest.context-menu.intent.required", "contextMenus.intent", $"Menu entry '{name}' has no submenu and no intent: it would do nothing.");
            }
            else if (entry.Intent is { } intent && (intent.Any(char.IsWhiteSpace) || intent.Length > 128 || AppIntentIds.IsBuiltIn(intent)))
            {
                AddError(errors, "manifest.context-menu.intent.invalid", "contextMenus.intent", $"Menu entry '{name}' has an invalid intent '{intent}': no spaces, 128 characters at most, not an intent of the OS.");
            }

            if (entry.Target != AppContextMenus.SelfTarget && entry.Intent is { } targetIntent && !AppIntentIds.IsWellFormed(targetIntent))
            {
                AddError(errors, "manifest.context-menu.intent.invalid", "contextMenus.intent", $"Menu entry '{name}' targets another app: its intent '{targetIntent}' must be an intent id that app declares (domain.name.vN).");
            }

            IReadOnlyList<string> scopes = entry.Scopes ?? [];
            if (scopes.Count == 0 || scopes.Any(scope => !AppContextMenus.Scopes.Contains(scope, StringComparer.Ordinal)))
            {
                AddError(errors, "manifest.context-menu.scope.invalid", "contextMenus.scopes", $"Menu entry '{name}' needs one or more scopes among file, directory, background and desktop.");
            }

            if (!AppContextMenus.Selections.Contains(entry.Selection ?? string.Empty, StringComparer.Ordinal))
            {
                AddError(errors, "manifest.context-menu.selection.invalid", "contextMenus.selection", $"Menu entry '{name}' has an unknown selection '{entry.Selection}' (all, each or single).");
            }

            // ADR 0218 §2: the screen filter, a count or one secondary screen, never both.
            if (entry.Screens is { } screens && screens != AppContextMenus.ScreensSingle && screens != AppContextMenus.ScreensMultiple)
            {
                AddError(errors, "manifest.context-menu.screens.invalid", "contextMenus.screens", $"Menu entry '{name}' has an unknown screens filter '{screens}' (single or multiple).");
            }

            if (entry.Screen is { } screen && (screen < AppContextMenus.MinScreenNumber || screen > AppContextMenus.MaxScreenNumber))
            {
                AddError(errors, "manifest.context-menu.screen.invalid", "contextMenus.screen", $"Menu entry '{name}' requires screen {screen}: use a number from {AppContextMenus.MinScreenNumber} to {AppContextMenus.MaxScreenNumber}.");
            }

            if (entry.Screens is not null && entry.Screen is not null)
            {
                AddError(errors, "manifest.context-menu.screen.many", "contextMenus", $"Menu entry '{name}' has both a screens filter and a screen: choose one.");
            }

            foreach (string type in entry.Types ?? [])
            {
                if (!IsContextMenuType(type))
                {
                    AddError(errors, "manifest.context-menu.type.invalid", "contextMenus.types", $"Menu entry '{name}' has an invalid type '{type}': use '.ext', 'type/subtype', 'type/*' or '*'.");
                }
            }

            if (entry.Icon is { } icon)
            {
                bool vector = icon.Kind == AppContextMenus.VectorIcon;
                if ((!vector && icon.Kind != AppContextMenus.ImageIcon)
                    || string.IsNullOrWhiteSpace(icon.Source)
                    || (vector && !AppContextMenus.VectorLibraries.Contains(icon.Library ?? string.Empty, StringComparer.Ordinal))
                    || (!vector && icon.Library is not null))
                {
                    AddError(errors, "manifest.context-menu.icon.invalid", "contextMenus.icon", $"Menu entry '{name}' has an invalid icon: a vector icon names its library, an image names its asset.");
                }
            }

            int positions = (entry.First ? 1 : 0) + (entry.After is null ? 0 : 1) + (entry.AfterGroup is null ? 0 : 1);
            if (positions > 1)
            {
                AddError(errors, "manifest.context-menu.position.many", "contextMenus", $"Menu entry '{name}' has several positions: choose one of first, after and afterGroup.");
            }

            if (entry.Parent is { } parent)
            {
                if (positions > 0)
                {
                    AddError(errors, "manifest.context-menu.position.child", "contextMenus", $"Menu entry '{name}' is in the submenu of '{parent}': first, after and afterGroup only place top-level entries.");
                }

                if (!byName.TryGetValue(parent, out AppContextMenuEntryManifest? parentEntry) || ReferenceEquals(parentEntry, entry))
                {
                    AddError(errors, "manifest.context-menu.parent.unknown", "contextMenus.parent", $"Menu entry '{name}' names an unknown parent '{parent}' (an entry of the same app).");
                }
                else if (!(parentEntry.Scopes ?? []).Any() || scopes.Any(scope => !(parentEntry.Scopes ?? []).Contains(scope, StringComparer.Ordinal)))
                {
                    AddError(errors, "manifest.context-menu.parent.scope", "contextMenus.scopes", $"Menu entry '{name}' appears where its parent '{parent}' does not.");
                }
            }

            foreach ((string? anchor, string property) in new[] { (entry.After, "after"), (entry.AfterGroup, "afterGroup") })
            {
                if (anchor is not null
                    && !AppContextMenus.DefaultItemIds.Contains(anchor)
                    && !(byName.TryGetValue(anchor, out AppContextMenuEntryManifest? anchored) && anchored.Parent is null && !ReferenceEquals(anchored, entry)))
                {
                    AddError(errors, "manifest.context-menu.anchor.unknown", $"contextMenus.{property}", $"Menu entry '{name}' is placed after '{anchor}', which is neither an entry of the file menus nor a top-level entry of this app.");
                }
            }
        }

        // Depth and cycles: follow each entry's parents; a chain longer than MaxDepth or looping is refused.
        foreach (AppContextMenuEntryManifest entry in entries)
        {
            int depth = 1;
            HashSet<string> seen = new(StringComparer.Ordinal) { entry.Name ?? string.Empty };
            string? parent = entry.Parent;
            while (parent is not null && byName.TryGetValue(parent, out AppContextMenuEntryManifest? next))
            {
                depth++;
                if (!seen.Add(parent))
                {
                    AddError(errors, "manifest.context-menu.parent.cycle", "contextMenus.parent", $"Menu entry '{entry.Name}' is its own ancestor.");
                    break;
                }

                if (depth > AppContextMenus.MaxDepth)
                {
                    AddError(errors, "manifest.context-menu.depth", "contextMenus.parent", $"Menu entry '{entry.Name}' is deeper than {AppContextMenus.MaxDepth} levels.");
                    break;
                }

                parent = next.Parent;
            }
        }
    }

    /// <summary>Whether <paramref name="type"/> is a menu entry type filter: <c>*</c>, <c>.ext</c>, <c>type/subtype</c> or <c>type/*</c>.</summary>
    private static bool IsContextMenuType(string? type) =>
        type is "*"
        || (type is { Length: > 1 } && type[0] == '.' && type.Skip(1).All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.'))
        || ContextMenuMediaTypePattern().IsMatch(type ?? string.Empty);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ContextMenuNamePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9!#$&^_.+-]*/(\\*|[a-z0-9][a-z0-9!#$&^_.+-]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ContextMenuMediaTypePattern();

    private static void ValidateAssets(IReadOnlyList<AssetManifest> assets, List<ManifestValidationError> errors)
    {
        ValidateUniqueValues(assets.Select(asset => asset.Path), "assets.path", errors);

        foreach (AssetManifest asset in assets)
        {
            ValidatePackageRelativePath(asset.Path, "assets.path", errors);
            if (!Enum.IsDefined(asset.Kind))
            {
                AddError(errors, "manifest.asset.kind.invalid", "assets.kind", $"Asset kind for '{asset.Path}' is not recognized.");
            }

            if (!Sha256Pattern().IsMatch(asset.Sha256 ?? string.Empty))
            {
                AddError(errors, "manifest.asset.hash.invalid", "assets.sha256", $"Asset '{asset.Path}' needs a lowercase 64-character hexadecimal SHA-256 hash.");
            }
        }
    }

    private static void ValidatePresentation(
        PresentationManifest presentation,
        IReadOnlyList<AssetManifest> assets,
        List<ManifestValidationError> errors)
    {
        RequireText(presentation.Category, "presentation.category", errors);
        if (!Enum.IsDefined(presentation.LaunchVisibility))
        {
            AddError(errors, "manifest.presentation.launchVisibility.invalid", "presentation.launchVisibility", "Launch visibility is not recognized.");
        }

        HashSet<string> assetPaths = new(assets.Select(asset => asset.Path), StringComparer.Ordinal);
        foreach (string iconAssetPath in presentation.IconAssetPaths)
        {
            ValidatePackageRelativePath(iconAssetPath, "presentation.iconAssetPaths", errors);
            if (!assetPaths.Contains(iconAssetPath))
            {
                AddError(errors, "manifest.presentation.icon.undeclared", "presentation.iconAssetPaths", $"Icon asset '{iconAssetPath}' is not declared in assets.");
            }
        }
    }

    private static void ValidateLocalizations(IReadOnlyList<LocalizationManifest> localizations, List<ManifestValidationError> errors)
    {
        ValidateUniqueValues(localizations.Select(localization => localization.Culture), "localizations.culture", errors);

        foreach (LocalizationManifest localization in localizations)
        {
            RequireText(localization.Culture, "localizations.culture", errors);
            ValidatePackageRelativePath(localization.ResourcePath, "localizations.resourcePath", errors);
        }
    }

    private static void ValidateResources(AppResourceProfileManifest resources, List<ManifestValidationError> errors)
    {
        ValidateWeightPair(resources.BaselineCpuWeight, resources.BurstCpuWeight, "resources.cpuWeight", errors);
        ValidateWeightPair(resources.BaselineMemoryWeight, resources.BurstMemoryWeight, "resources.memoryWeight", errors);
        ValidateWeightPair(resources.BaselineStorageIoWeight, resources.BurstStorageIoWeight, "resources.storageIoWeight", errors);
        ValidateWeightPair(resources.BaselineNetworkIoWeight, resources.BurstNetworkIoWeight, "resources.networkIoWeight", errors);
    }

    private static void ValidateWeightPair(double baseline, double burst, string path, List<ManifestValidationError> errors)
    {
        if (baseline is < 0 or > 1)
        {
            AddError(errors, "manifest.resources.weight.range", $"{path}.baseline", "Weight must be within [0, 1].");
        }

        if (burst is < 0 or > 1)
        {
            AddError(errors, "manifest.resources.weight.range", $"{path}.burst", "Weight must be within [0, 1].");
        }

        if (burst < baseline)
        {
            AddError(errors, "manifest.resources.weight.burstBelowBaseline", path, "Burst weight must be at least the baseline weight.");
        }
    }

    private static void ValidateSettingsSchema(AppSettingsSchemaManifest settings, List<ManifestValidationError> errors)
    {
        if (settings.SchemaVersion < 1)
        {
            AddError(errors, "manifest.settings.schemaVersion.invalid", "settings.schemaVersion", "Settings schema version must be at least 1.");
        }

        ValidateUniqueValues(
            settings.Fields.Select(field => field.Group is null ? field.Key : $"{field.Group}.{field.Key}"),
            "settings.fields.key",
            errors);

        foreach (AppSettingFieldManifest field in settings.Fields)
        {
            RequireText(field.Key, "settings.fields.key", errors);
            if (!Enum.IsDefined(field.ValueType))
            {
                AddError(errors, "manifest.settings.field.valueType.invalid", "settings.fields.valueType", $"Setting '{field.Key}' has an unrecognized value type.");
            }

            if (!Enum.IsDefined(field.Scope))
            {
                AddError(errors, "manifest.settings.field.scope.invalid", "settings.fields.scope", $"Setting '{field.Key}' has an unrecognized scope.");
            }

            if (!Enum.IsDefined(field.Sensitivity))
            {
                AddError(errors, "manifest.settings.field.sensitivity.invalid", "settings.fields.sensitivity", $"Setting '{field.Key}' has an unrecognized sensitivity.");
            }

            // ADR 0044: [locked] is the system layer's reserved lock section, never a data group.
            if (string.Equals(field.Group, "locked", StringComparison.Ordinal))
            {
                AddError(errors, "manifest.settings.field.group.reserved", "settings.fields.group", $"Setting '{field.Key}' uses the reserved group name 'locked'.");
            }

            if (field.ValueType == AppSettingValueType.Enum && (field.AllowedValues is null || field.AllowedValues.Count == 0))
            {
                AddError(errors, "manifest.settings.field.allowedValues.required", "settings.fields.allowedValues", $"Enum setting '{field.Key}' must declare at least one allowed value.");
            }
        }
    }

    private static void ValidateIntents(
        IReadOnlyList<AppIntentDeclarationManifest> intents,
        IReadOnlyList<AssetManifest> assets,
        List<ManifestValidationError> errors)
    {
        ValidateUniqueValues(intents.Select(intent => intent.IntentId), "intents.intentId", errors);

        HashSet<string> assetPaths = new(assets.Select(asset => asset.Path), StringComparer.Ordinal);
        foreach (AppIntentDeclarationManifest intent in intents)
        {
            RequireText(intent.IntentId, "intents.intentId", errors);
            if (intent.PayloadSchemaAssetPath is not null)
            {
                ValidatePackageRelativePath(intent.PayloadSchemaAssetPath, "intents.payloadSchemaAssetPath", errors);
                if (!assetPaths.Contains(intent.PayloadSchemaAssetPath))
                {
                    AddError(errors, "manifest.intent.payloadSchema.undeclared", "intents.payloadSchemaAssetPath", $"Payload schema asset '{intent.PayloadSchemaAssetPath}' is not declared in assets.");
                }
            }
        }
    }

    private static void ValidateUpdate(UpdateManifest update, List<ManifestValidationError> errors)
    {
        ValidateUniqueValues(update.MigrationIds, "update.migrationIds", errors);
        foreach (string migrationId in update.MigrationIds)
        {
            RequireText(migrationId, "update.migrationIds", errors);
        }

        if (update.MinimumUpgradableVersion is not null)
        {
            ValidateSemanticVersion(update.MinimumUpgradableVersion, "update.minimumUpgradableVersion", errors);
        }
    }

    private static void ValidatePackageRelativePath(string value, string path, List<ManifestValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            AddError(errors, "manifest.value.required", path, "A non-empty value is required.");
            return;
        }

        bool isInvalid = value.StartsWith('/')
            || value.Contains('\\')
            || value.Contains("..", StringComparison.Ordinal)
            || value.Contains('?')
            || value.Contains('#')
            || value.Contains("://", StringComparison.Ordinal);

        if (isInvalid)
        {
            AddError(
                errors,
                "manifest.path.invalid",
                path,
                "Paths must be package-relative, use '/' separators, and must not contain '..', backslashes, queries, fragments, or external URLs.");
        }
    }

    private static void ValidateDependencies(
        IReadOnlyList<AppDependencyManifest> dependencies,
        List<ManifestValidationError> errors)
    {
        ValidateUniqueValues(dependencies.Select(dependency => dependency.AppId), "dependencies", errors);

        foreach (AppDependencyManifest dependency in dependencies)
        {
            ValidateAppId(dependency.AppId, "dependencies.appId", errors);
            ValidateVersionRange(dependency.MinimumVersion, dependency.MaximumVersion, "dependencies.version", errors);
        }
    }

    private static void ValidateCapabilities(
        IReadOnlyList<string> capabilities,
        List<ManifestValidationError> errors)
    {
        ValidateUniqueValues(capabilities, "capabilities", errors);

        foreach (string capability in capabilities)
        {
            if (!AppCapabilities.IsKnown(capability) && !TopicPermissions.IsWellFormed(capability))
            {
                AddError(
                    errors,
                    "manifest.capability.unknown",
                    "capabilities",
                    $"Capability '{capability}' is not recognized by this App SDK version and is not a " +
                    "well-formed topic permission.");
            }
        }
    }

    private static void ValidateDeclaredTopicPermissions(
        string appId,
        IReadOnlyList<TopicPermissionDeclarationManifest> declarations,
        List<ManifestValidationError> errors)
    {
        // A manifest deserialized from JSON that predates this field (every manifest.json shipped before
        // this change) leaves it null rather than running the record's [] field initializer, the same as
        // every other optional array-typed manifest property would if a caller omitted it.
        declarations ??= [];

        ValidateUniqueValues(declarations.Select(static declaration => declaration.Id), "declaredTopicPermissions", errors);

        foreach (TopicPermissionDeclarationManifest declaration in declarations)
        {
            RequireText(declaration.Description, "declaredTopicPermissions.description", errors);

            if (!TopicPermissions.IsWellFormed(declaration.Id))
            {
                AddError(
                    errors,
                    "manifest.topicPermission.malformed",
                    "declaredTopicPermissions",
                    $"'{declaration.Id}' is not a well-formed topic permission identifier.");
                continue;
            }

            if (!TopicPermissions.IsOwnedByApp(declaration.Id, appId))
            {
                AddError(
                    errors,
                    "manifest.topicPermission.notOwned",
                    "declaredTopicPermissions",
                    $"'{declaration.Id}' must be rooted under this app's own topic namespace ('app/{appId}/...').");
            }
        }
    }

    private static void ValidateCapabilityKindCompatibility(
        AppKind kind,
        IReadOnlyList<string> capabilities,
        List<ManifestValidationError> errors)
    {
        if (kind == AppKind.Window)
        {
            return;
        }

        foreach (string capability in capabilities)
        {
            if (AppCapabilities.RequiresWindowKind(capability))
            {
                AddError(
                    errors,
                    "manifest.capability.incompatible",
                    "capabilities",
                    $"Capability '{capability}' renders modal UI and requires a window app.");
            }
        }
    }

    private static void ValidateVersionRange(
        string minimumVersion,
        string? maximumVersion,
        string path,
        List<ManifestValidationError> errors)
    {
        bool minimumIsValid = SemanticVersion.TryParse(minimumVersion, out SemanticVersion minimum);
        if (!minimumIsValid)
        {
            AddError(errors, "manifest.version.invalid", $"{path}.minimumVersion", "A semantic version with major, minor, and patch is required.");
        }

        if (maximumVersion is null)
        {
            return;
        }

        bool maximumIsValid = SemanticVersion.TryParse(maximumVersion, out SemanticVersion maximum);
        if (!maximumIsValid)
        {
            AddError(errors, "manifest.version.invalid", $"{path}.maximumVersion", "A semantic version with major, minor, and patch is required.");
        }
        else if (minimumIsValid && maximum.CompareTo(minimum) < 0)
        {
            AddError(errors, "manifest.version.range.invalid", path, "Maximum version must not be lower than minimum version.");
        }
    }

    private static void ValidateSemanticVersion(string value, string path, List<ManifestValidationError> errors)
    {
        if (!SemanticVersion.TryParse(value, out _))
        {
            AddError(errors, "manifest.version.invalid", path, "A semantic version with major, minor, and patch is required.");
        }
    }

    private static void ValidateAppId(string value, string path, List<ManifestValidationError> errors)
    {
        if (!AppIdPattern().IsMatch(value ?? string.Empty))
        {
            AddError(errors, "manifest.appId.invalid", path, "Use a lowercase reverse-domain ID with at least three segments.");
        }
    }

    private static void ValidatePublisherId(string value, List<ManifestValidationError> errors)
    {
        if (!PublisherIdPattern().IsMatch(value ?? string.Empty))
        {
            AddError(errors, "manifest.publisherId.invalid", "publisherId", "Use a lowercase reverse-domain publisher ID.");
        }
    }

    private static void ValidateCommandName(string value, string path, List<ManifestValidationError> errors)
    {
        if (!CommandNamePattern().IsMatch(value ?? string.Empty))
        {
            AddError(errors, "manifest.command.invalid", path, "Command names must use lowercase letters, digits, and hyphens.");
        }
    }

    private static void RequireText(string value, string path, List<ManifestValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            AddError(errors, "manifest.value.required", path, "A non-empty value is required.");
        }
    }

    private static void ValidateUniqueValues(IEnumerable<string> values, string path, List<ManifestValidationError> errors)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string value in values)
        {
            if (!seen.Add(value))
            {
                AddError(errors, "manifest.value.duplicate", path, $"Duplicate value '{value}' is not allowed.");
            }
        }
    }

    private static void AddError(List<ManifestValidationError> errors, string code, string path, string message) =>
        errors.Add(new ManifestValidationError(code, path, message));

    [GeneratedRegex("^[a-z][a-z0-9-]*(?:\\.[a-z][a-z0-9-]*){2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppIdPattern();

    [GeneratedRegex("^[a-z][a-z0-9-]*(?:\\.[a-z][a-z0-9-]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PublisherIdPattern();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CommandNamePattern();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*(?:\\.[a-z0-9]+(?:-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ComponentContractPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}