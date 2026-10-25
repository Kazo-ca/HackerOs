namespace HackerOs.App.Abstractions;

/// <summary>
/// Describes one independently versioned HackerOS application package.
/// </summary>
public sealed record AppManifest
{
    /// <summary>Gets the manifest schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Gets the immutable reverse-domain application identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the localized fallback display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the semantic package version.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the stable publisher identifier.</summary>
    public required string PublisherId { get; init; }

    /// <summary>Gets the localized fallback description.</summary>
    public required string Description { get; init; }

    /// <summary>Gets the application's hosting model.</summary>
    public required AppKind Kind { get; init; }

    /// <summary>
    /// Gets the managed assembly and type used to activate the app on every platform it supports.
    /// Mutually exclusive with <see cref="Platform"/> — exactly one of the two must be set. This is
    /// the legacy single-entry-point form; per doc/wasm/dev/old/mobile-interface-platform-plan.md §4.3, a
    /// manifest using this field is implicitly Desktop-only (see
    /// <see cref="AppManifestPlatformSupport.Resolve"/>).
    /// </summary>
    public AppEntryPointManifest? EntryPoint { get; init; }

    /// <summary>
    /// Gets the multi-platform entry-point declaration (<c>MOB-003</c>), or <see langword="null"/>
    /// for a manifest using the legacy single <see cref="EntryPoint"/> field. Mutually exclusive
    /// with <see cref="EntryPoint"/> — exactly one of the two must be set.
    /// See doc/wasm/dev/old/mobile-interface-platform-plan.md §4.1.
    /// </summary>
    public AppManifestPlatform? Platform { get; init; }

    /// <summary>Gets the supported App SDK version range.</summary>
    public required AppSdkCompatibilityManifest SdkCompatibility { get; init; }

    /// <summary>Gets the optional compatible HackerOS operating system version bound.</summary>
    public OsCompatibilityManifest? OsCompatibility { get; init; }

    /// <summary>Gets how the app presents itself in shell surfaces.</summary>
    public required PresentationManifest Presentation { get; init; }

    /// <summary>Gets localized label/help resource files keyed by culture.</summary>
    public IReadOnlyList<LocalizationManifest> Localizations { get; init; } = [];

    /// <summary>
    /// Gets the capabilities requested by the app — either a fixed OS-catalog identifier
    /// (<see cref="AppCapabilities"/>) or an app-declared topic permission (<see cref="TopicPermissions"/>)
    /// exported by another installed app's <see cref="DeclaredTopicPermissions"/>.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>
    /// Gets the custom topic permissions this app declares and exports, gating its own shared messaging
    /// channels. Purely optional per <c>doc/wasm/dev/adr/0040-declared-topic-permissions.md</c> — most apps
    /// declare none and rely on the default owner-only/open channel access instead. Every identifier must
    /// be rooted under this app's own topic namespace.
    /// </summary>
    public IReadOnlyList<TopicPermissionDeclarationManifest> DeclaredTopicPermissions { get; init; } = [];

    /// <summary>Gets the estimated simulation resource weights the app requests.</summary>
    public required AppResourceProfileManifest Resources { get; init; }

    /// <summary>Gets the app's persisted settings schema, or <see langword="null"/> when it persists none.</summary>
    public AppSettingsSchemaManifest? Settings { get; init; }

    /// <summary>Gets custom typed intents supported beyond the core intents in <see cref="AppIntentIds"/>.</summary>
    public IReadOnlyList<AppIntentDeclarationManifest> Intents { get; init; } = [];

    /// <summary>Gets app dependencies and their compatible version ranges.</summary>
    public IReadOnlyList<AppDependencyManifest> Dependencies { get; init; } = [];

    /// <summary>Gets static package assets with declared integrity hashes.</summary>
    public IReadOnlyList<AssetManifest> Assets { get; init; } = [];

    /// <summary>Gets data migration identifiers and upgrade rules applied over an older installed version.</summary>
    public UpdateManifest? Update { get; init; }

    /// <summary>Gets file types handled by a window app.</summary>
    public IReadOnlyList<FileHandlerManifest> FileHandlers { get; init; } = [];

