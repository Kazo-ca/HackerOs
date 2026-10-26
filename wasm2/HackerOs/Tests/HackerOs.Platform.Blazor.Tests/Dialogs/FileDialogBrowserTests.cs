using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor.Components;
using HackerOs.AppSdk.Blazor.Files;
using HackerOs.AppSdk.Files;
using HackerOs.AppSdk.Components;
using HackerOs.Platform.Blazor.Dialogs;
using HackerOs.Platform.Blazor.Tests.Localization;
using HackerOs.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Blazor.Tests.Dialogs;

/// <summary>
/// ADR 0157: the Open / Save / Choose folder dialogs show the OS file control
/// (<c>hackeros.file-view</c>) of whichever app provides it, with the requesting app's rights, and the
/// platform's own browser when no app provides it.
/// </summary>
public sealed class FileDialogBrowserTests
{
    private static readonly HackFileViewItem Notes = new("/home/user/notes.txt", "notes.txt", false, 12, "text/plain", DateTimeOffset.UnixEpoch);
    private static readonly HackFileViewItem Readme = new("/home/user/readme.md", "readme.md", false, 12, "text/markdown", DateTimeOffset.UnixEpoch);
    private static readonly HackFileViewItem Projects = new("/home/user/projects", "projects", true, 0, "inode/directory", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task The_dialog_asks_for_the_file_control_and_runs_it_with_the_requesting_app_context()
    {
        MinimalAppExecutionContext context = new(Manifest());
        await using DialogHarness harness = await DialogHarness.RenderAsync(provided: true, new Dictionary<string, object?>
        {
            [nameof(FileDialogBrowser.Context)] = context,
            [nameof(FileDialogBrowser.FileSystem)] = context.FileSystem,
            [nameof(FileDialogBrowser.InitialDirectory)] = VirtualPath.Parse("/home/user/docs"),
            [nameof(FileDialogBrowser.AllowMultipleSelection)] = true,
            [nameof(FileDialogBrowser.Features)] = HackFileViewFeatures.CreateFolder | HackFileViewFeatures.Rename,
            [nameof(FileDialogBrowser.FileFilter)] = (Func<string, string?, bool>)((name, _) => name.EndsWith(".txt", StringComparison.Ordinal)),
        });

        FakeFileView view = harness.View!;
        Assert.Equal(HackComponentContracts.FileView, harness.Host.Contract);
        Assert.Same(context, view.Context);
        Assert.Equal("/home/user/docs", view.InitialDirectory);
        Assert.True(view.AllowMultiSelect);
        Assert.Equal(HackFileViewFeatures.CreateFolder | HackFileViewFeatures.Rename, view.Features);

        // The dialog's file type filters regular files only: folders stay, to be browsed into.
        Assert.True(view.Filter!(Notes));
        Assert.False(view.Filter(Readme));
        Assert.True(view.Filter(Projects));

        // The dialog draws its breadcrumb once the control is ready.
        Assert.Contains("file-dialog-toolbar", harness.Html, StringComparison.Ordinal);
        Assert.Contains("fake-file-view", harness.Html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task On_a_phone_one_tap_opens_an_item_in_the_dialog(bool? openOnTap, bool expected)
    {
        // ADR 0171: no double click on the mobile platform; without a platform service, the desktop.
        MinimalAppExecutionContext context = new(Manifest());
        await using DialogHarness harness = await DialogHarness.RenderAsync(provided: true, new Dictionary<string, object?>
        {
            [nameof(FileDialogBrowser.Context)] = context,
            [nameof(FileDialogBrowser.FileSystem)] = context.FileSystem,
            [nameof(FileDialogBrowser.OpenOnTap)] = openOnTap,
        });

        Assert.Equal(expected, harness.View!.OpenOnTap);
    }

    [Fact]
    public async Task An_activated_file_goes_to_the_dialog_instead_of_its_default_app()
    {
        MinimalAppExecutionContext context = new(Manifest());
        List<VirtualPath> activated = [];
        await using DialogHarness harness = await DialogHarness.RenderAsync(provided: true, new Dictionary<string, object?>
        {
            [nameof(FileDialogBrowser.Context)] = context,
            [nameof(FileDialogBrowser.FileSystem)] = context.FileSystem,
            [nameof(FileDialogBrowser.FileActivated)] = EventCallback.Factory.Create<VirtualPath>(new object(), activated.Add),
        });

        HackFileViewOpeningEventArgs file = new(Notes);
        await harness.InvokeAsync(() => harness.View!.Opening.InvokeAsync(file));
        Assert.True(file.Handled);
        Assert.Equal([VirtualPath.Parse("/home/user/notes.txt")], activated);

        // A folder is left to the control, which enters it.
        HackFileViewOpeningEventArgs folder = new(Projects);
        await harness.InvokeAsync(() => harness.View!.Opening.InvokeAsync(folder));
        Assert.False(folder.Handled);
        Assert.Single(activated);
    }

    [Fact]
    public async Task The_selection_keeps_only_the_kinds_the_dialog_accepts_and_navigation_is_reported()
    {
        MinimalAppExecutionContext context = new(Manifest());
        List<IReadOnlyList<VirtualPath>> selections = [];
        List<VirtualPath> directories = [];
        await using DialogHarness harness = await DialogHarness.RenderAsync(provided: true, new Dictionary<string, object?>
        {
            [nameof(FileDialogBrowser.Context)] = context,
            [nameof(FileDialogBrowser.FileSystem)] = context.FileSystem,
            [nameof(FileDialogBrowser.AllowFileSelection)] = false,
            [nameof(FileDialogBrowser.AllowDirectorySelection)] = true,
            [nameof(FileDialogBrowser.SelectionChanged)] = EventCallback.Factory.Create<IReadOnlyList<VirtualPath>>(new object(), selections.Add),
            [nameof(FileDialogBrowser.DirectoryChanged)] = EventCallback.Factory.Create<VirtualPath>(new object(), directories.Add),
        });

        await harness.InvokeAsync(() => harness.View!.SelectionChanged.InvokeAsync([Notes, Projects]));
        Assert.Equal([VirtualPath.Parse("/home/user/projects")], Assert.Single(selections));

        await harness.InvokeAsync(() => harness.View!.Navigated.InvokeAsync("/home/user/projects"));
        Assert.Equal([VirtualPath.Parse("/home/user/projects")], directories);
    }

    [Fact]
    public async Task An_error_of_the_control_is_shown_in_the_dialog_until_the_next_folder()
    {
        MinimalAppExecutionContext context = new(Manifest());
        await using DialogHarness harness = await DialogHarness.RenderAsync(provided: true, new Dictionary<string, object?>
        {
            [nameof(FileDialogBrowser.Context)] = context,
            [nameof(FileDialogBrowser.FileSystem)] = context.FileSystem,
        });

        await harness.InvokeAsync(() => harness.View!.ErrorOccurred.InvokeAsync("Access denied: /root"));
        Assert.Contains("Access denied: /root", harness.Html, StringComparison.Ordinal);

        await harness.InvokeAsync(() => harness.View!.Navigated.InvokeAsync("/home/user"));
        Assert.DoesNotContain("Access denied: /root", harness.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_toolbar_offers_new_folder_when_the_dialog_allows_it()
    {
        // ADR 0175: the Choose folder and Save dialogs create a folder from their toolbar, on the
        // desktop and the phone alike, with the control's own "New folder" (named, then in rename).
        MinimalAppExecutionContext context = new(Manifest());
        List<bool> fileControl = [];
        await using DialogHarness harness = await DialogHarness.RenderAsync(provided: true, new Dictionary<string, object?>
        {
            [nameof(FileDialogBrowser.Context)] = context,
            [nameof(FileDialogBrowser.FileSystem)] = context.FileSystem,
            [nameof(FileDialogBrowser.Features)] = HackFileViewFeatures.CreateFolder | HackFileViewFeatures.Rename,
            [nameof(FileDialogBrowser.FileControlChanged)] = EventCallback.Factory.Create<bool>(new object(), fileControl.Add),
        });

        Assert.Equal([true], fileControl);
        Assert.Contains("new-folder", harness.Html, StringComparison.Ordinal);
        Assert.Contains("New folder", harness.Html, StringComparison.Ordinal);

        // The control can run it here, so the button is enabled; a click runs it (UI.E2E CodeEditorOpenFolderTests).
        Assert.DoesNotMatch("class=\"toolbar-button new-folder\"[^>]*disabled", harness.Html);
    }

    [Fact]
    public async Task An_open_dialog_has_no_new_folder_button()
    {
        MinimalAppExecutionContext context = new(Manifest());
        await using DialogHarness harness = await DialogHarness.RenderAsync(provided: true, new Dictionary<string, object?>
        {
            [nameof(FileDialogBrowser.Context)] = context,
            [nameof(FileDialogBrowser.FileSystem)] = context.FileSystem,
        });

        Assert.Contains("file-dialog-toolbar", harness.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("new-folder", harness.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_provider_the_platform_browser_lists_the_folder_with_the_app_filesystem()
    {
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithDirectory("/home")
            .WithDirectory("/home/user")
            .WithFile("/home/user/notes.txt", "notes");
        MinimalAppExecutionContext context = new(Manifest(), fileSystem);
        List<VirtualPath> directories = [];
        await using DialogHarness harness = await DialogHarness.RenderAsync(provided: false, new Dictionary<string, object?>
        {
            [nameof(FileDialogBrowser.Context)] = context,
            [nameof(FileDialogBrowser.FileSystem)] = fileSystem,
            [nameof(FileDialogBrowser.InitialDirectory)] = VirtualPath.Parse("/home/user"),
            [nameof(FileDialogBrowser.DirectoryChanged)] = EventCallback.Factory.Create<VirtualPath>(new object(), directories.Add),
        });

        Assert.Null(harness.View);
        Assert.Contains("folder-browser", harness.Html, StringComparison.Ordinal);
        Assert.Contains("notes.txt", harness.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("file-dialog-toolbar", harness.Html, StringComparison.Ordinal);
        Assert.Equal([VirtualPath.Parse("/home/user")], directories);
    }

    [Fact]
    public async Task A_presentation_without_a_context_uses_the_platform_browser_without_asking_for_the_contract()
    {
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway().WithDirectory("/home").WithDirectory("/home/user");
        await using DialogHarness harness = await DialogHarness.RenderAsync(provided: true, new Dictionary<string, object?>
        {
            [nameof(FileDialogBrowser.FileSystem)] = fileSystem,
            [nameof(FileDialogBrowser.InitialDirectory)] = VirtualPath.Parse("/home/user"),
        });

        Assert.Null(harness.Host.Contract);
        Assert.Contains("folder-browser", harness.Html, StringComparison.Ordinal);
    }

    private static AppManifest Manifest() => new()
    {
        Id = "org.example.writer",
        Name = "Writer",
        Version = "1.0.0",
        PublisherId = "org.example",
        Description = "File dialog test.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Writer", "Writer.WriterWindow"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    /// <summary>Renders a <see cref="FileDialogBrowser"/> to HTML, with or without a provider of the file control.</summary>
    private sealed class DialogHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;

        private DialogHarness(ServiceProvider services, HtmlRenderer renderer, RecordingHost host)
        {
            _services = services;
            _renderer = renderer;
            Host = host;
        }

        public RecordingHost Host { get; }

        public FakeFileView? View => Host.View;

        public string Html => _renderer.Dispatcher.InvokeAsync(() => _root.ToHtmlString()).GetAwaiter().GetResult();

        public static async Task<DialogHarness> RenderAsync(bool provided, IDictionary<string, object?> parameters)
        {
            RecordingHost host = new(provided);
            ServiceCollection services = new();
            services.AddEnglishLocalizer();
            services.AddSingleton<IHackComponentHost>(host);
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            ServiceProvider provider = services.BuildServiceProvider();
            HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);
            DialogHarness harness = new(provider, renderer, host);
            harness._root = await renderer.Dispatcher.InvokeAsync(() =>
                renderer.RenderComponentAsync<FileDialogBrowser>(ParameterView.FromDictionary(parameters)));
            await renderer.Dispatcher.InvokeAsync(() => harness._root.QuiescenceTask);
            return harness;
        }

        /// <summary>Runs a callback of the control on the renderer, as the control would, and waits for the re-render.</summary>
        public async Task InvokeAsync(Func<Task> callback)
        {
            await _renderer.Dispatcher.InvokeAsync(callback);
            await _renderer.Dispatcher.InvokeAsync(() => _root.QuiescenceTask);
        }

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    /// <summary>Resolves <c>hackeros.file-view</c> to <see cref="FakeFileView"/>, or to nothing.</summary>
    private sealed class RecordingHost(bool provided) : IHackComponentHost
    {
        public string? Contract { get; private set; }

        public FakeFileView? View { get; set; }

        public Task<HackComponentResolution> ResolveAsync(string contract, CancellationToken cancellationToken = default)
        {
            Contract = contract;
            return Task.FromResult(provided
                ? HackComponentResolution.Resolved(contract, typeof(FakeFileView), "org.hackeros.file-explorer")
                : HackComponentResolution.Unavailable(contract, $"No installed app provides '{contract}'."));
        }
    }

    /// <summary>A provider of the file control that remembers what the dialog handed it.</summary>
    public sealed class FakeFileView : HackEmbeddableComponent, IHackFileView
    {
        [Inject]
        private IHackComponentHost Host { get; set; } = default!;

        [Parameter] public string InitialDirectory { get; set; } = "/";
        [Parameter] public HackFileViewMode Mode { get; set; }
        [Parameter] public EventCallback<HackFileViewMode> ModeChanged { get; set; }
        [Parameter] public HackFileViewFeatures Features { get; set; }
        [Parameter] public bool AllowMultiSelect { get; set; }
        [Parameter] public bool OpenOnTap { get; set; }
        [Parameter] public Func<HackFileViewItem, bool>? Filter { get; set; }
        [Parameter] public EventCallback<HackFileViewOpeningEventArgs> Opening { get; set; }
        [Parameter] public EventCallback<string> Navigated { get; set; }
        [Parameter] public EventCallback<IReadOnlyList<HackFileViewItem>> SelectionChanged { get; set; }
        [Parameter] public EventCallback<string> ErrorOccurred { get; set; }

        /// <summary>The context the control runs in: the requesting app's.</summary>
        public IAppExecutionContext Context => HostContext;

        public string CurrentDirectory => InitialDirectory;
        public IReadOnlyList<HackFileViewItem> Items => [];
        public IReadOnlyList<HackFileViewItem> SelectedItems => [];
        public bool CanPaste => false;
        public bool IsBusy => false;
        public event EventHandler? StateChanged { add { } remove { } }

        public Task NavigateAsync(string path, CancellationToken cancellationToken = default) => Navigated.InvokeAsync(path);
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> RevealAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void ClearSelection() { }
        public void RefreshFilter() { }
        public bool CanExecute(string command) =>
            command == HackFileMenuItems.NewFolder && (Features & HackFileViewFeatures.CreateFolder) != 0;

        public Task ExecuteAsync(string command) => Task.CompletedTask;
        public Task WhenIdleAsync() => Task.CompletedTask;

        protected override async Task OnInitializedAsync()
        {
            ((RecordingHost)Host).View = this;
            // HtmlRenderer runs no after-render step: hand the controller once initialized.
            await ControllerReady.InvokeAsync(this);
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "fake-file-view");
            builder.CloseElement();
        }
    }
}
