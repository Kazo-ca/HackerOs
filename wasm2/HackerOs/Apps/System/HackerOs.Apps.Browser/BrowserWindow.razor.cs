using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using HackerOs.App.Abstractions;
using HackerOs.Apps.Browser.Bridge;
using HackerOs.Apps.Browser.DevTools;
using HackerOs.Apps.Browser.Net;
using HackerOs.Apps.Browser.Rendering;
using HackerOs.Apps.Browser.Storage;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Apps;
using HackerOs.AppSdk.Blazor;
using HackerOs.AppSdk.Blazor.Display;
using HackerOs.AppSdk.Net;
using HackerOs.AppSdk.Net.Hosting;
using HackerOs.AppSdk.Web.Blazor.Sites;
using HackerOs.AppSdk.Web.Hosting;
using HackerOs.Simulation.Abstractions.Network;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using HackerOs.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Browser;

/// <summary>
/// Code-behind for the Browser window application.
/// </summary>
/// <remarks>
/// <para>
/// Every request goes through the SDK (ADR 0056 §6): <see cref="HackHttpClient"/> for
/// <c>http(s)</c>, <c>HackFile</c> for <c>file://</c> (ADR 0075 §4). The response is shown by type
/// (ADR 0075 §5): a historical site's structured page with <see cref="SimulatedPageRenderer"/>,
/// HTML in the isolated frame of <see cref="BrowserPageView"/> after <see cref="HtmlPageComposer"/>
/// inlined its sub-resources, other bodies with <see cref="ResponseBodyViewer"/>, and anything the
/// browser cannot show is saved to <c>~/Downloads</c>.
/// </para>
/// <para>
/// All browsing state (history, cookies, <c>sessionStorage</c>, developer tools) belongs to the
/// window and is discarded on close; <c>localStorage</c> is kept in the user's files
/// (<see cref="PageStorage"/>).
/// </para>
/// <para>
/// The page can move to a detached window of the host browser (ADR 0078 §3): the same tab, drawn
/// in a display surface through <see cref="DisplaySurfaceContent"/>, still navigated and served by
/// this window. Launched with <c>--detached</c> (see <c>HackBrowser.OpenDetachedAsync</c>), the
/// window detaches before its first navigation.
/// </para>
/// <para>
/// A Blazor site (ADR 0084) is recognised by its host page: the browser reads its
/// <c>blazor.boot.json</c>, asks the OS for a renderer process (<see cref="IHackWebSiteHost"/>) and
/// draws the site's root component in the page area with <see cref="HackWebSiteView"/>. Links
/// within the site change the address without a request; back and forward between two addresses
/// of the site stay in the site; the address bar and Reload make a real request.
/// </para>
/// </remarks>
public sealed partial class BrowserWindow : WindowAppBase, IAsyncDisposable
{
    private const string HomeUrl = "https://hackersearch.net";
    private const string SearchUrl = "https://hackersearch.net/search?q=";
    private const string DocumentAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8";

    /// <summary>Default bookmarks shown on the new-tab page.</summary>
    internal static readonly IReadOnlyList<(string Label, string Url)> DefaultBookmarks =
    [
        ("HackerSearch",   "https://hackersearch.net"),
        ("HackMail",       "https://hackmail.com"),
        ("CryptoBank",     "https://cryptobank.com"),
        ("DarkNet Market", "https://darknet.market"),
        ("Hacker Forum",   "https://hackerz.forum"),
    ];

    // ── Navigation state ─────────────────────────────────────────────────
    private readonly List<Uri> _history = [];
    private int _historyPos = -1;

    private string _currentUrl      = string.Empty;
    private string _finalUrl        = string.Empty;
    private string _urlInputValue   = string.Empty;
    private bool   _isLoading;
    private BrowserErrorView? _error;
    private SimulatedPage? _currentPage;
    private ResponseBodyView? _currentBody;
    private int _currentStatus;
    private string? _downloadedTo;
    private Uri? _documentUrl;
    private bool _viewSource;
    private string _pageTitle = string.Empty;
    private string? _alertMessage;
    private string _alertOrigin = string.Empty;
    private bool _devToolsOpen;
    private CancellationTokenSource? _navigation;

    // ── HTML page (ADR 0075) ─────────────────────────────────────────────
    private PageFrameDocument? _frameDocument;
    private PageSession? _session;
    private BrowserPageView? _pageView;
    private FrameSource? _frameSource;

    // ── Blazor site (ADR 0084) ───────────────────────────────────────────
    private IHackWebSiteProcess? _site;
    private Uri? _siteUrl;
    private HackWebSiteView? _siteView;

    // ── Detached window (ADR 0078 §3) ────────────────────────────────────
    private IDisplaySurfaceService? _surfaces;
    private IDisplaySurface? _surface;
    private IDisposable? _surfaceTracking;
    private string? _detachMessage;
    private bool _detaching;

    // ── Per-window session state, sent and filled by the OS on each request ──
    private readonly HackCookieContainer _cookies = new();
    private readonly DevToolsLog _devTools = new();
    private BrowserResourceLoader _loader = default!;
    private HtmlPageComposer _composer = default!;
    private PageBridgeHandler _bridge = default!;
    private PageStorage? _storage;
    private FormHistory? _formHistory;
    private string? _notice;
    private string _homeDirectory = "/";

