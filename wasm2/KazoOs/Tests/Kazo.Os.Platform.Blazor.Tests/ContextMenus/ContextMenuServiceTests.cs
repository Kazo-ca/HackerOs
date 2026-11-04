#pragma warning disable BL0006 // The test renderer attaches components directly, as ASP.NET Core's own component tests do.
using System.Runtime.ExceptionServices;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.Components;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.AppSdk.Components;
using Kazo.Os.Platform.Blazor.Components;
using Kazo.Os.Platform.Blazor.ContextMenus;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Blazor.Tests.ContextMenus;

/// <summary>
/// ADR 0150 §4 to §6: each window's menus and attached objects, the one menu open in the OS, and the
/// platform provider of <c>hackeros.context-menu</c>.
/// </summary>
public sealed class ContextMenuServiceTests
{
    [Fact]
    public void The_same_object_attached_again_gets_the_same_token_and_a_released_one_is_forgotten()
    {
        ContextMenuService service = new();
        using ContextMenuScope scope = service.CreateScope(context: null);
        object row = new();

        string token = (string)scope.Attach("rows", row)[HackMenuAttributes.Token];
        Assert.Equal(token, scope.Attach("rows", row)[HackMenuAttributes.Token]);
        Assert.NotEqual(token, scope.Attach("rows", new object())[HackMenuAttributes.Token]);
        Assert.NotEqual(token, scope.Attach("other", row)[HackMenuAttributes.Token]);
        // A value type is boxed anew at each render: it is matched by value.
        Assert.Equal(scope.Attach("rows", 7)[HackMenuAttributes.Token], scope.Attach("rows", 7)[HackMenuAttributes.Token]);
        Assert.Equal("rows", scope.Attach("rows", row)[HackMenuAttributes.Menu]);
        Assert.Equal(4, scope.AttachedCount);

        service.Release([token]);

        Assert.Equal(3, scope.AttachedCount);
        Assert.False(service.TryResolve(token, out _, out _, out _));
    }

    [Fact]
    public void Two_menus_of_the_same_name_in_a_window_are_an_error()
    {
        ContextMenuService service = new();
        using ContextMenuScope scope = service.CreateScope(context: null);
        using IDisposable first = scope.Register(Registration("rows", menu => menu.AddGroup().Add("open", "Open")));

        Assert.Throws<InvalidOperationException>(() => scope.Register(Registration("rows", _ => { })));

        first.Dispose();
        scope.Register(Registration("rows", _ => { })).Dispose();
    }

    [Fact]
    public async Task Opening_adjusts_a_fresh_copy_for_the_attached_object()
    {
        ContextMenuService service = new();
        using ContextMenuScope scope = service.CreateScope(context: null);
        HackMenuDefinition source = new("rows");
        HackMenuGroupDefinition group = source.AddGroup();
        group.Add("open", "Open");
        group.Add("delete", "Delete");
        object? seen = null;
        scope.Register(new HackMenuRegistration(
            "rows",
            () => source,
            new EventCallback<HackMenuOpeningEventArgs>(null, (Func<HackMenuOpeningEventArgs, Task>)(args =>
            {
                seen = args.Item;
                args.Menu.Hide("delete");
                args.Menu.AddGroup().Add("added", "Added");
                return Task.CompletedTask;
            }))));
        string token = (string)scope.Attach("rows", "row 3")[HackMenuAttributes.Token];

        Assert.True(await service.OpenFromTokenAsync(token, 40, 50, ContextMenuOrigin.Pointer));

        ContextMenuSession session = service.Current!;
        Assert.Equal("row 3", seen);
        Assert.Equal("row 3", session.Item);
        Assert.Equal((40d, 50d), (session.ClientX, session.ClientY));
        Assert.Equal(["open", null, "added"], session.Root.Rows.Select(row => row.Item?.Id));
        // The app's own definition is untouched.
        Assert.True(source.Find("delete")!.IsVisible);
        Assert.Null(source.Find("added"));
    }

