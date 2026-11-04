using Kazo.Os.AppSdk.Blazor.Components;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.Platform.Blazor.ContextMenus;

/// <summary>
/// The platform's provider of the contract <c>hackeros.context-menu</c> (ADR 0150 §1), rendered in
/// place of an app's <see cref="KazoContextMenu"/>. It draws nothing there: it registers the app's
/// menu with its window's menus and hands back the controller; the menu itself is drawn by the
/// shell's <see cref="ContextMenuOverlay"/>, above every window.
/// </summary>
public sealed class PlatformContextMenu : KazoEmbeddableComponent, IHackContextMenu, IDisposable
{
    private IDisposable? _registration;
    private string? _registeredName;
    private IHackMenuHost? _registeredHost;
    private bool _controllerSent;

    [CascadingParameter]
    private IHackMenuHost? Host { get; set; }

    /// <summary>Gets or sets the menu's name in its window.</summary>
    [Parameter]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the menu's accessible name.</summary>
    [Parameter]
    public string? Label { get; set; }

    /// <summary>Gets or sets the SDK's builder of the menu from its markup entries.</summary>
    [Parameter]
    public Func<KazoMenuDefinition>? Build { get; set; }

    /// <summary>Gets or sets the app's handler run before the menu is drawn.</summary>
    [Parameter]
    public EventCallback<KazoMenuOpeningEventArgs> Opening { get; set; }

    /// <inheritdoc />
    string IHackContextMenu.Name => Name;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (_registration is not null && string.Equals(_registeredName, Name, StringComparison.Ordinal) && ReferenceEquals(_registeredHost, Host))
        {
            return;
        }

        _registration?.Dispose();
        _registration = null;
        _registeredName = Name;
        _registeredHost = Host;
        if (Host is null || string.IsNullOrWhiteSpace(Name) || Build is null)
        {
            return;
        }

        // The registration reads the current parameters at each opening: a re-render with new
        // titles or a new Opening lambda needs no new registration.
        _registration = Host.Register(new KazoMenuRegistration(
            Name,
            () => Build?.Invoke() ?? new KazoMenuDefinition(Name),
            new EventCallback<KazoMenuOpeningEventArgs>(null, (Func<KazoMenuOpeningEventArgs, Task>)(args => Opening.InvokeAsync(args)))));
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_controllerSent)
        {
            _controllerSent = true;
            await ControllerReady.InvokeAsync(this);
        }
    }

    /// <inheritdoc />
    public Task<bool> OpenAsync(object? item, double clientX, double clientY) =>
        Host?.OpenAsync(Name, item, clientX, clientY) ?? Task.FromResult(false);

    /// <inheritdoc />
    public void Close() => Host?.Close();

    /// <inheritdoc />
    public void Dispose()
    {
        _registration?.Dispose();
        _registration = null;
    }
}