    // ── Computed helpers ──────────────────────────────────────────────────
    private bool CanGoBack    => _historyPos > 0;
    private bool CanGoForward => _historyPos < _history.Count - 1;
    private bool CanViewSource => _currentBody is { Kind: ResponseBodyKind.Html };
    private bool CanDetach => _surfaces is not null && !_detaching;
    private bool ShowsFrame => !_isLoading && _error is null && _currentPage is null && _frameDocument is not null && !_viewSource;
    private bool ShowsSite => !_isLoading && _error is null && _site is { HasExited: false } && _siteUrl is not null && !_viewSource;

    private IStringLocalizer<Strings>? _localizer;

    /// <summary>
    /// The browser's own texts in its process's language (ADR 0104). Web pages and developer-tools
    /// console messages stay as the sites and the web platform write them.
    /// </summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    private string SchemeIcon => _documentUrl?.Scheme switch
    {
        "https" => "\U0001F512",
        "file" => "\U0001F4C4",
        _ => "ⓘ",
    };

    private string SchemeClass => _documentUrl?.Scheme ?? "none";

    /// <summary>Where the shown page came from (ADR 0210 §4); <see langword="null"/> for an error or an internal page.</summary>
    private BrowserResponseSource? _documentSource;

    private AddressBarIndicator Indicator => AddressBarIndicators.For(_documentUrl, _documentSource, AppContext.Culture);

    private string SchemeTitle => _documentUrl?.Scheme switch
    {
        "https" => L["Scheme.Secure"],
        "file" => L["Scheme.File"],
        "http" => L["Scheme.NotSecure"],
        _ => string.Empty,
    };

    /// <summary>The session's detached windows (ADR 0078); part of the Blazor SDK contracts.</summary>
    [Inject]
    private IDisplaySurfaceService DisplaySurfaces { get; set; } = default!;

    /// <summary>Starts the renderer of a Blazor site (ADR 0084); part of the Blazor SDK contracts.</summary>
    [Inject]
    private IHackWebSiteHost SiteHost { get; set; } = default!;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override void OnAppInitialized()
    {
        _loader = new BrowserResourceLoader(new SdkBrowserTransport(_cookies), _devTools);
        _composer = new HtmlPageComposer(_loader);
        _bridge = new PageBridgeHandler(_loader, _cookies);
        _devTools.Changed += OnDevToolsChanged;
        _surfaces = DisplaySurfaces;

        try
        {
            _homeDirectory = HackUser.Current.HomeDirectory;
            _storage = new PageStorage(new HackBrowserStorageFiles(), PageStorage.GetRoot(_homeDirectory),
                message => _devTools.Add(new ConsoleLogEntry(ConsoleLevel.Warning, message, "browser")));
            // ADR 0088 §6: what the user types in forms, offered back by field name.
            _formHistory = new FormHistory(new HackBrowserStorageFiles(), FormHistory.GetPath(_homeDirectory),
                onError: message => _devTools.Add(new ConsoleLogEntry(ConsoleLevel.Warning, message, "browser")));
        }
        catch (Exception exception) when (exception is InvalidOperationException or HackExecutionContextMissingException)
        {
            // No user (a test host): pages get an in-memory storage only, and no form history.
            _storage = null;
            _formHistory = null;
        }
    }

    /// <inheritdoc/>
    protected override async Task OnAppAfterRenderAsync(bool firstRender)
    {
        RaiseNavigationAvailabilityChanged();
        if (firstRender)
        {
            // Asked to open detached (HackBrowser.OpenDetachedAsync): the window opens first, while
            // the gesture that launched the app still lets the host browser open one.
            if (LaunchArguments.Contains(HackBrowser.DetachedArgument, StringComparer.Ordinal))
            {
                await DetachAsync();
            }

            // A file passed at launch (an .html opened from the file manager) wins over the home page.
            string start = LaunchFile is { } file ? BrowserUrl.FromLocalPath(file.Value).AbsoluteUri
                : LaunchArguments.FirstOrDefault(argument => !argument.StartsWith('-')) ?? HomeUrl;
            await NavigateToAsync(start);
            return;
        }

        // The detached window's title follows the page, as a browser tab's does.
        if (_surface is { State: DisplaySurfaceState.Open } surface && surface.Title != SurfaceTitle)
        {
            await surface.SetTitleAsync(SurfaceTitle);
        }
    }

    // ── Navigation commands ───────────────────────────────────────────────

    /// <summary>Navigates to what the user typed (or a bookmark): a URL, a path or a search.</summary>
    internal async Task NavigateToAsync(string input)
    {
        AddressTarget? target = BrowserUrl.FromAddressBar(input, _homeDirectory, SearchUrl);
        if (target is null)
        {
            if (!string.IsNullOrWhiteSpace(input))
            {
                ShowNetworkError(BrowserErrorCodes.InvalidUrl, null, input);
            }

            return;
        }

        _viewSource = target.ViewSource;
        await NavigateAsync(target.Url, HistoryMode.Push);
    }

    private enum HistoryMode
    {
        Push,
        Replace,
        Traverse,
    }

