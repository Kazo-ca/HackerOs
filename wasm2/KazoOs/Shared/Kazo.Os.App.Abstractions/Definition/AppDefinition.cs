namespace Kazo.Os.App.Abstractions;

/// <summary>
/// The single C# source of an application's manifest (ADR 0047, plan decision D6). A subclass marked
/// with <see cref="AppManifestAttribute"/> overrides the four required members and any optional member
/// whose default does not fit; <see cref="AppDefinitionMapper.ToManifest(AppDefinition)"/> turns it into
/// the schema-version-1 <see cref="AppManifest"/> that the build writes as <c>manifest.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Getters must be <b>pure</b>: no service, clock, randomness or I/O. The generator instantiates the
/// class twice and compares the canonical bytes, so a non-deterministic getter fails the build.
/// </para>
/// <para>
/// No member speaks about authority or system status: an app can neither declare itself a system app
/// (ADR 0048, that is decided by the host's <c>ProjectReference</c>) nor grant itself a capability
/// (ADR 0002) — <see cref="Capabilities"/> is only a request.
/// </para>
/// </remarks>
public abstract class AppDefinition
{
    /// <summary>Gets the immutable reverse-domain application identifier, e.g. <c>org.kazoos.text-editor</c>.</summary>
    public abstract string Id { get; }

    /// <summary>Gets the fallback display name.</summary>
    public abstract string Name { get; }

    /// <summary>Gets the application's hosting model.</summary>
    public abstract AppKind Kind { get; }

    /// <summary>
    /// Gets the desktop entry-point type: a window component for <see cref="AppKind.Window"/>, a command
    /// for <see cref="AppKind.Terminal"/>, a service for <see cref="AppKind.Service"/>. It must live in
    /// the same assembly as the definition, because lazy loading loads exactly one DLL per app.
    /// </summary>
    public abstract Type EntryPoint { get; }

    /// <summary>Gets the semantic package version. Defaults to <c>1.0.0</c>.</summary>
    public virtual string Version => "1.0.0";

    /// <summary>Gets the fallback description. Defaults to <see cref="Name"/>.</summary>
    public virtual string Description => Name;

    /// <summary>Gets the stable publisher identifier. Defaults to <c>pub.local</c>.</summary>
    public virtual string PublisherId => "pub.local";

    /// <summary>Gets the lower-kebab-case launcher category. Defaults to <c>utilities</c>.</summary>
    public virtual string Category => "utilities";

    /// <summary>Gets whether shell surfaces offer the app for direct launch. Defaults to visible.</summary>
    public virtual AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Visible;

    /// <summary>Gets package-relative icon asset paths (declared in <see cref="Assets"/>).</summary>
    public virtual IReadOnlyList<string> Icons => [];

    /// <summary>Gets the capabilities the app requests; granting them stays a policy decision.</summary>
    public virtual IReadOnlyList<string> Capabilities => [];

    /// <summary>Gets the file types a window app opens or edits.</summary>
    public virtual IReadOnlyList<FileHandler> FileHandlers => [];

    /// <summary>Gets the shell command a terminal app provides, or <see langword="null"/>.</summary>
    public virtual TerminalCommand? Terminal => null;

    /// <summary>
    /// Gets the components this window app registers with the OS for other apps to embed (ADR 0063),
    /// e.g. the Terminal app's terminal for <c>kazoos.terminal</c>. Each type must live in the
    /// definition's assembly and derive from <c>KazoEmbeddableComponent</c>.
    /// </summary>
    public virtual IReadOnlyList<AppComponent> Components => [];

    /// <summary>
    /// Gets the mobile entry-point type, or <see langword="null"/> for a desktop-only app. A non-null
    /// value produces the multi-platform <c>platform</c> manifest form instead of <c>entryPoint</c>.
    /// </summary>
    public virtual Type? MobileEntryPoint => null;

    /// <summary>Gets whether a second launch focuses the running instance (window apps only).</summary>
    public virtual bool SingleInstancePerUser => false;

    /// <summary>
    /// Gets the <c>oom_score_adj</c> the app's processes start with (ADR 0126, section 4), from
    /// <c>-1000</c> to <c>1000</c>, or <see langword="null"/> for the OS default (<c>0</c>, <c>-500</c>
    /// for a system service). A higher value makes the app the OOM killer's first choice. A negative
    /// value shields it and is reserved to system apps: the build of a host that references a
    /// non-system app declaring one fails with <c>KOSAPP0024</c>. Added in App SDK 1.2.
    /// </summary>
    public virtual int? OomScoreAdjust => null;

    /// <summary>Gets whether a service app starts on its own (at login, or at boot with <see cref="ServiceStartup"/>).</summary>
    public virtual bool AutoStart => false;