    [Fact]
    public async Task A_cancelled_unknown_empty_or_broken_menu_does_not_open()
    {
        ContextMenuService service = new();
        using ContextMenuScope scope = service.CreateScope(context: null);
        scope.Register(new HackMenuRegistration(
            "cancelled",
            () => Build("cancelled", menu => menu.AddGroup().Add("open", "Open")),
            new EventCallback<HackMenuOpeningEventArgs>(null, (Func<HackMenuOpeningEventArgs, Task>)(args =>
            {
                args.Cancel = true;
                return Task.CompletedTask;
            }))));
        scope.Register(Registration("empty", menu => menu.AddGroup().Add("gone", "Gone").IsVisible = false));
        scope.Register(Registration("duplicate", menu =>
        {
            menu.AddGroup().Add("same", "A");
            menu.AddGroup().Add("same", "B");
        }));
        scope.Register(new HackMenuRegistration("throws", () => throw new InvalidOperationException("broken"), default));

        Assert.False(await scope.OpenAsync("cancelled", null, 0, 0));
        Assert.False(await scope.OpenAsync("unknown", null, 0, 0));
        Assert.False(await scope.OpenAsync("empty", null, 0, 0));
        Assert.False(await scope.OpenAsync("duplicate", null, 0, 0));
        Assert.False(await scope.OpenAsync("throws", null, 0, 0));
        Assert.False(await service.OpenFromTokenAsync("m99-1", 0, 0, ContextMenuOrigin.Pointer));
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task One_menu_is_open_in_the_whole_os_and_a_closed_window_closes_its_own()
    {
        ContextMenuService service = new();
        ContextMenuScope first = service.CreateScope(context: null);
        using ContextMenuScope second = service.CreateScope(context: null);
        first.Register(Registration("a", menu => menu.AddGroup().Add("open", "Open")));
        second.Register(Registration("b", menu => menu.AddGroup().Add("open", "Open")));
        int changes = 0;
        service.Changed += () => changes++;

        await first.OpenAsync("a", null, 0, 0);
        await second.OpenAsync("b", null, 0, 0);
        Assert.Same(second, service.Current!.Scope);

        first.Close();
        Assert.NotNull(service.Current);
        await first.OpenAsync("a", null, 0, 0);
        first.Dispose();

        Assert.Null(service.Current);
        Assert.True(first.IsDisposed);
        Assert.Equal(4, changes);
    }

    [Fact]
    public async Task An_entry_runs_once_the_menu_closed_in_the_app_context_with_the_attached_object()
    {
        MinimalAppExecutionContext context = new(Manifest("org.hackeros.tests.menu-app"));
        ContextMenuService service = new();
        using ContextMenuScope scope = service.CreateScope(context);
        object? received = null;
        IAppExecutionContext? ambient = null;
        bool openDuringAction = true;
        scope.Register(Registration("rows", menu =>
        {
            HackMenuItemDefinition open = menu.AddGroup().Add("open", "Open");
            open.OnClick = new EventCallback<object?>(null, (Func<object?, Task>)(item =>
            {
                received = item;
                ambient = HackExecutionContext.CurrentOrNull;
                openDuringAction = service.Current is not null;
                return Task.CompletedTask;
            }));
            menu.AddGroup().Add("failing", "Failing").OnClick =
                new EventCallback<object?>(null, (Func<object?, Task>)(_ => throw new InvalidOperationException("app bug")));
        }));

        await scope.OpenAsync("rows", "row 9", 0, 0);
        await service.InvokeAsync(service.Current!.Root.Rows[0].Item!);

        Assert.Equal("row 9", received);
        Assert.Same(context, ambient);
        Assert.False(openDuringAction);

        // A failing action is written to the log, the shell goes on.
        await scope.OpenAsync("rows", "row 9", 0, 0);
        await service.InvokeAsync(service.Current!.Root.Rows[2].Item!);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task The_platform_provides_the_contract_and_registers_the_markup_menu_with_the_window()
    {
        MinimalAppExecutionContext context = new(Manifest("org.hackeros.tests.menu-app"));
        ContextMenuService service = new();
        using ContextMenuScope scope = service.CreateScope(context);
        AppCatalog catalog = Assert.IsType<AppCatalog>(AppCatalog.Build([], []).Catalog);
        HackComponentHost host = new(
            catalog,
            new AppEnablementRegistry(catalog),
            (_, _) => Task.FromResult<(AppDescriptor?, string?)>((null, "none")),
            [new HackPlatformComponent(HackComponentContracts.ContextMenu, typeof(PlatformContextMenu))],
            reportError: null);
        using MenuRenderer renderer = new(host);

        MenuWindow window = await renderer.RenderAsync(context, scope);
        await renderer.WaitUntilAsync(() => window.Menu?.Controller is not null);

        IHackContextMenu controller = window.Menu!.Controller!;
        Assert.IsType<PlatformContextMenu>(controller);
        Assert.Equal("rows", controller.Name);
        Assert.True(await renderer.Dispatcher.InvokeAsync(() => controller.OpenAsync("row 1", 10, 20)));
        Assert.Equal(["open", null, "export"], service.Current!.Root.Rows.Select(row => row.Item?.Id));
        Assert.Equal(ContextMenuOrigin.Code, service.Current.Origin);
        Assert.Equal(["csv"], service.Current.Root.Rows[2].Submenu!.Rows.Select(row => row.Item?.Id));

        await renderer.Dispatcher.InvokeAsync(() => service.InvokeAsync(service.Current.Root.Rows[0].Item!));
        Assert.Equal("row 1", window.Opened);
        Assert.Same(context, window.ContextInCallback);

        // The window's own attachment resolves to the same menu.
        Assert.True(service.TryResolve((string)window.Attached![HackMenuAttributes.Token], out ContextMenuScope? found, out string? name, out object? item));
        Assert.Same(scope, found);
        Assert.Equal(("rows", "row 1"), (name, item));
    }

    private static HackMenuRegistration Registration(string name, Action<HackMenuDefinition> fill) =>
        new(name, () => Build(name, fill), default);

    private static HackMenuDefinition Build(string name, Action<HackMenuDefinition> fill)
    {
        HackMenuDefinition menu = new(name);
        fill(menu);
        return menu;
    }

    private static AppManifest Manifest(string id) => new()
    {
        Id = id,
        Name = "Menu App",
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "Context menu test.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Blazor.Tests", "MenuWindow"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    /// <summary>An app window declaring a menu in markup.</summary>
    private sealed class MenuWindow : HackComponentBase
    {
        [Parameter]
        public IAppExecutionContext Context { get; set; } = null!;

        public HackContextMenu? Menu { get; private set; }
        public IReadOnlyDictionary<string, object>? Attached { get; private set; }
        public object? Opened { get; private set; }
        public IAppExecutionContext? ContextInCallback { get; private set; }

        protected override void OnParametersSet() => AppContext = Context;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            Attached = Menus.Attach("rows", "row 1");
            builder.OpenComponent<HackContextMenu>(0);
            builder.AddComponentParameter(1, nameof(HackContextMenu.Name), "rows");
            builder.AddComponentParameter(2, nameof(HackContextMenu.ChildContent), (RenderFragment)(menu =>
            {
                menu.OpenComponent<HackMenuItem>(0);
                menu.AddComponentParameter(1, nameof(HackMenuItem.Id), "open");
                menu.AddComponentParameter(2, nameof(HackMenuItem.Title), "Open");
                menu.AddComponentParameter(3, nameof(HackMenuItem.OnClick), EventCallback.Factory.Create<object?>(this, item =>
                {
                    Opened = item;
                    ContextInCallback = HackExecutionContext.CurrentOrNull;
                }));
                menu.CloseComponent();
                menu.OpenComponent<HackMenuGroup>(4);
                menu.AddComponentParameter(5, nameof(HackMenuGroup.ChildContent), (RenderFragment)(group =>
                {
                    group.OpenComponent<HackMenuItem>(0);
                    group.AddComponentParameter(1, nameof(HackMenuItem.Id), "export");
                    group.AddComponentParameter(2, nameof(HackMenuItem.Title), "Export");
                    group.AddComponentParameter(3, nameof(HackMenuItem.ChildContent), (RenderFragment)(export =>
                    {
                        export.OpenComponent<HackMenuItem>(0);
                        export.AddComponentParameter(1, nameof(HackMenuItem.Id), "csv");
                        export.AddComponentParameter(2, nameof(HackMenuItem.Title), "CSV");
                        export.CloseComponent();
                    }));
                    group.CloseComponent();
                }));
                menu.CloseComponent();
            }));
            builder.AddComponentReferenceCapture(3, component => Menu = (HackContextMenu)component);
            builder.CloseComponent();
        }
    }

    /// <summary>Cascades the app context and the window's menus, as <c>WindowAppRenderer</c> does.</summary>
    private sealed class WindowFrame : ComponentBase
    {
        [Parameter]
        public IAppExecutionContext Context { get; set; } = null!;

        [Parameter]
        public IHackMenuHost Menus { get; set; } = null!;

        public MenuWindow? Window { get; private set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, "Value", Context);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<CascadingValue<IHackMenuHost>>(0);
                inner.AddComponentParameter(1, "Value", Menus);
                inner.AddComponentParameter(2, "IsFixed", true);
                inner.AddComponentParameter(3, "ChildContent", (RenderFragment)(content =>
                {
                    content.OpenComponent<MenuWindow>(0);
                    content.AddComponentParameter(1, nameof(MenuWindow.Context), Context);
                    content.AddComponentReferenceCapture(2, component => Window = (MenuWindow)component);
                    content.CloseComponent();
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    private sealed class MenuRenderer(IHackComponentHost host) : Renderer(new Services(host), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public async Task<MenuWindow> RenderAsync(IAppExecutionContext context, IHackMenuHost menus)
        {
            WindowFrame frame = new();
            await Dispatcher.InvokeAsync(async () =>
            {
                int id = AssignRootComponentId(frame);
                await RenderRootComponentAsync(id, ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(WindowFrame.Context)] = context,
                    [nameof(WindowFrame.Menus)] = menus,
                }));
            });
            return frame.Window!;
        }

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            for (int attempt = 0; attempt < 200 && !await Dispatcher.InvokeAsync(condition); attempt++)
            {
                await Task.Delay(10);
            }

            Assert.True(await Dispatcher.InvokeAsync(condition));
        }

        protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }

    private sealed class Services(IHackComponentHost host) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IHackComponentHost) ? host
            : serviceType == typeof(IServiceProvider) ? this
            : null;
    }
}