    /// <summary>
    /// Loads <paramref name="url"/> in the tab, as a browser's navigation does: one document request
    /// (logged in the Network panel), then a view chosen by the response's type.
    /// </summary>
    private async Task NavigateAsync(
        Uri url,
        HistoryMode mode,
        string method = "GET",
        byte[]? body = null,
        string? contentType = null,
        bool download = false,
        bool reload = false)
    {
        // Back or forward between two addresses of the live Blazor site (ADR 0084 §5): the site
        // follows the address, no request. Reload, the address bar and links make a real one.
        if (mode == HistoryMode.Traverse && !reload && method == "GET" && !download
            && ShowsSite && _siteView?.Site is { } live && live.Navigation.IsWithinSite(url))
        {
            FollowPageHistory(url, replace: false, traverse: true);
            return;
        }

        _navigation?.Cancel();
        _navigation?.Dispose();
        _navigation = new CancellationTokenSource();
        CancellationToken cancellationToken = _navigation.Token;
        Uri? referrer = _documentUrl;

        // Leaving the document ends the site's renderer; a reload starts a new one (ADR 0084 §3).
        await StopSiteAsync();
        _devTools.Clear();
        _currentUrl = BrowserUrl.ToDisplayString(url);
        _urlInputValue = BrowserUrl.ToDisplayString(url, _viewSource);
        _isLoading = true;
        _error = null;
        _alertMessage = null;
        StateHasChanged();

        try
        {
            if (url == BrowserUrl.AboutBlank)
            {
                ClearView();
                _documentUrl = url;
                _finalUrl = url.AbsoluteUri;
                await ShowHtmlAsync(url, string.Empty, referrer, cancellationToken);
                UpdateHistory(url, mode);
                return;
            }

            List<KeyValuePair<string, string>> headers = [new("Accept", DocumentAccept)];
            if (referrer is { Scheme: "http" or "https" } && url.Scheme is "http" or "https")
            {
                headers.Add(new("Referer", referrer.GetLeftPart(UriPartial.Query)));
            }

            BrowserFetchResult result = await _loader.SendAsync(
                new BrowserRequest(method, url, headers, body, contentType), NetworkRequestKind.Document, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (result.Response is not { } response)
            {
                ShowNetworkError(result.ErrorCode ?? BrowserErrorCodes.Failed, url, BrowserUrl.ToDisplayString(url));
                UpdateHistory(url, mode);
                return;
            }

            await ShowResponseAsync(response, referrer, download, cancellationToken);
            UpdateHistory(response.Url, mode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer navigation took over.
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _isLoading = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Shows a response (ADR 0075 §5): a historical site's structured page, HTML in the isolated
    /// frame, a body the viewer can show, a download, or the browser's error page for an error
    /// status with nothing to show.
    /// </summary>
    private async Task ShowResponseAsync(BrowserResponse response, Uri? referrer, bool download, CancellationToken cancellationToken)
    {
        ClearView();
        _documentUrl = response.Url;
        _documentSource = response.Source;
        _finalUrl = BrowserUrl.ToDisplayString(response.Url);
        _currentStatus = response.StatusCode;
        _urlInputValue = BrowserUrl.ToDisplayString(response.Url, _viewSource);

        if (response.Page is not null && !download)
        {
            _currentPage = response.Page;
            _pageTitle = response.Page.Title;
            return;
        }

        if (response.Body.Length == 0 && !response.IsSuccessStatusCode)
        {
            _error = BrowserErrorPages.ForHttpStatus(response.StatusCode, response.Url, AppContext.Culture);
            return;
        }

        _currentBody = ResponseBodyPresenter.Present(response.ContentType, response.Body);
        bool attachment = response.GetHeader("Content-Disposition")?.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase) == true;
        if (download || attachment || _currentBody.Kind == ResponseBodyKind.Binary)
        {
            _currentBody = _currentBody with { Kind = ResponseBodyKind.Binary };
            await DownloadAsync(response);
            return;
        }

        if (_currentBody.Kind == ResponseBodyKind.Html)
        {
            // The host page of a Blazor site loads blazor.hack.js: the site is drawn, not the page (ADR 0084 §3).
            if (HackBlazorSiteEndpoints.IsHostPage(_currentBody.Text) && await TryStartSiteAsync(response.Url, cancellationToken))
            {
                return;
            }

            await ShowHtmlAsync(response.Url, _currentBody.Text, referrer, cancellationToken);
        }
    }

    // ── Blazor site (ADR 0084) ───────────────────────────────────────────

    /// <summary>
    /// Reads the site's <c>blazor.boot.json</c> and asks the OS for its renderer. Returns
    /// <see langword="false"/> when the host page must be shown as an HTML page instead (no
    /// platform, no boot manifest); a site that exists but cannot start gets the browser's error page.
    /// </summary>
    private async Task<bool> TryStartSiteAsync(Uri pageUrl, CancellationToken cancellationToken)
    {
        Uri bootUrl = new(new Uri(pageUrl.GetLeftPart(UriPartial.Authority) + "/"), HackBlazorBootManifest.BootPath);
        BrowserFetchResult fetched = await _loader.SendAsync(
            new BrowserRequest("GET", bootUrl, [new("Accept", "application/json")]), NetworkRequestKind.Other, cancellationToken);
        HackBlazorBootManifest? boot = null;
        if (fetched.Response is { IsSuccessStatusCode: true } bootResponse)
        {
            try
            {
                boot = JsonSerializer.Deserialize(bootResponse.Body, HackBlazorBootJsonContext.Default.HackBlazorBootManifest);
            }
            catch (JsonException exception)
            {
                _devTools.Add(new ConsoleLogEntry(ConsoleLevel.Error, $"blazor.boot.json is not valid: {exception.Message}", BrowserUrl.ToDisplayString(bootUrl)));
            }
        }

        if (boot is null || string.IsNullOrWhiteSpace(boot.AppId))
        {
            _devTools.Add(new ConsoleLogEntry(ConsoleLevel.Error,
                "Failed to start the Blazor site: _framework/blazor.boot.json could not be read.", BrowserUrl.ToDisplayString(pageUrl)));
            return false;
        }

        HackWebSiteStart start = await SiteHost.StartAsync(new HackWebSiteStartRequest(pageUrl, boot, AppContext), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (start.Process is not { } process)
        {
            _devTools.Add(new ConsoleLogEntry(ConsoleLevel.Error, $"Failed to start the Blazor site: {start.Error}", BrowserUrl.ToDisplayString(pageUrl)));
            _error = BrowserErrorPages.ForSiteNotStarted(pageUrl, start.Error ?? L["Error.SiteNotStarted.Fallback"], AppContext.Culture);
            return true;
        }

        _site = process;
        _siteUrl = pageUrl;
        _pageTitle = process.AppName;
        process.Exited += OnSiteExited;
        _devTools.Add(new ConsoleLogEntry(ConsoleLevel.Log,
            $"Blazor site {boot.AppId} started: renderer process {process.ProcessId} ({boot.RootComponent}).", "browser"));
        return true;
    }

    /// <summary>Ends the site's renderer, when there is one; <paramref name="faulted"/> records a crash.</summary>
    private async Task StopSiteAsync(bool faulted = false)
    {
        if (_site is not { } site)
        {
            return;
        }

        _site = null;
        _siteUrl = null;
        site.Exited -= OnSiteExited;
        await site.StopAsync(faulted);
    }

    /// <summary>The site changed the tab's address (a link, <c>NavigateTo</c>): the history follows, the document stays.</summary>
    private Task HandleSiteLocationChangedAsync(Uri url, bool replace)
    {
        FollowPageHistory(url, replace);
        return Task.CompletedTask;
    }

    /// <summary>The site asks for a navigation of the browser (another site, <c>forceLoad</c>).</summary>
    private Task HandleSiteNavigateAsync(Uri url, bool replace)
    {
        _viewSource = false;
        return NavigateAsync(url, replace ? HistoryMode.Replace : HistoryMode.Push);
    }

    private Task HandleSiteTitleChangedAsync(string title)
    {
        _pageTitle = title;
        StateHasChanged();
        return Task.CompletedTask;
    }

    /// <summary>A component of the site threw (ADR 0084 §4): the renderer ends in fault and the tab shows "Aw, Snap!".</summary>
    private async Task HandleSiteFaultedAsync(Exception exception)
    {
        Uri? url = _siteUrl;
        _devTools.Add(new ConsoleLogEntry(ConsoleLevel.Error, exception.ToString(), url is null ? string.Empty : BrowserUrl.ToDisplayString(url)));
        await StopSiteAsync(faulted: true);
        ShowSiteCrash(url, killed: false);
    }

    /// <summary>The OS ended the renderer (task manager, <c>kill</c>): same page as a crash. Arrives outside any Blazor event.</summary>
    private void OnSiteExited() => _ = InvokeAsync(() =>
    {
        using IDisposable scope = HackExecutionContext.Enter(AppContext);
        if (_site is not { HasExited: true } site)
        {
            return;
        }

        Uri? url = _siteUrl;
        _site = null;
        _siteUrl = null;
        site.Exited -= OnSiteExited;
        _devTools.Add(new ConsoleLogEntry(ConsoleLevel.Error,
            $"The renderer process {site.ProcessId} of this site was terminated.", url is null ? string.Empty : BrowserUrl.ToDisplayString(url)));
        ShowSiteCrash(url, killed: true);
    });

    private void ShowSiteCrash(Uri? url, bool killed)
    {
        _currentPage = null;
        _frameDocument = null;
        _error = BrowserErrorPages.ForRendererCrash(url, killed, AppContext.Culture);
        StateHasChanged();
    }

    /// <summary>Composes an HTML document and writes it in the isolated frame (ADR 0075 §1–§3).</summary>
    private async Task ShowHtmlAsync(Uri documentUrl, string html, Uri? referrer, CancellationToken cancellationToken)
    {
        string token = Guid.NewGuid().ToString("N");
        PageSession session = new(documentUrl, token, _loader, cancellationToken);
        string storageKey = session.StorageKey;
        IReadOnlyDictionary<string, string> local = _storage is null
            ? new Dictionary<string, string>()
            : await _storage.GetLocalAsync(storageKey, cancellationToken);
        IReadOnlyDictionary<string, string> sessionStorage = _storage?.GetSession(storageKey) ?? new Dictionary<string, string>();

        PageBootstrap bootstrap = new(
            token,
            documentUrl.AbsoluteUri,
            referrer is { Scheme: "http" or "https" } ? referrer.AbsoluteUri : string.Empty,
            _bridge.GetDocumentCookies(documentUrl),
            local,
            sessionStorage,
            PageStorage.Quota,
            documentUrl.Scheme == "file");

        ComposedPage page = await _composer.ComposeAsync(html, session, bootstrap);
        cancellationToken.ThrowIfCancellationRequested();
        _frameSource = new FrameSource(documentUrl, html, referrer);
        _session = session;
        _pageTitle = page.Title;
        _frameDocument = new PageFrameDocument(page.Html, token);
    }

    private async Task DownloadAsync(BrowserResponse response)
    {
        string fileName = BrowserDownloads.SuggestFileName(response.Url, response.GetHeader("Content-Disposition"));
        try
        {
            _downloadedTo = await BrowserDownloads.SaveAsync(_homeDirectory, fileName, response.Body);
            _devTools.Add(new ConsoleLogEntry(ConsoleLevel.Log, $"Downloaded {fileName} to {_downloadedTo}", "browser"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _devTools.Add(new ConsoleLogEntry(ConsoleLevel.Error, $"Download of {fileName} failed: {exception.Message}", "browser"));
        }
    }

    private void ClearView()
    {
        _documentSource = null;
        _currentPage = null;
        _currentBody = null;
        _frameDocument = null;
        _frameSource = null;
        _session = null;
        _downloadedTo = null;
        _error = null;
        _pageTitle = string.Empty;
    }

    private void ShowNetworkError(string code, Uri? url, string display)
    {
        ClearView();
        _documentUrl = url;
        _finalUrl = display;
        _currentUrl = display;
        _error = BrowserErrorPages.ForNetworkError(code, url, AppContext.Culture);
    }

    private async Task NavigateToCurrentInputAsync() =>
        await NavigateToAsync(_urlInputValue);

    private async Task NavigateHomeAsync() =>
        await NavigateToAsync(HomeUrl);

    private async Task RefreshAsync()
    {
        if (_historyPos >= 0)
        {
            await NavigateAsync(_history[_historyPos], HistoryMode.Traverse, reload: true);
        }
        else if (_currentUrl.Length > 0)
        {
            await NavigateToAsync(_currentUrl);
        }
    }

    private async Task GoBack()
    {
        if (!CanGoBack) return;
        _historyPos--;
        await NavigateAsync(_history[_historyPos], HistoryMode.Traverse);
    }

    private async Task GoForward()
    {
        if (!CanGoForward) return;
        _historyPos++;
        await NavigateAsync(_history[_historyPos], HistoryMode.Traverse);
    }

    private async Task ToggleViewSourceAsync()
    {
        _viewSource = !_viewSource;
        if (_documentUrl is not null)
        {
            _urlInputValue = BrowserUrl.ToDisplayString(_documentUrl, _viewSource);
        }

        await Task.CompletedTask;
    }

    private void ToggleDevTools() => _devToolsOpen = !_devToolsOpen;

    private void DismissAlert() => _alertMessage = null;

    private void DismissDetachMessage() => _detachMessage = null;

    // ── Detached window (ADR 0078 §3) ────────────────────────────────────

    /// <summary>The source of the document in the frame, to write it again after it moved.</summary>
    private sealed record FrameSource(Uri DocumentUrl, string Html, Uri? Referrer);

    private string SurfaceTitle => string.IsNullOrWhiteSpace(_pageTitle)
        ? (string.IsNullOrEmpty(_finalUrl) ? L["NewTab.Title"] : _finalUrl)
        : _pageTitle;

    private Task ToggleDetachedAsync() => _surface is null ? DetachAsync() : ReattachAsync();

    /// <summary>
    /// Moves the page to a new detached window. Runs from the user's click (or right after the
    /// launch that the click started): the host browser only opens windows after a gesture.
    /// </summary>
    private async Task DetachAsync()
    {
        if (_surfaces is null || _surface is not null || _detaching)
        {
            return;
        }

        _detaching = true;
        try
        {
            DisplaySurfaceOpenResult result = await _surfaces.OpenAsync(new DisplaySurfaceOptions(SurfaceTitle));
            switch (result.Status)
            {
                case DisplaySurfaceOpenStatus.Opened when result.Surface is { } surface:
                    _detachMessage = null;
                    _surface = surface;
                    surface.Changed += OnSurfaceChanged;
                    // ADR 0095 §1: the detached window's focus and size reach the window hooks too.
                    _surfaceTracking = TrackDisplaySurface(surface);
                    await RewriteFrameAsync();
                    break;
                case DisplaySurfaceOpenStatus.Blocked:
                    _detachMessage = L["Detach.Blocked"];
                    _devTools.Add(new ConsoleLogEntry(ConsoleLevel.Warning, "The host browser blocked the detached window (pop-up blocker).", "browser"));
                    break;
                default:
                    _detachMessage = L["Detach.Unsupported"];
                    break;
            }
        }
        finally
        {
            _detaching = false;
            StateHasChanged();
        }
    }

    /// <summary>Brings the page back into this window and closes the detached one.</summary>
    private async Task ReattachAsync()
    {
        if (_surface is not { } surface)
        {
            return;
        }

        ForgetSurface();
        await surface.CloseAsync();
        await RewriteFrameAsync();
        StateHasChanged();
    }

    private async Task ShowDetachedAsync()
    {
        if (_surface is { } surface)
        {
            await surface.FocusAsync();
        }
    }

    private void ForgetSurface()
    {
        if (_surface is { } surface)
        {
            surface.Changed -= OnSurfaceChanged;
            _surface = null;
            _surfaceTracking?.Dispose();
            _surfaceTracking = null;
        }
    }

    /// <summary>
    /// The surface changed outside any Blazor event: when the user closed the detached window (or
    /// navigated it away), the page comes back into this window, as a browser does when a detached
    /// tab's window closes… except that the tab survives.
    /// </summary>
    private void OnSurfaceChanged(IDisplaySurface surface)
    {
        if (surface.State == DisplaySurfaceState.Open || !ReferenceEquals(surface, _surface))
        {
            return;
        }

        _ = InvokeAsync(async () =>
        {
            using IDisposable scope = HackExecutionContext.Enter(AppContext);
            if (!ReferenceEquals(surface, _surface))
            {
                return;
            }

            ForgetSurface();
            await RewriteFrameAsync();
            StateHasChanged();
        });
    }

    /// <summary>
    /// Writes the current HTML page again from the document already received (no new request to
    /// the server): a frame that changes window starts a new browsing context, so the page restarts
    /// with the cookies and <c>localStorage</c> it has now, like a reload served from cache.
    /// </summary>
    private async Task RewriteFrameAsync()
    {
        if (_frameSource is not { } source || _frameDocument is null)
        {
            return;
        }

        try
        {
            await ShowHtmlAsync(source.DocumentUrl, source.Html, source.Referrer, _navigation?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // A navigation started meanwhile: it writes its own document.
        }
    }

    private void OnDevToolsChanged() => _ = InvokeAsync(StateHasChanged);

    private async Task HandleInPageNavigationAsync(string href)
    {
        if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#')) return;

        // Resolve relative hrefs against current host
        if (href.StartsWith('/') && !string.IsNullOrEmpty(_finalUrl))
        {
            try
            {
                var uri = new Uri(_finalUrl);
                href = $"{uri.Scheme}://{uri.Host}{href}";
            }
            catch (UriFormatException) { }
        }

        await NavigateToAsync(href);
    }

    // ── Form submission (login/generic forms rendered by SimulatedPageRenderer) ──

    private async Task HandleFormSubmitAsync((string PostPath, ImmutableDictionary<string, string> FormBody) submission)
    {
        string url = ResolveSubmitUrl(_finalUrl, submission.PostPath);
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? target))
        {
            return;
        }

        // Redirects are followed by the OS: a login answered by 302 lands on the target page.
        SimulatedHttpBody form = SimulatedHttpBody.FormUrlEncoded(submission.FormBody);
        await NavigateAsync(target, HistoryMode.Push, "POST", form.ToArray(), form.ContentType);
    }

    /// <summary>
    /// Resolves a form's relative or absolute post path against the current page's URL. Pure
    /// (no instance state) so it can be unit-tested without rendering the component.
    /// </summary>
    public static string ResolveSubmitUrl(string finalUrl, string postPath)
    {
        if (string.IsNullOrEmpty(finalUrl))
        {
            return string.Empty;
        }

        if (!postPath.StartsWith('/'))
        {
            return postPath;
        }

        try
        {
            Uri baseUri = new(finalUrl);
            return $"{baseUri.Scheme}://{baseUri.Host}{postPath}";
        }
        catch (UriFormatException)
        {
            return string.Empty;
        }
    }

    // ── Page bridge (ADR 0075 §3) ────────────────────────────────────────

    private async Task<string> HandleBridgeRequestAsync(string operation, string payload)
    {
        PageSession? session = _session;
        if (session is null)
        {
            return "{}";
        }

        switch (operation)
        {
            case "fetch":
                BridgeFetchRequest? fetch = JsonSerializer.Deserialize(payload, PageBridgeJsonContext.Default.BridgeFetchRequest);
                BridgeFetchReply reply = fetch is null
                    ? BridgeFetchReply.Fail("Failed to fetch")
                    : await _bridge.FetchAsync(session, fetch);
                return JsonSerializer.Serialize(reply, PageBridgeJsonContext.Default.BridgeFetchReply);
            case "formhistory":
                BridgeFormHistoryRequest? field = JsonSerializer.Deserialize(payload, PageBridgeJsonContext.Default.BridgeFormHistoryRequest);
                IReadOnlyList<string> values = _formHistory is null || field?.Name is not { Length: > 0 } name
                    ? []
                    : await _formHistory.SuggestAsync(name, session.CancellationToken);
                return JsonSerializer.Serialize(new BridgeFormHistoryReply(values), PageBridgeJsonContext.Default.BridgeFormHistoryReply);
            case "resource":
                BridgeResourceRequest? resource = JsonSerializer.Deserialize(payload, PageBridgeJsonContext.Default.BridgeResourceRequest);
                BridgeResourceReply loaded = resource is null
                    ? new BridgeResourceReply(DataUrl.Empty)
                    : await _bridge.LoadResourceAsync(session, resource);
                return JsonSerializer.Serialize(loaded, PageBridgeJsonContext.Default.BridgeResourceReply);
            default:
                return "{}";
        }
    }

    private async Task HandleBridgeEventAsync(string type, string payload)
    {
        PageSession? session = _session;
        if (session is null)
        {
            return;
        }

        switch (type)
        {
            case "navigate" when JsonSerializer.Deserialize(payload, PageBridgeJsonContext.Default.BridgeNavigateEvent) is { } navigation:
                await InvokeAsync(() => NavigateFromPageAsync(session, navigation));
                return;
            case "fragment" when JsonSerializer.Deserialize(payload, PageBridgeJsonContext.Default.BridgeNavigateEvent) is { } fragment
                                 && Uri.TryCreate(fragment.Url, UriKind.Absolute, out Uri? fragmentUrl):
                await InvokeAsync(() => FollowPageHistory(fragmentUrl, replace: false));
                return;
            case "history" when JsonSerializer.Deserialize(payload, PageBridgeJsonContext.Default.BridgeHistoryEvent) is { } history
                                && Uri.TryCreate(history.Url, UriKind.Absolute, out Uri? historyUrl):
                await InvokeAsync(() => FollowPageHistory(historyUrl, history.Replace));
                return;
            case "console" when JsonSerializer.Deserialize(payload, PageBridgeJsonContext.Default.BridgeConsoleEvent) is { } message:
                _devTools.Add(new ConsoleLogEntry(ParseLevel(message.Level), message.Message ?? string.Empty, message.Source ?? string.Empty));
                return;
            case "storage" when JsonSerializer.Deserialize(payload, PageBridgeJsonContext.Default.BridgeStorageEvent) is { } change:
                _storage?.Apply(session.StorageKey, change);
                return;
            case "cookie" when JsonSerializer.Deserialize(payload, PageBridgeJsonContext.Default.BridgeCookieEvent) is { Value: { } assignment }:
                _bridge.ApplyDocumentCookie(session.DocumentUrl, assignment, DateTimeOffset.UtcNow);
                return;
            case "title" when JsonSerializer.Deserialize(payload, PageBridgeJsonContext.Default.BridgeTitleEvent) is { } title:
                await InvokeAsync(() =>
                {
                    _pageTitle = title.Title ?? string.Empty;
                    StateHasChanged();
                });
                return;
            case "dialog" when JsonSerializer.Deserialize(payload, PageBridgeJsonContext.Default.BridgeDialogEvent) is { } dialog:
                await InvokeAsync(() =>
                {
                    _alertOrigin = session.DocumentUrl.Scheme == "file" ? L["Alert.ThisPage"] : session.DocumentUrl.Host;
                    _alertMessage = dialog.Message ?? string.Empty;
                    StateHasChanged();
                });
                return;
            case "formhistory" when _formHistory is not null
                                    && JsonSerializer.Deserialize(payload, PageBridgeJsonContext.Default.BridgeFormSubmitEvent) is { Fields: { } fields }:
                // Before the navigation the same submission asks for: the next page already gets the values.
                await _formHistory.RecordAsync(fields.Where(pair => pair.Length == 2).Select(pair => new KeyValuePair<string, string>(pair[0], pair[1])));
                return;
            case "key" when JsonSerializer.Deserialize(payload, PageBridgeJsonContext.Default.BridgeKeyEvent) is { } key:
                await InvokeAsync(() => HandleShortcutAsync(key));
                return;
        }
    }

    /// <summary>"Clear browsing data → Form history" (Ctrl+Shift+Delete, ADR 0088 §6).</summary>
    private async Task ClearFormHistoryAsync()
    {
        if (_formHistory is null)
        {
            return;
        }

        await _formHistory.ClearAsync();
        _devTools.Add(new ConsoleLogEntry(ConsoleLevel.Log, $"Form history cleared ({_formHistory.Path}).", "browser"));
        _notice = L["Notice.FormHistoryCleared"];
        StateHasChanged();
    }

    private void DismissNotice() => _notice = null;

    private async Task NavigateFromPageAsync(PageSession session, BridgeNavigateEvent navigation)
    {
        if (!Uri.TryCreate(navigation.Url, UriKind.Absolute, out Uri? target) || target.Scheme is not ("http" or "https" or "file"))
        {
            return;
        }

        if (target.Scheme == "file" && session.DocumentUrl.Scheme != "file")
        {
            // Chromium: a web page may not link to a local file.
            _devTools.Add(new ConsoleLogEntry(ConsoleLevel.Error,
                $"Not allowed to load local resource: {BrowserUrl.ToDisplayString(target)}", BrowserUrl.ToDisplayString(session.DocumentUrl)));
            return;
        }

        if (navigation.Target is "_blank")
        {
            _devTools.Add(new ConsoleLogEntry(ConsoleLevel.Log, "Links opening a new tab open in this tab (one tab per window).", "browser"));
        }

        _viewSource = false;
        bool post = string.Equals(navigation.Method, "POST", StringComparison.OrdinalIgnoreCase);
        byte[]? body = post && navigation.Body is { Length: > 0 } encoded ? Convert.FromBase64String(encoded) : null;
        await NavigateAsync(
            target,
            navigation.Replace ? HistoryMode.Replace : HistoryMode.Push,
            post ? "POST" : "GET",
            body,
            post ? navigation.ContentType ?? SimulatedMediaTypes.FormUrlEncoded : null,
            navigation.Download);
    }

    /// <summary>A same-document navigation (fragment, <c>pushState</c>, a Blazor site's link): the address changes, the page stays.</summary>
    private void FollowPageHistory(Uri url, bool replace, bool traverse = false)
    {
        _documentUrl = url;
        _finalUrl = BrowserUrl.ToDisplayString(url);
        _urlInputValue = _finalUrl;
        if (_site is not null)
        {
            _siteUrl = url;
        }

        UpdateHistory(url, traverse ? HistoryMode.Traverse : replace ? HistoryMode.Replace : HistoryMode.Push);
        StateHasChanged();
    }

    private async Task HandleShortcutAsync(BridgeKeyEvent key)
    {
        string lower = (key.Key ?? string.Empty).ToLowerInvariant();
        if (key.Key == "F5" || (key.Ctrl && lower == "r"))
        {
            await RefreshAsync();
        }
        else if (key.Key == "F12" || (key.Ctrl && key.Shift && lower == "i"))
        {
            ToggleDevTools();
        }
        else if (key.Ctrl && lower == "u")
        {
            await ToggleViewSourceAsync();
        }
        else if (key.Ctrl && key.Shift && key.Key == "Delete")
        {
            await ClearFormHistoryAsync();
        }
        else if ((key.Alt && key.Key == "ArrowLeft") || key.Key == "BrowserBack")
        {
            // Alt+← or the mouse's Back button over the page (ADR 0220 §4): the page's iframe keeps
            // these from the OS document, so the bridge hands them to the window.
            await GoBack();
        }
        else if ((key.Alt && key.Key == "ArrowRight") || key.Key == "BrowserForward")
        {
            await GoForward();
        }

        StateHasChanged();
    }

    private Task HandleFrameEscapedAsync() => InvokeAsync(() =>
    {
        // The page set window.location (or similar) by script: the frame left the document the
        // browser wrote. Show why instead of an unbridged page.
        Uri? url = _documentUrl;
        ClearView();
        _error = BrowserErrorPages.ForScriptNavigation(AppContext.Culture);
        _devTools.Add(new ConsoleLogEntry(ConsoleLevel.Error,
            "The page navigated its frame by script (window.location); the navigation was blocked.",
            url is null ? string.Empty : BrowserUrl.ToDisplayString(url)));
        StateHasChanged();
    });

    private static ConsoleLevel ParseLevel(string? level) => level switch
    {
        "debug" => ConsoleLevel.Debug,
        "warning" => ConsoleLevel.Warning,
        "error" => ConsoleLevel.Error,
        _ => ConsoleLevel.Log,
    };

    // ── Address bar input ────────────────────────────────────────────────

    private void OnUrlInputChanged(ChangeEventArgs e) =>
        _urlInputValue = e.Value?.ToString() ?? string.Empty;

    private async Task HandleAddressKeyDownAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            await NavigateToCurrentInputAsync();
    }

    // ── Bookmarks (session-only, first slice) ────────────────────────────

    private readonly List<(string Label, string Url)> _sessionBookmarks = [..DefaultBookmarks];

    private bool _bookmarksOpen;
    private Task ToggleBookmarksAsync()
    {
        _bookmarksOpen = !_bookmarksOpen;
        StateHasChanged();
        return Task.CompletedTask;
    }

    // ── History helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Records a navigation: a new one drops the forward entries, a replacement rewrites the current
    /// entry, and back/forward/reload (traverse) only moved the position, so the list is kept.
    /// </summary>
    private void UpdateHistory(Uri url, HistoryMode mode)
    {
        switch (mode)
        {
            case HistoryMode.Traverse when _historyPos >= 0:
                // Keep the entry, but follow a redirect the server answered this time.
                _history[_historyPos] = url;
                break;
            case HistoryMode.Replace when _historyPos >= 0:
                _history[_historyPos] = url;
                break;
            default:
                if (_historyPos < _history.Count - 1)
                {
                    _history.RemoveRange(_historyPos + 1, _history.Count - _historyPos - 1);
                }

                if (_history.Count == 0 || _history[^1] != url)
                {
                    _history.Add(url);
                }

                _historyPos = _history.Count - 1;
                break;
        }
    }

    // ── Disposal ─────────────────────────────────────────────────────────

    /// <summary>Cancels the current navigation, writes pending storage and forgets session cookies.</summary>
    public async ValueTask DisposeAsync()
    {
        _navigation?.Cancel();
        _navigation?.Dispose();
        _devTools.Changed -= OnDevToolsChanged;

        // The site's renderer ends with its tab.
        await StopSiteAsync();

        // The detached window belongs to this tab: it closes with it.
        if (_surface is { } surface)
        {
            ForgetSurface();
            await surface.CloseAsync();
        }

        if (_storage is not null)
        {
            await _storage.FlushAsync();
        }

        // Session cookies are cleared with the window; nothing else holds them.
        _cookies.Clear();
    }
}