    /// <summary>
    /// Gets when an automatic service app starts (ADR 0097, section 4): <see langword="null"/> or
    /// <see cref="AppServiceStartup.Login"/> after a login, <see cref="AppServiceStartup.Boot"/> at boot
    /// (honored for system apps only). Service apps only.
    /// </summary>
    public virtual AppServiceStartup? ServiceStartup => null;

    /// <summary>Gets whether the app participates in platform Back navigation (window apps only).</summary>
    public virtual bool SupportsBack => false;

    /// <summary>
    /// Gets the window sizes of a window app (ADR 0095 §2): initial, minimum and maximum size and
    /// whether the user can resize it. <see langword="null"/> (the default) keeps the OS defaults,
    /// <see cref="WindowOptions.Default"/>. Window apps only.
    /// </summary>
    public virtual WindowOptions? Window => null;

    /// <summary>Gets the estimated simulation resource weights. Defaults to the zero profile.</summary>
    public virtual AppResourceProfileManifest Resources => AppResourceProfileManifest.None;

    /// <summary>
    /// Gets the settings schema the app persists under <c>/etc/&lt;appId&gt;/</c> and
    /// <c>~/.config/&lt;appId&gt;/</c> (ADR 0044), or <see langword="null"/> when it persists none.
    /// </summary>
    public virtual AppSettingsSchemaManifest? Settings => null;

    /// <summary>
    /// Gets the App SDK range the app supports, or <see langword="null"/> (the default) to let the
    /// manifest generator write the SDK version the app is compiled against as the minimum, with no
    /// maximum (ADR 0098 §3). Declare a range only to narrow it, e.g. a maximum for a known break.
    /// </summary>
    public virtual AppSdkCompatibilityManifest? SdkCompatibility => null;

    /// <summary>Gets dependencies on other application packages.</summary>
    public virtual IReadOnlyList<AppDependencyManifest> Dependencies => [];

    /// <summary>Gets custom typed intents supported beyond the core intents.</summary>
    public virtual IReadOnlyList<AppIntentDeclarationManifest> Intents => [];

    /// <summary>Gets the topic permissions this app declares and exports (ADR 0040).</summary>
    public virtual IReadOnlyList<TopicPermissionDeclarationManifest> DeclaredTopicPermissions => [];

    /// <summary>Gets localized label/help resource files.</summary>
    public virtual IReadOnlyList<LocalizationManifest> Localizations => [];

    /// <summary>Gets static package assets with integrity hashes.</summary>
    public virtual IReadOnlyList<AssetManifest> Assets => [];

    /// <summary>
    /// Gets the URLs a <see cref="AppKind.Web"/> app answers on, e.g. <c>http://intranet.hack/</c>
    /// (ADR 0073, section 1): written to <c>web.urls</c>, published by the OS resolver and listened on
    /// without <c>network.listen</c>. Required for a web app, empty for every other kind.
    /// </summary>
    public virtual IReadOnlyList<string> Urls => [];

    /// <summary>
    /// Gets the root component of a <see cref="AppKind.Web"/> app's interactive Blazor part
    /// (ADR 0073, section 4; ADR 0084), or <see langword="null"/> when the site has none. Written to
    /// <c>web.blazor</c>; the OS browser draws it in its page area. It must live in the entry-point
    /// assembly (the one lazy loading fetches).
    /// </summary>
    public virtual Type? BlazorRoot => null;
}

/// <summary>
/// Short form of <see cref="FileHandlerManifest"/> used by <see cref="AppDefinition.FileHandlers"/>.
/// </summary>
/// <param name="MediaType">Normalized media type, when known.</param>
/// <param name="Extensions">Normalized extensions including the leading dot.</param>
/// <param name="Actions">Supported actions such as <c>open</c> or <c>edit</c>.</param>
/// <param name="Priority">Non-negative tie-break preference; higher wins.</param>
public sealed record FileHandler(
    string? MediaType,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> Actions,
    int Priority = 0);

/// <summary>
/// Short form of <see cref="AppComponentManifest"/> used by <see cref="AppDefinition.Components"/>.
/// </summary>
/// <param name="Contract">Contract name, e.g. <c>kazoos.terminal</c>.</param>
/// <param name="Type">The component type, in the definition's assembly.</param>
public sealed record AppComponent(string Contract, Type Type);

/// <summary>
/// Short form of <see cref="TerminalCommandManifest"/> used by <see cref="AppDefinition.Terminal"/>.
/// </summary>
/// <param name="Name">Primary shell command name.</param>
/// <param name="Usage">Short usage text.</param>
/// <param name="Aliases">Optional alternative command names; <see langword="null"/> means none.</param>
public sealed record TerminalCommand(
    string Name,
    string Usage,
    IReadOnlyList<string>? Aliases = null);
