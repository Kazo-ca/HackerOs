using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.AppSdk.Files;
using Kazo.Os.Platform.Blazor.ContextMenus;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Kazo.Os.Platform.Blazor.Shell;

/// <summary>
/// Starts the desktop app (<c>org.hackeros.desktop</c>, ADR 0160 §1) for the signed-in user and renders
/// it under the windows, in <see cref="Kazo.Os.Windowing.Blazor.DesktopArea.BackgroundContent"/>, like a file manager drawing the
/// desktop of a real session (Nautilus's or Explorer's desktop window): a process of its own, with its
/// own rights, that has no window.
/// </summary>
/// <remarks>
/// <para>
/// The app is rendered the way <see cref="WindowAppRenderer"/> renders a window's content: its execution
/// context and its context-menu scope are cascaded, and its entry point receives the context. The window
/// coordinator is told the app is a shell surface, so the process's <c>Running</c> state never opens a
/// window for it.
/// </para>
/// <para>
/// The desktop app is optional: a host that does not ship it, or a launch refused, leaves an empty
/// desktop. A process that faults is started again once; a second fault leaves the desktop empty for
/// the session.
/// </para>
/// </remarks>
public sealed class DesktopSurfaceHost : ComponentBase, IDisposable
{
    /// <summary>The id of the desktop app.</summary>
    public const string DesktopAppId = "org.hackeros.desktop";

    private IDisposable? _reservation;
    private IDisposable? _processSubscription;
    private ContextMenuScope? _menus;
    private ProcessId? _pid;
    private AppDescriptor? _descriptor;
    private IAppExecutionContext? _context;
    private int _launches;
    private bool _disposed;

    [Inject]
    private AppLifecycleOrchestrator Orchestrator { get; set; } = null!;

    [Inject]
    private ISessionService Sessions { get; set; } = null!;

    [Inject]
    private WindowLaunchCoordinator LaunchCoordinator { get; set; } = null!;

    [Inject]
    private IEventBus EventBus { get; set; } = null!;

    /// <summary>Gets the services, to find the optional <see cref="ContextMenuService"/>.</summary>
    [Inject]
    private IServiceProvider Services { get; set; } = null!;

    /// <summary>Gets the process of the desktop app, while it runs.</summary>
    public ProcessId? ProcessId => _pid;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        _reservation = LaunchCoordinator.ReserveShellSurface(DesktopAppId);
        _processSubscription = EventBus.Subscribe<ProcessStateChangedEvent>(OnProcessStateChanged);
        await LaunchAsync();
    }

    private async Task LaunchAsync()
    {
        if (_disposed || _launches >= 2 || Sessions.CurrentPrincipal is not AuthenticatedPrincipal principal)
        {
            return;
        }

        _launches++;
        AppLaunchResult result = await Orchestrator.LaunchAsync(new AppLaunchRequest(DesktopAppId, principal, []));
        if (_disposed || !result.IsSuccess || result.Process is not ProcessRecord process
            || !Orchestrator.TryGetRunningInstance(process.Pid, out AppDescriptor? descriptor, out IAppExecutionContext? context))
        {
            return;
        }

        _pid = process.Pid;
        _descriptor = descriptor;
        _context = context;
        _menus?.Dispose();
        // ADR 0150 §4: Menus.Attach finds the desktop's menus in cascade, as a window's.
        _menus = Services.GetService(typeof(ContextMenuService)) is ContextMenuService menus ? menus.CreateScope(context) : null;
        StateHasChanged();
    }

    /// <summary>
    /// "Send to ▸ Desktop" from the Start menu (ADR 0160 §6): hands the app id to the running desktop,
    /// as a second activation carrying the <see cref="KazoDesktopItems.CreateShortcutIntent"/> intent.
    /// </summary>
    /// <param name="appId">The app the shortcut opens.</param>
    /// <returns>Whether the desktop received it.</returns>
    public async Task<bool> CreateShortcutAsync(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (_pid is null || Sessions.CurrentPrincipal is not AuthenticatedPrincipal principal)
        {
            return false;
        }

        string payload = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, string> { ["appId"] = appId },
            DesktopSurfaceJsonContext.Default.DictionaryStringString);
        AppLaunchResult result = await Orchestrator.LaunchAsync(new AppLaunchRequest(
            DesktopAppId,
            principal,
            [],
            Intent: new AppIntentDelivery(KazoDesktopItems.CreateShortcutIntent, payload, ShellSenderId)));
        return result.IsSuccess;
    }

    /// <summary>The sender of the shell's intents.</summary>
    private const string ShellSenderId = "org.hackeros.shell";

    private void OnProcessStateChanged(ProcessStateChangedEvent e)
    {
        if (_pid != e.Pid || e.NewState is not (ProcessState.Stopped or ProcessState.Faulted))
        {
            return;
        }

        bool faulted = e.NewState == ProcessState.Faulted;
        _ = InvokeAsync(async () =>
        {
            Clear();
            StateHasChanged();
            if (faulted)
            {
                await LaunchAsync();
            }
        });
    }

    private void Clear()
    {
        _pid = null;
        _descriptor = null;
        _context = null;
        _menus?.Dispose();
        _menus = null;
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (_context is null || _descriptor is null)
        {
            return;
        }

        builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), _context);
        builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.IsFixed), true);
        builder.AddComponentParameter(3, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)RenderWithMenus);
        builder.SetKey(_context);
        builder.CloseComponent();
    }

    private void RenderWithMenus(RenderTreeBuilder builder)
    {
        if (_menus is null)
        {
            RenderEntryPoint(builder);
            return;
        }

        builder.OpenComponent<CascadingValue<IHackMenuHost>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<IHackMenuHost>.Value), _menus);
        builder.AddComponentParameter(2, nameof(CascadingValue<IHackMenuHost>.IsFixed), true);
        builder.AddComponentParameter(3, nameof(CascadingValue<IHackMenuHost>.ChildContent), (RenderFragment)RenderEntryPoint);
        builder.CloseComponent();
    }

    private void RenderEntryPoint(RenderTreeBuilder builder)
    {
        if (_descriptor is null || _context is null)
        {
            return;
        }

        builder.OpenComponent(0, _descriptor.EntryPointType);
        builder.AddComponentParameter(1, nameof(WindowAppBase.AppContext), _context);
        builder.CloseComponent();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _processSubscription?.Dispose();
        _reservation?.Dispose();
        _menus?.Dispose();
        _menus = null;
    }
}

/// <summary>Source-generated serializer of the shell's intent payloads (trim-safe).</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class DesktopSurfaceJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
