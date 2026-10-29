using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Clipboard;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Tests.Support;

/// <summary>
/// Minimal <see cref="IAppExecutionContext"/> test double. Only the gateways a test actually
/// passes in are usable; every other gateway throws <see cref="NotSupportedException"/> so a
/// test that reaches an unexpected gateway fails loudly instead of silently succeeding.
/// </summary>
public sealed class MinimalAppExecutionContext(
    AppManifest manifest,
    IAppFileSystemGateway? fileSystem = null,
    IAppEventGateway? events = null,
    IAppNotificationGateway? notifications = null,
    IAppLoggingGateway? logging = null,
    IAppClockGateway? clock = null,
    IAppProcessGateway? processes = null,
    IAppIntentGateway? intents = null,
    ICapabilityChecker? capabilities = null,
    string userId = "user",
    AppAuthority userAuthority = AppAuthority.User,
    SessionId? sessionId = null,
    IAppFileSystemWatchGateway? watch = null,
    string? homeDirectory = null,
    IAppEnvironmentGateway? environment = null,
    bool isRootElevated = false,
    IAppClipboardGateway? clipboard = null,
    ProcessId? processId = null,
    HackerOs.AppSdk.Apps.IAppCatalogGateway? catalog = null,
    HackerOs.AppSdk.Settings.IAppSystemSettingsGateway? systemSettings = null,
    HackerOs.AppSdk.Administration.IAppScreensGateway? screens = null,
    HackerOs.AppSdk.IO.IAppDriveGateway? drives = null,
    HackerOs.AppSdk.WhatsNew.IAppWhatsNewGateway? whatsNew = null) : IAppExecutionContext
{
    public AppManifest Manifest { get; } = manifest;
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string UserId { get; } = userId;
    public AppAuthority UserAuthority { get; } = userAuthority;
    public IReadOnlySet<string> GrantedCapabilities { get; } =
        new HashSet<string>(manifest.Capabilities, StringComparer.Ordinal);
    public SessionId SessionId { get; } = sessionId ?? SessionId.FromGuid(Guid.NewGuid());
    public ProcessId ProcessId { get; } = processId ?? ProcessId.FromInt64(1);
    public CancellationToken CancellationToken => CancellationToken.None;

    public ICapabilityChecker Capabilities =>
        capabilities ?? throw Unsupported(nameof(Capabilities));
    public IAppFileSystemGateway FileSystem { get; } = fileSystem ?? new FakeAppFileSystemGateway();
    public IAppEventGateway Events => events ?? throw Unsupported(nameof(Events));
    public IAppNotificationGateway Notifications => notifications ?? throw Unsupported(nameof(Notifications));
    public IAppLoggingGateway Logging => logging ?? throw Unsupported(nameof(Logging));
    public IAppClockGateway Clock => clock ?? throw Unsupported(nameof(Clock));
    public IAppProcessGateway Processes => processes ?? throw Unsupported(nameof(Processes));
    public IAppIntentGateway Intents => intents ?? throw Unsupported(nameof(Intents));
    public IAppFileSystemWatchGateway Watch => watch ?? throw Unsupported(nameof(Watch));
    public string HomeDirectory { get; } = homeDirectory ?? $"/home/{userId}";
    public IAppEnvironmentGateway Environment { get; } =
        environment ?? new FakeAppEnvironmentGateway(homeDirectory ?? $"/home/{userId}", userId);
    public bool IsRootElevated { get; } = isRootElevated;
    public IAppClipboardGateway Clipboard => clipboard ?? throw Unsupported(nameof(Clipboard));

    // Without a catalog, the interface's own default applies (every call throws NotSupportedException).
    HackerOs.AppSdk.Apps.IAppCatalogGateway IAppExecutionContext.Catalog => catalog ?? UnsupportedCatalog.Instance;

    // Without OS settings documents, the interface's own default applies (every call throws NotSupportedException).
    HackerOs.AppSdk.Settings.IAppSystemSettingsGateway IAppExecutionContext.SystemSettings =>
        systemSettings ?? UnsupportedAdministrationGateways.SystemSettings;

    // Without screens, the interface's own default applies (a host without secondary screens).
    HackerOs.AppSdk.Administration.IAppScreensGateway IAppExecutionContext.Screens =>
        screens ?? UnsupportedAdministrationGateways.Screens;

    // Without a change list, the interface's own default applies (an empty list).
    HackerOs.AppSdk.WhatsNew.IAppWhatsNewGateway IAppExecutionContext.WhatsNew =>
        whatsNew ?? UnsupportedAdministrationGateways.WhatsNew;

    public HackerOs.AppSdk.IO.IAppDriveGateway Drives => drives ?? throw Unsupported(nameof(Drives));

    private static NotSupportedException Unsupported(string gatewayName) =>
        new($"'{gatewayName}' was not provided to this test's MinimalAppExecutionContext.");
}

/// <summary>Stands for the SDK's default catalog of a context that wires none.</summary>
file sealed class UnsupportedCatalog : HackerOs.AppSdk.Apps.IAppCatalogGateway
{
    public static readonly UnsupportedCatalog Instance = new();

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public ValueTask<IReadOnlyList<HackerOs.AppSdk.Apps.HackAppInfo>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide a Catalog gateway.");

    public ValueTask<IReadOnlyList<HackerOs.AppSdk.Apps.HackAppInfo>> GetHandlersAsync(
        VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide a Catalog gateway.");
}
