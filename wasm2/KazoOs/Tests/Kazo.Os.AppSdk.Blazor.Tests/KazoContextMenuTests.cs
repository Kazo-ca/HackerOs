#pragma warning disable BL0006 // The test renderer attaches components directly, as ASP.NET Core's own component tests do.
using System.Runtime.ExceptionServices;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.Components;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.AppSdk.Components;
using Kazo.Os.AppSdk.Icons;
using Kazo.Os.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.AppSdk.Blazor.Tests;

/// <summary>
/// ADR 0150 §2 and §4: the SDK's context menu model, its C# builder, its markup components and
/// <c>Menus</c>; the drawing is the platform's (tested in Platform.Blazor.Tests).
/// </summary>
public sealed class KazoContextMenuTests
{
    [Fact]
    public void Loose_entries_form_the_implicit_group_where_they_appear()
    {
        KazoMenuDefinition menu = new("files");
        new KazoMenuBuilder(menu, receiver: null)
            .Item("open", "Open", _ => Task.CompletedTask)
            .Item("preview", "Preview", _ => Task.CompletedTask)
            .Group(group => group.Item("copy", "Copy", _ => Task.CompletedTask))
            .Item("properties", "Properties", _ => Task.CompletedTask);

        Assert.Equal(
            [["open", "preview"], ["copy"], ["properties"]],
            menu.Groups.Select(group => group.Items.Select(item => item.Id).ToArray()).ToArray());
    }

    [Fact]
    public void A_fourth_level_is_refused_by_the_builder()
    {
        KazoMenuDefinition menu = new("deep");
        KazoMenuBuilder builder = new(menu, receiver: null);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            builder.Submenu("one", "One", one => one
                .Submenu("two", "Two", two => two
                    .Submenu("three", "Three", three => three
                        .Item("four", "Four", _ => Task.CompletedTask)))));

