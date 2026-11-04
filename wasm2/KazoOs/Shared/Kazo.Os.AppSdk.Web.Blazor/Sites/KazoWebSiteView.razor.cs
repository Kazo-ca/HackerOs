using Kazo.Os.AppSdk.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kazo.Os.AppSdk.Web.Blazor.Sites;

/// <summary>
/// Draws a Blazor site in the browser's page area (ADR 0084, sections 4 to 6). The browser gives it
/// the renderer process, the tab's address and cookies; the view builds the tab's
/// <see cref="KazoWebSite"/> (navigation, HTTP client, title), renders the root component under the
/// renderer's context, intercepts the site's links and reports to the browser what changes the
/// tab: an address within the site (<see cref="OnLocationChanged"/>), a navigation the browser must
/// make (<see cref="OnNavigate"/>), the title, and a crash (<see cref="OnFaulted"/>).
/// </summary>
/// <remarks>
/// The view runs in the browser window (its ambient context is the browser's); everything under
/// it runs as the renderer process. Link clicks arrive from JavaScript outside any Blazor event,
/// so <see cref="OnLinkClicked"/> enters the browser's scope first, as the page frame does.
/// </remarks>
public sealed partial class KazoWebSiteView : IAsyncDisposable
{
    private ElementReference _element;
    private IJSObjectReference? _module;
    private IJSObjectReference? _links;
    private DotNetObjectReference<KazoWebSiteView>? _self;
    private KazoWebSite? _site;
    private IKazoWebSiteProcess? _boundProcess;
    private bool _faulted;

    [Inject]
    private IJSRuntime JavaScript { get; set; } = default!;

    /// <summary>Gets or sets the renderer process started for the page (<see cref="IKazoWebSiteHost"/>).</summary>
    [Parameter, EditorRequired]
    public IKazoWebSiteProcess Process { get; set; } = default!;

    /// <summary>Gets or sets the tab's address; the browser changes it on back/forward within the site.</summary>
    [Parameter, EditorRequired]
    public Uri Url { get; set; } = default!;

    /// <summary>Gets or sets the tab's cookies, shared with the site's HTTP client.</summary>
    [Parameter, EditorRequired]
    public KazoCookieContainer Cookies { get; set; } = default!;

    /// <summary>Called when the site changed the tab's address without leaving: the new address and whether it replaces the current history entry.</summary>
    [Parameter]
    public Func<Uri, bool, Task>? OnLocationChanged { get; set; }

    /// <summary>Called when the site asks for a navigation of the browser (another site, or <c>forceLoad</c>): the target and whether it replaces the current entry.</summary>
    [Parameter]
    public Func<Uri, bool, Task>? OnNavigate { get; set; }

    /// <summary>Called when the site set the tab's title (<see cref="KazoPageTitle"/>).</summary>
    [Parameter]
    public Func<string, Task>? OnTitleChanged { get; set; }

    /// <summary>Called once when a component of the site threw: the browser shows its crash page and ends the renderer.</summary>
    [Parameter]
    public Func<Exception, Task>? OnFaulted { get; set; }

    /// <summary>Gets the tab's site services, once the view is bound to a process.</summary>
    public KazoWebSite? Site => _site;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        ArgumentNullException.ThrowIfNull(Process);
        ArgumentNullException.ThrowIfNull(Url);
        ArgumentNullException.ThrowIfNull(Cookies);
        if (!ReferenceEquals(_boundProcess, Process))
        {
            Bind(Process);
        }