    /// <summary>Gets command metadata for a terminal app.</summary>
    public TerminalCommandManifest? Terminal { get; init; }

    /// <summary>
    /// Gets the embeddable components this app registers with the OS (ADR 0063): another app can
    /// render one inside its own window by contract name, without referencing this app's assembly.
    /// <see langword="null"/> (omitted from <c>manifest.json</c>) when the app registers none, so
    /// existing manifests keep their exact bytes.
    /// </summary>
    public IReadOnlyList<AppComponentManifest>? Components { get; init; }

    /// <summary>
    /// Gets the entries this app adds to the file menus of the OS (ADR 0151, ADR 0159), in declaration
    /// order, which is the order of entries sharing a place. <see langword="null"/> (omitted from
    /// <c>manifest.json</c>) when the app registers none, so existing manifests keep their exact bytes.
    /// </summary>
    public IReadOnlyList<AppContextMenuEntryManifest>? ContextMenus { get; init; }

    /// <summary>
    /// Gets whether the ecosystem focuses the existing running instance instead of starting a
    /// new one when this app is launched again for the same user. Only consulted for
    /// <see cref="AppKind.Window"/> apps; Terminal and Service launches always start independently.
    /// </summary>
    public bool SingleInstancePerUser { get; init; }

    /// <summary>
    /// Gets whether a <see cref="AppKind.Service"/> app starts automatically at session login rather
    /// than only on explicit launch. Ignored, and rejected by <see cref="AppManifestValidator"/>, for
    /// every other app kind.
    /// </summary>
    public bool AutoStart { get; init; }

    /// <summary>
    /// Gets when an automatic <see cref="AppKind.Service"/> app starts (ADR 0097, section 4): after a
    /// login (<see cref="AppServiceStartup.Login"/>, the default) or at boot
    /// (<see cref="AppServiceStartup.Boot"/>, system apps only). <see langword="null"/> (omitted from
    /// <c>manifest.json</c>) means <see cref="AppServiceStartup.Login"/>, so existing manifests keep
    /// their exact bytes. Rejected by <see cref="AppManifestValidator"/> for every other kind.
    /// </summary>
    public AppServiceStartup? ServiceStartup { get; init; }

    /// <summary>
    /// Gets whether this app participates in platform Back navigation
    /// (doc/wasm/dev/old/mobile-interface-platform-plan.md §8, <c>MOB-012</c>) — a Desktop-only chrome Back
    /// button is shown for it, and the Mobile system navigation bar's triangle calls into it first.
    /// Only <see cref="AppKind.Window"/> apps may declare this; <see cref="AppManifestValidator"/>
    /// rejects it otherwise. Declaring it requires the resolved entry-point type to implement
    /// <c>HackerOs.AppSdk.Blazor.IAppBackHandler</c> — enforced at discovery, not here, since this
    /// project has no reference to the Blazor App SDK.
    /// </summary>
    public bool SupportsBack { get; init; }

    /// <summary>
    /// Gets the window sizes a <see cref="AppKind.Window"/> app declares (ADR 0095 §2), or
    /// <see langword="null"/> (omitted from <c>manifest.json</c>) when it declares none, in which case
    /// the OS uses <see cref="WindowOptions.Default"/>. <see cref="AppManifestValidator"/> rejects it
    /// for every other kind.
    /// </summary>
    public WindowOptions? Window { get; init; }

    /// <summary>
    /// Gets where the OS runs this app (ADR 0070, section 1): <c>mainThread</c>, <c>pool</c> or
    /// <c>dedicated</c>. The generator always writes the resolved value; <see langword="null"/> only for
    /// a manifest written before ADR 0070, read as the default of its <see cref="Kind"/>
    /// (<see cref="AppExecutionHosts.Of"/>).
    /// </summary>
    public AppExecutionHost? Execution { get; init; }

    /// <summary>
    /// Gets the web part of a <see cref="AppKind.Web"/> app (ADR 0073, section 1): the URLs it
    /// answers on. Required for a web app, forbidden for every other kind; <see langword="null"/>
    /// (omitted from <c>manifest.json</c>) otherwise, so existing manifests keep their exact bytes.
    /// </summary>
    public WebAppManifest? Web { get; init; }