        Assert.Contains("'three'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_refuses_duplicate_ids_and_an_action_on_a_submenu_entry()
    {
        KazoMenuDefinition duplicate = new("dup");
        duplicate.AddGroup().Add("copy", "Copy");
        duplicate.AddGroup().Add("copy", "Copy again");
        Assert.Contains("two entries", Assert.Throws<InvalidOperationException>(duplicate.Validate).Message, StringComparison.Ordinal);

        KazoMenuDefinition parentWithAction = new("parent");
        KazoMenuItemDefinition parent = parentWithAction.AddGroup().Add("export", "Export");
        parent.OnClick = new EventCallback<object?>(null, (Func<object?, Task>)(_ => Task.CompletedTask));
        parent.Children.Add(new KazoMenuGroupDefinition());
        parent.Children[0].Add("csv", "CSV");
        Assert.Contains("cannot also have an action", Assert.Throws<InvalidOperationException>(parentWithAction.Validate).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clone_is_changed_without_touching_the_original()
    {
        KazoMenuDefinition menu = new("files");
        new KazoMenuBuilder(menu, receiver: null)
            .Submenu("export", "Export", export => export.Item("csv", "CSV", _ => Task.CompletedTask));

        KazoMenuDefinition copy = menu.Clone();
        copy.Hide("csv");
        copy.Disable("export");

        Assert.False(copy.Find("csv")!.IsVisible);
        Assert.True(copy.Find("export")!.IsDisabled);
        Assert.True(menu.Find("csv")!.IsVisible);
        Assert.False(menu.Find("export")!.IsDisabled);
    }

    [Fact]
    public async Task Outside_a_window_menus_attach_nothing_and_open_nothing()
    {
        KazoMenus menus = new(host: null, receiver: null);

        Assert.False(menus.IsAvailable);
        Assert.Empty(menus.Attach("files", new object()));
        menus.Define("files", menu => menu.Item("open", "Open", _ => Task.CompletedTask)).Dispose();
        Assert.False(await menus.OpenAsync("files", null, 0, 0));
    }

    [Fact]
    public void Define_registers_a_builder_called_at_each_opening()
    {
        RecordingMenuHost host = new();
        KazoMenus menus = new(host, receiver: null);
        int builds = 0;

        using IDisposable registration = menus.Define("files", menu =>
        {
            builds++;
            menu.Item("open", $"Open {builds}", _ => Task.CompletedTask, icon: KazoIcons.Vector(IconLibrary.Lucide, "folder-open"));
        });

        KazoMenuRegistration registered = Assert.Single(host.Registrations);
        Assert.Equal("Open 1", registered.Build().Find("open")!.Title);
        Assert.Equal("Open 2", registered.Build().Find("open")!.Title);
        Assert.Equal(KazoIconKind.Vector, registered.Build().Find("open")!.Icon!.Kind);
    }

    [Fact]
    public async Task Markup_entries_describe_the_menu_to_the_provider()
    {
        MinimalAppExecutionContext context = new(WindowManifest());
        RecordingMenuHost menus = new();
        using MenuRenderer renderer = new(new StubComponentHost(typeof(RecordingProvider)));
        MarkupWindow window = await renderer.RenderAsync(context, menus);
        await renderer.WaitUntilAsync(() => window.Menu?.Controller is not null);

        RecordingProvider provider = Assert.IsType<RecordingProvider>(window.Menu!.Controller);
        Assert.Equal("rows", provider.Name);
        KazoMenuDefinition definition = await renderer.Dispatcher.InvokeAsync(() => provider.Build!());
        Assert.Equal(
            [["preview"], ["export", "hidden"]],
            definition.Groups.Select(group => group.Items.Select(item => item.Id).ToArray()).ToArray());
        KazoMenuItemDefinition export = definition.Find("export")!;
        Assert.Equal(["csv", "json"], export.Children.SelectMany(group => group.Items).Select(item => item.Id));
        Assert.False(definition.Find("hidden")!.IsVisible);
        Assert.Equal("Ctrl+P", definition.Find("preview")!.Shortcut);

        // The entry's action runs in the window's app context, with the attached object.
        await renderer.Dispatcher.InvokeAsync(() => definition.Find("preview")!.OnClick.InvokeAsync("row 7"));
        Assert.Equal("row 7", window.Previewed);
        Assert.Same(context, window.ContextInCallback);
    }

    [Fact]
    public async Task Menus_on_a_component_use_the_window_host_in_cascade()
    {
        MinimalAppExecutionContext context = new(WindowManifest());
        RecordingMenuHost menus = new();
        using MenuRenderer renderer = new(new StubComponentHost(typeof(RecordingProvider)));
        MarkupWindow window = await renderer.RenderAsync(context, menus);

        Assert.Equal("rows", window.Attached?[KazoMenuAttributes.Menu]);
        Assert.Equal("token:rows:row 1", window.Attached?[KazoMenuAttributes.Token]);
    }

    [Fact]
    public async Task An_absent_provider_draws_nothing_in_place()
    {
        MinimalAppExecutionContext context = new(WindowManifest());
        using MenuRenderer renderer = new(new StubComponentHost(null));
        MarkupWindow window = await renderer.RenderAsync(context, new RecordingMenuHost());

        Assert.Null(window.Menu!.Controller);
        Assert.DoesNotContain(renderer.Texts, text => text.Contains(KazoComponentContracts.ContextMenu, StringComparison.Ordinal));
    }

    private static AppManifest WindowManifest() => new()
    {
        Id = "org.kazoos.test-context-menu",
        Name = "Context Menu Host",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "Context menu test.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.AppSdk.Blazor.Tests", "MarkupWindow"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    /// <summary>Stands for the platform's window menus.</summary>
    private sealed class RecordingMenuHost : IKazoMenuHost
    {
        public List<KazoMenuRegistration> Registrations { get; } = [];

        public IReadOnlyDictionary<string, object> Attach(string name, object? item) =>
            new Dictionary<string, object>
            {
                [KazoMenuAttributes.Menu] = name,
                [KazoMenuAttributes.Token] = $"token:{name}:{item}",
            };

        public IDisposable Register(KazoMenuRegistration registration)
        {
            Registrations.Add(registration);
            return new Removal(() => Registrations.Remove(registration));
        }

        public Task<bool> OpenAsync(string name, object? item, double clientX, double clientY) => Task.FromResult(false);

        public void Close()
        {
        }

        private sealed class Removal(Action remove) : IDisposable
        {
            public void Dispose() => remove();
        }
    }

    /// <summary>Stands for the platform's provider: keeps what the SDK component forwarded.</summary>
    public sealed class RecordingProvider : KazoEmbeddableComponent, IKazoContextMenu
    {
        [Parameter]
        public string Name { get; set; } = string.Empty;

        [Parameter]
        public Func<KazoMenuDefinition>? Build { get; set; }

        [Parameter]
        public EventCallback<KazoMenuOpeningEventArgs> Opening { get; set; }

        public Task<bool> OpenAsync(object? item, double clientX, double clientY) => Task.FromResult(false);

        public void Close()
        {
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                await ControllerReady.InvokeAsync(this);
            }
        }
    }

    /// <summary>An app window declaring a menu in markup and attaching it to an element.</summary>
    private sealed class MarkupWindow : KazoComponentBase
    {
        [Parameter]
        public IAppExecutionContext Context { get; set; } = null!;

        public KazoContextMenu? Menu { get; private set; }
        public IReadOnlyDictionary<string, object>? Attached { get; private set; }
        public object? Previewed { get; private set; }
        public IAppExecutionContext? ContextInCallback { get; private set; }

        protected override void OnParametersSet() => AppContext = Context;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            Attached = Menus.Attach("rows", "row 1");
            builder.OpenComponent<KazoContextMenu>(0);
            builder.AddComponentParameter(1, nameof(KazoContextMenu.Name), "rows");
            builder.AddComponentParameter(2, nameof(KazoContextMenu.ChildContent), (RenderFragment)(menu =>
            {
                menu.OpenComponent<KazoMenuItem>(0);
                menu.AddComponentParameter(1, nameof(KazoMenuItem.Id), "preview");
                menu.AddComponentParameter(2, nameof(KazoMenuItem.Title), "Preview");
                menu.AddComponentParameter(3, nameof(KazoMenuItem.Shortcut), "Ctrl+P");
                menu.AddComponentParameter(4, nameof(KazoMenuItem.OnClick), EventCallback.Factory.Create<object?>(this, item =>
                {
                    Previewed = item;
                    ContextInCallback = KazoExecutionContext.CurrentOrNull;
                }));
                menu.CloseComponent();

                menu.OpenComponent<KazoMenuGroup>(5);
                menu.AddComponentParameter(6, nameof(KazoMenuGroup.ChildContent), (RenderFragment)(group =>
                {
                    group.OpenComponent<KazoMenuItem>(0);
                    group.AddComponentParameter(1, nameof(KazoMenuItem.Id), "export");
                    group.AddComponentParameter(2, nameof(KazoMenuItem.Title), "Export");
                    group.AddComponentParameter(3, nameof(KazoMenuItem.ChildContent), (RenderFragment)(export =>
                    {
                        export.OpenComponent<KazoMenuItem>(0);
                        export.AddComponentParameter(1, nameof(KazoMenuItem.Id), "csv");
                        export.AddComponentParameter(2, nameof(KazoMenuItem.Title), "CSV");
                        export.CloseComponent();
                        export.OpenComponent<KazoMenuItem>(3);
                        export.AddComponentParameter(4, nameof(KazoMenuItem.Id), "json");
                        export.AddComponentParameter(5, nameof(KazoMenuItem.Title), "JSON");
                        export.CloseComponent();
                    }));
                    group.CloseComponent();

                    group.OpenComponent<KazoMenuItem>(4);
                    group.AddComponentParameter(5, nameof(KazoMenuItem.Id), "hidden");
                    group.AddComponentParameter(6, nameof(KazoMenuItem.Title), "Hidden");
                    group.AddComponentParameter(7, nameof(KazoMenuItem.IsVisible), false);
                    group.CloseComponent();
                }));
                menu.CloseComponent();
            }));
            builder.AddComponentReferenceCapture(3, component => Menu = (KazoContextMenu)component);
            builder.CloseComponent();
        }
    }

    /// <summary>Cascades the app context and the window menus around the window, as the window manager does.</summary>
    private sealed class WindowFrame : ComponentBase
    {
        [Parameter]
        public IAppExecutionContext Context { get; set; } = null!;

        [Parameter]
        public IKazoMenuHost Menus { get; set; } = null!;

        public MarkupWindow? Window { get; private set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, "Value", Context);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<CascadingValue<IKazoMenuHost>>(0);
                inner.AddComponentParameter(1, "Value", Menus);
                inner.AddComponentParameter(2, "IsFixed", true);
                inner.AddComponentParameter(3, "ChildContent", (RenderFragment)(content =>
                {
                    content.OpenComponent<MarkupWindow>(0);
                    content.AddComponentParameter(1, nameof(MarkupWindow.Context), Context);
                    content.AddComponentReferenceCapture(2, component => Window = (MarkupWindow)component);
                    content.CloseComponent();
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    private sealed class StubComponentHost(Type? provider) : IKazoComponentHost
    {
        public Task<KazoComponentResolution> ResolveAsync(string contract, CancellationToken cancellationToken = default) =>
            Task.FromResult(provider is null
                ? KazoComponentResolution.Unavailable(contract, $"No installed app provides '{contract}'.")
                : KazoComponentResolution.Resolved(contract, provider, KazoComponentResolution.PlatformProviderId));
    }

    /// <summary>Attaches components to a real render handle and keeps the texts it drew (no bUnit in this solution).</summary>
    private sealed class MenuRenderer(IKazoComponentHost host) : Renderer(new Services(host), NullLoggerFactory.Instance)
    {
        public List<string> Texts { get; } = [];

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public async Task<MarkupWindow> RenderAsync(IAppExecutionContext context, IKazoMenuHost menus)
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

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            for (int index = 0; index < renderBatch.ReferenceFrames.Count; index++)
            {
                RenderTreeFrame frame = renderBatch.ReferenceFrames.Array[index];
                if (frame.FrameType == RenderTreeFrameType.Text)
                {
                    Texts.Add(frame.TextContent);
                }
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Services(IKazoComponentHost host) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IKazoComponentHost) ? host
            : serviceType == typeof(IServiceProvider) ? this
            : null;
    }
}