        // Back/forward within the site: the browser moved the address, the site follows.
        if (_site is not null && !string.Equals(_site.Navigation.Uri, Url.AbsoluteUri, StringComparison.Ordinal))
        {
            _site.Navigation.SetLocation(Url.AbsoluteUri, isInterceptedLink: false);
        }
    }

    private void Bind(IKazoWebSiteProcess process)
    {
        Unbind();
        _boundProcess = process;
        _faulted = false;
        string origin = Url.GetLeftPart(UriPartial.Authority) + "/";
        KazoSiteNavigationManager navigation = new(origin, Url.AbsoluteUri);
        navigation.NavigationRequested += OnNavigationRequested;
        KazoHttpClient http = new(Cookies) { BaseAddress = new Uri(origin) };
        _site = new KazoWebSite(navigation, http, process.AppId, process.AppName);
        _site.TitleChanged += OnSiteTitleChanged;
    }

    private void Unbind()
    {
        if (_site is not null)
        {
            _site.Navigation.NavigationRequested -= OnNavigationRequested;
            _site.TitleChanged -= OnSiteTitleChanged;
            _site.Http.Dispose();
            _site = null;
        }

        _boundProcess = null;
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _self = DotNetObjectReference.Create(this);
            _module = await JavaScript.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Kazo.Os.AppSdk.Web.Blazor/Sites/KazoWebSiteView.razor.js");
            _links = await _module.InvokeAsync<IJSObjectReference>("attach", _element, _self);
        }
    }

    // The root component, rendered as a child of the error boundary.
    private RenderFragment RootContent => builder =>
    {
        builder.OpenComponent(0, Process.RootComponent);
        builder.CloseComponent();
    };

    /// <summary>Called by the script when the user clicked a link of the site.</summary>
    /// <param name="href">The link's <c>href</c>, as written.</param>
    /// <param name="forceLoad">
    /// Whether the link opens outside the site's own frame (a <c>target</c> other than <c>_self</c>):
    /// as in Blazor, such a link is a navigation of the document, so a site reaches its server-rendered
    /// pages (MVC views on the same origin) with <c>&lt;a href="/" target="_top"&gt;</c>.
    /// </param>
    [JSInvokable]
    public async Task OnLinkClicked(string href, bool forceLoad)
    {
        using IDisposable? scope = AmbientScope();
        if (_site is null || string.IsNullOrWhiteSpace(href))
        {
            return;
        }

        Uri target;
        try
        {
            target = _site.Navigation.ToAbsoluteUri(href);
        }
        catch (UriFormatException)
        {
            return;
        }

        await FollowAsync(target, replace: false, forceLoad, interceptedLink: true);
    }

    private void OnNavigationRequested(string uri, NavigationOptions options) =>
        _ = InvokeAsync(async () =>
        {
            using IDisposable? scope = AmbientScope();
            await FollowAsync(new Uri(uri), options.ReplaceHistoryEntry, options.ForceLoad, interceptedLink: false);
        });

    /// <summary>Within the site and without <c>forceLoad</c>, only the address changes; otherwise the browser navigates.</summary>
    private async Task FollowAsync(Uri target, bool replace, bool forceLoad, bool interceptedLink)
    {
        if (_site is null)
        {
            return;
        }

        if (!forceLoad && _site.Navigation.IsWithinSite(target))
        {
            _site.Navigation.SetLocation(target.AbsoluteUri, interceptedLink);
            if (OnLocationChanged is not null)
            {
                await OnLocationChanged(target, replace);
            }

            StateHasChanged();
            return;
        }

        if (OnNavigate is not null)
        {
            await OnNavigate(target, replace);
        }
    }

    private void OnSiteTitleChanged(string title) =>
        _ = InvokeAsync(async () =>
        {
            using IDisposable? scope = AmbientScope();
            if (OnTitleChanged is not null)
            {
                await OnTitleChanged(title);
            }
        });

    private async Task HandleErrorAsync(Exception exception)
    {
        if (_faulted)
        {
            return;
        }

        _faulted = true;
        StateHasChanged();
        if (OnFaulted is not null)
        {
            await OnFaulted(exception);
        }
    }

    // Callbacks reach the browser outside any Blazor event: they run in the browser's scope, as
    // the browser's own handlers do. Without a cascaded context (a test), nothing is entered.
    private IDisposable? AmbientScope() => AppContext is null ? null : KazoExecutionContext.Enter(AppContext);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Unbind();
        try
        {
            if (_links is not null)
            {
                await _links.InvokeVoidAsync("dispose");
                await _links.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit or the page is gone: nothing left to clean up on the JavaScript side.
        }

        _self?.Dispose();
    }
}
