namespace HackerOs.Tools.ManifestGenerator;

/// <summary>
/// One build-breaking manifest problem with a stable <c>HOSAPPnnnn</c> code (ADR 0047 §2). The
/// <see cref="Subject"/> names the offending <c>[AppManifest]</c> class (full type name) or file.
/// </summary>
/// <param name="Code">Stable code, <c>HOSAPP0001</c> to <c>HOSAPP0099</c>.</param>
/// <param name="Subject">Full name of the class, or path of the file, the problem is about.</param>
/// <param name="Message">Human-readable explanation.</param>
public sealed record GeneratorDiagnostic(string Code, string Subject, string Message)
{
    /// <summary>
    /// Formats the diagnostic in MSBuild's canonical error format so the calling <c>Exec</c> task
    /// reports it as a real build error attributed to <paramref name="origin"/>.
    /// </summary>
    /// <param name="origin">Project or file path shown as the error origin.</param>
    /// <returns>One line, e.g. <c>/x/App.csproj : error HOSAPP0005: [Ns.MyApp] ...</c>.</returns>
    public string ToMsBuildLine(string origin) => $"{origin} : error {Code}: [{Subject}] {Message}";
}

/// <summary>Catalog of the stable diagnostic codes emitted by the generator.</summary>
public static class GeneratorCodes
{
    /// <summary>The app project contains no <c>[AppManifest]</c> class.</summary>
    public const string NoDefinition = "HOSAPP0001";

    /// <summary>The <c>[AppManifest]</c> class is not public.</summary>
    public const string NotPublic = "HOSAPP0002";

    /// <summary>The <c>[AppManifest]</c> class is abstract.</summary>
    public const string Abstract = "HOSAPP0003";

    /// <summary>The <c>[AppManifest]</c> class does not derive from <c>AppDefinition</c>.</summary>
    public const string NotAnAppDefinition = "HOSAPP0004";

    /// <summary>The <c>[AppManifest]</c> class has no public parameterless constructor.</summary>
    public const string NoParameterlessConstructor = "HOSAPP0005";

    /// <summary>The constructor or a getter threw while the manifest was being produced.</summary>
    public const string DefinitionThrew = "HOSAPP0006";

    /// <summary>An entry-point type is missing or lives in another assembly than the definition.</summary>
    public const string EntryPointAssembly = "HOSAPP0007";

    /// <summary>Two instantiations of the definition produced different manifests.</summary>
    public const string NonDeterministic = "HOSAPP0008";

    /// <summary><c>AppManifestValidator</c> rejected the produced manifest.</summary>
    public const string InvalidManifest = "HOSAPP0009";

    /// <summary>The entry-point type breaks the <c>AppEntryPointDiscovery</c> rules for its kind.</summary>
    public const string EntryPointKind = "HOSAPP0010";

    /// <summary>Two <c>[AppManifest]</c> classes of one assembly declare the same app id.</summary>
    public const string DuplicateIdInAssembly = "HOSAPP0011";

    /// <summary>
    /// The app project references a project outside the SDK boundary (ADR 0055 §2). Emitted by the
    /// <c>ValidateHackerOsSdkBoundary</c> MSBuild target, not by this tool; listed here so the catalog
    /// of codes stays in one place. ADR 0059 gives it this number (ADR 0055 said HOSAPP0002, already taken).
    /// </summary>
    public const string ForbiddenReference = "HOSAPP0030";

    /// <summary>
    /// The app injects a service that is neither an SDK type, a MudBlazor service nor
    /// <c>IJSRuntime</c> (ADR 0055 §3). ADR 0059 gives it this number (ADR 0055 said HOSAPP0003, already taken).
    /// </summary>
    public const string ForbiddenInjection = "HOSAPP0031";

    /// <summary>
    /// A web app (ADR 0073, section 3) requests a <c>filesystem.*</c> capability: a site's data lives
    /// in the host browser's IndexedDB, never in the OS file system.
    /// </summary>
    public const string WebFileSystem = "HOSAPP0050";