    /// <summary>
    /// Gets the <c>oom_score_adj</c> the app's processes start with (ADR 0126, section 4), from
    /// <c>-1000</c> (never chosen by the OOM killer) to <c>1000</c> (chosen first), or
    /// <see langword="null"/> (omitted from <c>manifest.json</c>) for the OS default. A negative value
    /// is honored only for a system app: the generator refuses it for any other (<c>HOSAPP0024</c>)
    /// and the catalog ignores it at run time.
    /// </summary>
    public int? OomScoreAdj { get; init; }
}

/// <summary>
/// Identifies the managed entry point for an application.
/// </summary>
/// <param name="Assembly">Assembly name without a path.</param>
/// <param name="Type">Assembly-qualified or full .NET type name.</param>
public sealed record AppEntryPointManifest(string Assembly, string Type);

/// <summary>
/// Declares the range of App SDK versions supported by an application.
/// </summary>
/// <param name="MinimumVersion">Lowest supported semantic App SDK version.</param>
/// <param name="MaximumVersion">Highest supported semantic App SDK version, or no upper bound.</param>
public sealed record AppSdkCompatibilityManifest(string MinimumVersion, string? MaximumVersion = null);

/// <summary>
/// Declares a dependency on another application package.
/// </summary>
/// <param name="AppId">Immutable dependency application identifier.</param>
/// <param name="MinimumVersion">Lowest compatible package version.</param>
/// <param name="MaximumVersion">Highest compatible package version, or no upper bound.</param>
/// <param name="Optional">Whether the application can run without the dependency.</param>
public sealed record AppDependencyManifest(
    string AppId,
    string MinimumVersion,
    string? MaximumVersion = null,
    bool Optional = false);

/// <summary>
/// Declares a file type that a window application can open or edit.
/// </summary>
/// <param name="MediaType">Normalized media type, when known.</param>
/// <param name="Extensions">Normalized extensions including the leading dot.</param>
/// <param name="Actions">Supported actions such as <c>open</c> or <c>edit</c>.</param>
/// <param name="Priority">
/// Non-negative preference used to break ties among multiple candidate handlers for the same file
/// type; higher values are preferred. Defaults to <c>0</c>.
/// </param>
public sealed record FileHandlerManifest(
    string? MediaType,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> Actions,
    int Priority = 0);

/// <summary>
/// Declares one custom permission an app exports, gating a shared messaging channel it owns. The
/// <see cref="Id"/> must be a well-formed topic-permission identifier
/// (<see cref="HackerOs.App.Abstractions.TopicPermissions.IsWellFormed"/>) rooted under the declaring
/// app's own topic namespace — produced by a
/// <c>HackerOs.Simulation.Abstractions.Events.TopicName</c>'s <c>ToPublishPermission()</c>/
/// <c>ToSubscribePermission()</c> extension, never hand-typed. See
/// <c>doc/wasm/dev/adr/0040-declared-topic-permissions.md</c>.
/// </summary>
/// <param name="Id">Well-formed topic-permission capability identifier.</param>
/// <param name="Description">Human-readable description, shown when a requesting app's grant is approved.</param>
public sealed record TopicPermissionDeclarationManifest(string Id, string Description);

/// <summary>
/// Registers one embeddable component of an app for a contract (ADR 0063).
/// </summary>
/// <param name="Contract">Contract name, lower-case dotted (e.g. <c>hackeros.terminal</c>).</param>
/// <param name="Assembly">Assembly file name; the app's entry-point assembly, which lazy loading fetches.</param>
/// <param name="Type">Full .NET type name of the component.</param>
public sealed record AppComponentManifest(string Contract, string Assembly, string Type);

/// <summary>
/// Describes a command provided by a terminal application.
/// </summary>
/// <param name="Name">Primary shell command name.</param>
/// <param name="Aliases">Optional alternative command names.</param>
/// <param name="Usage">Short command usage text.</param>
public sealed record TerminalCommandManifest(
    string Name,
    IReadOnlyList<string> Aliases,
    string Usage);