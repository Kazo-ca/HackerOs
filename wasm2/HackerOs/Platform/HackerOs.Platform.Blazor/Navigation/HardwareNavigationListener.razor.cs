using HackerOs.AppSdk.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace HackerOs.Platform.Blazor.Navigation;

/// <summary>
/// Catches, for the OS document, the gestures the host browser would apply to its tab — the mouse's Back
/// (button 4) and Forward (button 5) buttons, Alt+←/→, the Browser Back/Forward keys, and Back through the
/// tab's history (Android Back, iOS swipe, the host's Back arrow) — and hands them to
/// <see cref="HardwareNavigationService"/> (ADR 0220 §1). <c>DesktopShell</c> and <c>MobileShell</c> each
/// render one.
/// </summary>
/// <remarks>
/// The browser module holds no rule: it only cancels the browser's default action and reports the direction
/// and the source. Hosts without <see cref="HardwareNavigationService"/> render nothing and attach nothing.
/// </remarks>
public partial class HardwareNavigationListener : ComponentBase, IAsyncDisposable
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<HardwareNavigationListener>? _self;
    private HardwareNavigationService? _service;
    private int _token;

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private ILogger<HardwareNavigationListener> Logger { get; set; } = default!;

    /// <inheritdoc />
    protected override void OnInitialized() => _service = Services.GetService<HardwareNavigationService>();

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _service is null)
        {
            return;
        }

        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>(
                "import", "./_content/HackerOs.Platform.Blazor/Navigation/HardwareNavigationListener.razor.js");
            _self = DotNetObjectReference.Create(this);
            _token = await _module.InvokeAsync<int>("attachHardwareNavigation", _self);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or TaskCanceledException)
        {
            // Prerendering or a host without the module: the browser keeps its own Back behaviour.
            Logger.LogDebug(exception, "hardware navigation: browser listener not attached");
        }
    }

    /// <summary>Called by the browser module for one gesture.</summary>
    /// <param name="forward"><see langword="true"/> for Forward, <see langword="false"/> for Back.</param>
    /// <param name="source">An <see cref="AppBackSource"/> name: <c>MouseButton</c>, <c>KeyboardShortcut</c> or <c>SystemBack</c>.</param>
    [JSInvokable]
    public Task OnHardwareNavigationAsync(bool forward, string source)
    {
        if (_service is null || !Enum.TryParse(source, ignoreCase: false, out AppBackSource parsed))
        {
            return Task.CompletedTask;
        }

        var request = new HardwareNavigationRequest(
            forward ? HardwareNavigationDirection.Forward : HardwareNavigationDirection.Back,
            parsed);
        return InvokeAsync(() => _service.NavigateAsync(request));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("detachHardwareNavigation", _token);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is going away: its listeners go with it.
            }
        }

        _self?.Dispose();
        GC.SuppressFinalize(this);
    }
}