    /// <summary>
    /// A <c>Resources/AppManifest.&lt;culture&gt;.resx</c> file is malformed, names an unknown culture,
    /// app or key, or a referenced <c>localization/*.json</c> file cannot be read (ADR 0104, section 4).
    /// </summary>
    public const string InvalidLocalization = "HOSAPP0040";

    /// <summary>A manifest file read back during aggregation is missing or not strict schema-v1 JSON.</summary>
    public const string UnreadableManifest = "HOSAPP0020";

    /// <summary>The aggregated catalog is invalid (duplicate id, missing or cyclic dependency...).</summary>
    public const string InvalidCatalog = "HOSAPP0021";

    /// <summary>
    /// Aggregation (ADR 0098 §4): two apps of the host declare the same terminal command name or
    /// alias, or an app declares a name the shell runs itself (special built-ins, <c>sh</c>/<c>bash</c>/<c>hsh</c>).
    /// </summary>
    public const string DuplicateCommand = "HOSAPP0022";

    /// <summary>
    /// An asset the app declares (<c>AppDefinition.Assets</c>) has no file under the project's
    /// <c>wwwroot/</c>, so its SHA-256 cannot be computed (ADR 0098 §5).
    /// </summary>
    public const string AssetMissing = "HOSAPP0023";

    /// <summary>
    /// Aggregation (ADR 0126, section 4): a non-system app declares a negative <c>oomScoreAdj</c>.
    /// Only a system app (<c>IsSystemApp</c> on the host's <c>ProjectReference</c>) may shield itself
    /// from the OOM killer, like <c>CAP_SYS_RESOURCE</c> on Linux.
    /// </summary>
    public const string NegativeOomScoreAdjust = "HOSAPP0024";

    // HOSAPP0025 (a non-system app registering hackeros.file-view or hackeros.file-dialogs, ADR 0153
    // §4) is retired: ADR 0156 opens both contracts to any window app. The code is not reused.

    /// <summary>
    /// A file menu entry an app registers is invalid (ADR 0151 §5, ADR 0159): a bad
    /// <c>[RegisterFileContextMenu]</c> method, a method that throws or is not deterministic, a title
    /// key missing from <c>Resources/AppManifest.resx</c>, or a rule of the entries broken.
    /// </summary>
    public const string InvalidContextMenu = "HOSAPP0026";

    /// <summary>An app registers file menu entries without the <c>shell.context-menu</c> capability (ADR 0151 §6).</summary>
    public const string ContextMenuCapability = "HOSAPP0027";

    /// <summary>
    /// Aggregation (ADR 0151 §5): a file menu entry targets an app the host does not have, or one
    /// that does not declare the entry's intent.
    /// </summary>
    public const string ContextMenuTarget = "HOSAPP0028";

    /// <summary>
    /// <c>pack</c> (ADR 0182 §1): the app package could not be written, or the OS would refuse it
    /// (a declared assembly or asset is missing, or the manifest is invalid).
    /// </summary>
    public const string PackageFailed = "HOSAPP0060";

    /// <summary>
    /// <c>pack</c> (ADR 0216 §1), a warning: a static web resource of the project has a path a
    /// manifest refuses (<c>..</c>, <c>#</c>, <c>?</c>) and is left out of the package.
    /// </summary>
    public const string PackageAssetSkipped = "HOSAPP0061";

    // HOSAPP0062 (ADR 0216 §1, a warning) is raised by the HackerOsPack target itself, not by this
    // tool: a Razor project packed without its staticwebassets.build.json.

    /// <summary>The tool was called with invalid arguments.</summary>
    public const string InvalidArguments = "HOSAPP0090";

    /// <summary>The compiled app assembly could not be loaded or its types enumerated.</summary>
    public const string AssemblyLoadFailed = "HOSAPP0091";
}
