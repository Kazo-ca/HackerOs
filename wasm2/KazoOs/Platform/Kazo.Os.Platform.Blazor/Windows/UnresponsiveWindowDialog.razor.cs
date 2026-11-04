using Kazo.Os.Windowing.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Kazo.Os.Platform.Blazor.Windows;

/// <summary>
/// "« App » ne répond pas. Vous pouvez attendre qu'elle réponde ou terminer l'application." with
/// <em>Attendre</em> (the default, also <c>Échap</c>) and <em>Terminer l'application</em>
/// (ADR 0127 §3). The shell renders it inside the unresponsive window's overlay, so it covers that
/// window's content only.
/// </summary>
/// <remarks>
/// The dialog is an <c>alertdialog</c> that takes the keyboard when it appears (on <em>Attendre</em>)
/// and keeps <c>Tab</c> inside its two buttons (<c>UnresponsiveWindowDialog.razor.js</c>), as
/// required by the accessibility rules of ADR 0105.
/// </remarks>
public sealed partial class UnresponsiveWindowDialog : IAsyncDisposable
{
    private ElementReference _veil;
    private ElementReference _waitButton;
    private IJSObjectReference? _module;

    /// <summary>Gets or sets the dialog's texts, in the session language (ADR 0104).</summary>
    [Inject]
    private IStringLocalizer<Strings> L { get; set; } = default!;

    /// <summary>Gets or sets the JavaScript runtime, for the focus trap only.</summary>
    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    /// <summary>Gets or sets the window the dialog belongs to.</summary>
    [Parameter, EditorRequired]
    public WindowRuntimeState Window { get; set; } = null!;

    /// <summary>Gets or sets the name of the app shown in the question (its manifest name, else the window title).</summary>
    [Parameter]
    public string? AppName { get; set; }

    /// <summary>Gets or sets the <em>Attendre</em> callback.</summary>
    [Parameter]
    public EventCallback<WindowId> OnWait { get; set; }

    /// <summary>Gets or sets the <em>Terminer l'application</em> callback.</summary>
    [Parameter]
    public EventCallback<WindowId> OnTerminate { get; set; }

    private string TitleId => $"unresponsive-title-{Window.Id.Value:N}";

    private string MessageId => $"unresponsive-message-{Window.Id.Value:N}";

    private string DisplayName => string.IsNullOrWhiteSpace(AppName) ? Window.Title : AppName;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            // Take the keyboard on the default answer, like any alert dialog.
            await _waitButton.FocusAsync();
            _module = await JS.InvokeAsync<IJSObjectReference>(
                "import",
                "./_content/Kazo.Os.Platform.Blazor/Windows/UnresponsiveWindowDialog.razor.js");
            await _module.InvokeVoidAsync("trapFocus", _veil);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JSException or JSDisconnectedException)
        {
            // Prerendering or the test renderer: the focus trap is a convenience of the browser only.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            await _module.InvokeVoidAsync("releaseFocus", _veil);
            await _module.DisposeAsync();
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException)
        {
            // The page is going away.
        }
    }

    private Task WaitAsync() => OnWait.InvokeAsync(Window.Id);

    private Task TerminateAsync() => OnTerminate.InvokeAsync(Window.Id);

    private Task HandleKeyDownAsync(KeyboardEventArgs args) =>
        args.Key == "Escape" ? WaitAsync() : Task.CompletedTask;
}
