namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>
/// One build-breaking manifest problem with a stable <c>KOSAPPnnnn</c> code (ADR 0047 §2). The
/// <see cref="Subject"/> names the offending <c>[AppManifest]</c> class (full type name) or file.
/// </summary>
/// <param name="Code">Stable code, <c>KOSAPP0001</c> to <c>KOSAPP0099</c>.</param>
/// <param name="Subject">Full name of the class, or path of the file, the problem is about.</param>
/// <param name="Message">Human-readable explanation.</param>
public sealed record GeneratorDiagnostic(string Code, string Subject, string Message)
{
    /// <summary>
    /// Formats the diagnostic in MSBuild's canonical error format so the calling <c>Exec</c> task
    /// reports it as a real build error attributed to <paramref name="origin"/>.
    /// </summary>
    /// <param name="origin">Project or file path shown as the error origin.</param>
    /// <returns>One line, e.g. <c>/x/App.csproj : error KOSAPP0005: [Ns.MyApp] ...</c>.</returns>
    public string ToMsBuildLine(string origin) => $"{origin} : error {Code}: [{Subject}] {Message}";
}

/// <summary>Catalog of the stable diagnostic codes emitted by the generator.</summary>
public static class GeneratorCodes
{
    /// <summary>The app project contains no <c>[AppManifest]</c> class.</summary>
    public const string NoDefinition = "KOSAPP0001";

    /// <summary>The <c>[AppManifest]</c> class is not public.</summary>
    public const string NotPublic = "KOSAPP0002";

    /// <summary>The <c>[AppManifest]</c> class is abstract.</summary>
    public const string Abstract = "KOSAPP0003";

    /// <summary>The <c>[AppManifest]</c> class does not derive from <c>AppDefinition</c>.</summary>
    public const string NotAnAppDefinition = "KOSAPP0004";

    /// <summary>The <c>[AppManifest]</c> class has no public parameterless constructor.</summary>
    public const string NoParameterlessConstructor = "KOSAPP0005";

    /// <summary>The constructor or a getter threw while the manifest was being produced.</summary>
    public const string DefinitionThrew = "KOSAPP0006";

    /// <summary>An entry-point type is missing or lives in another assembly than the definition.</summary>
    public const string EntryPointAssembly = "KOSAPP0007";

    /// <summary>Two instantiations of the definition produced different manifests.</summary>
    public const string NonDeterministic = "KOSAPP0008";

    /// <summary><c>AppManifestValidator</c> rejected the produced manifest.</summary>
    public const string InvalidManifest = "KOSAPP0009";

    /// <summary>The entry-point type breaks the <c>AppEntryPointDiscovery</c> rules for its kind.</summary>
    public const string EntryPointKind = "KOSAPP0010";

    /// <summary>Two <c>[AppManifest]</c> classes of one assembly declare the same app id.</summary>
    public const string DuplicateIdInAssembly = "KOSAPP0011";

    /// <summary>
    /// The app project references a project outside the SDK boundary (ADR 0055 §2). Emitted by the
    /// <c>ValidateKazoOsSdkBoundary</c> MSBuild target, not by this tool; listed here so the catalog
    /// of codes stays in one place. ADR 0059 gives it this number (ADR 0055 said KOSAPP0002, already taken).
    /// </summary>
    public const string ForbiddenReference = "KOSAPP0030";

    /// <summary>
    /// The app injects a service that is neither an SDK type, a MudBlazor service nor
    /// <c>IJSRuntime</c> (ADR 0055 §3). ADR 0059 gives it this number (ADR 0055 said KOSAPP0003, already taken).
    /// </summary>
    public const string ForbiddenInjection = "KOSAPP0031";

    /// <summary>
    /// A web app (ADR 0073, section 3) requests a <c>filesystem.*</c> capability: a site's data lives
    /// in the host browser's IndexedDB, never in the OS file system.
    /// </summary>
    public const string WebFileSystem = "KOSAPP0050";

    /// <summary>
    /// A <c>Resources/AppManifest.&lt;culture&gt;.resx</c> file is malformed, names an unknown culture,
    /// app or key, or a referenced <c>localization/*.json</c> file cannot be read (ADR 0104, section 4).
    /// </summary>
    public const string InvalidLocalization = "KOSAPP0040";

    /// <summary>A manifest file read back during aggregation is missing or not strict schema-v1 JSON.</summary>
    public const string UnreadableManifest = "KOSAPP0020";

    /// <summary>The aggregated catalog is invalid (duplicate id, missing or cyclic dependency...).</summary>
    public const string InvalidCatalog = "KOSAPP0021";

    /// <summary>
    /// Aggregation (ADR 0098 §4): two apps of the host declare the same terminal command name or
    /// alias, or an app declares a name the shell runs itself (special built-ins, <c>sh</c>/<c>bash</c>/<c>hsh</c>).
    /// </summary>
    public const string DuplicateCommand = "KOSAPP0022";

    /// <summary>
    /// An asset the app declares (<c>AppDefinition.Assets</c>) has no file under the project's
    /// <c>wwwroot/</c>, so its SHA-256 cannot be computed (ADR 0098 §5).
    /// </summary>
    public const string AssetMissing = "KOSAPP0023";

    /// <summary>
    /// Aggregation (ADR 0126, section 4): a non-system app declares a negative <c>oomScoreAdj</c>.
    /// Only a system app (<c>IsSystemApp</c> on the host's <c>ProjectReference</c>) may shield itself
    /// from the OOM killer, like <c>CAP_SYS_RESOURCE</c> on Linux.
    /// </summary>
    public const string NegativeOomScoreAdjust = "KOSAPP0024";

    // KOSAPP0025 (a non-system app registering kazoos.file-view or kazoos.file-dialogs, ADR 0153
    // §4) is retired: ADR 0156 opens both contracts to any window app. The code is not reused.

    /// <summary>
    /// A file menu entry an app registers is invalid (ADR 0151 §5, ADR 0159): a bad
    /// <c>[RegisterFileContextMenu]</c> method, a method that throws or is not deterministic, a title
    /// key missing from <c>Resources/AppManifest.resx</c>, or a rule of the entries broken.
    /// </summary>
    public const string InvalidContextMenu = "KOSAPP0026";

    /// <summary>An app registers file menu entries without the <c>shell.context-menu</c> capability (ADR 0151 §6).</summary>
    public const string ContextMenuCapability = "KOSAPP0027";

    /// <summary>
    /// Aggregation (ADR 0151 §5): a file menu entry targets an app the host does not have, or one
    /// that does not declare the entry's intent.
    /// </summary>
    public const string ContextMenuTarget = "KOSAPP0028";

    /// <summary>
    /// <c>pack</c> (ADR 0182 §1): the app package could not be written, or the OS would refuse it
    /// (a declared assembly or asset is missing, or the manifest is invalid).
    /// </summary>
    public const string PackageFailed = "KOSAPP0060";

    // KOSAPP0061 is raised by the KazoOsKeyGen MSBuild target itself: KazoOsSigningKeyFile not set (ADR 0219 §1).

    /// <summary><c>keygen</c> or <c>sign</c> (ADR 0219 §1): the key file exists already, or the key or package cannot be used.</summary>
    public const string SigningFailed = "KOSAPP0062";

    /// <summary>
    /// <c>pack</c> warning (ADR 0219 §4): no author key was given, the package is published with its SHA-256 only.
    /// </summary>
    public const string UnsignedPackage = "KOSAPP0063";

    /// <summary>
    /// <c>pack</c> (ADR 0221 §1), a warning: a static web resource of the project has a path a
    /// manifest refuses (<c>..</c>, <c>#</c>, <c>?</c>) and is left out of the package.
    /// </summary>
    public const string PackageAssetSkipped = "KOSAPP0064";

    /// <summary>
    /// <c>pack</c> (ADR 0221 §1), a warning: the static web asset manifest given by <c>--web-assets</c>
    /// does not exist, so the package holds only the declared assets.
    /// </summary>
    public const string PackageWebAssetsMissing = "KOSAPP0065";

    /// <summary>The tool was called with invalid arguments.</summary>
    public const string InvalidArguments = "KOSAPP0090";

    /// <summary>The compiled app assembly could not be loaded or its types enumerated.</summary>
    public const string AssemblyLoadFailed = "KOSAPP0091";
}
