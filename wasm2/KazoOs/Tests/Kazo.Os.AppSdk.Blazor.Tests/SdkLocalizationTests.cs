#pragma warning disable BL0006 // The test renderer reads render batches directly, as ASP.NET Core's own component tests do.
using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.ExceptionServices;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.AppSdk.Blazor.Tests;

/// <summary>
/// ADR 0104: the SDK's controls speak the language of the app they render in — the cascaded
/// <see cref="IAppExecutionContext"/>'s <c>LANG</c> — and fall back to English without one.
/// </summary>
public sealed class SdkLocalizationTests
{
    [Fact]
    public async Task A_setting_badge_rendered_in_a_french_app_is_written_in_french()
    {
        MinimalAppExecutionContext french = Context("fr_FR.UTF-8");
        MinimalAppExecutionContext english = Context("en_US.UTF-8");

        IReadOnlyList<string> inFrench = await RenderBadgeAsync(french, locked: true);
        IReadOnlyList<string> inEnglish = await RenderBadgeAsync(english, locked: true);
        IReadOnlyList<string> withoutContext = await RenderBadgeAsync(null, locked: false);

        Assert.Contains("verrouillé par l'administrateur", inFrench);
        Assert.Contains(inFrench, text => text.StartsWith("Un administrateur a fixé cette valeur", StringComparison.Ordinal));
        Assert.Contains("locked by admin", inEnglish);
        Assert.Contains("built-in", withoutContext);
    }

    [Fact]
    public void Every_sdk_text_exists_in_english_and_in_french()
    {
        ResourceManager resources = new("Kazo.Os.AppSdk.Blazor.Resources.Strings", typeof(WindowAppBase).Assembly);

        HashSet<string> english = Keys(resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false));
        HashSet<string> french = Keys(resources.GetResourceSet(CultureInfo.GetCultureInfo("fr"), createIfNotExists: true, tryParents: false));

        Assert.NotEmpty(english);
        Assert.Empty(english.Except(french));
        Assert.Empty(french.Except(english));
    }

    private static HashSet<string> Keys(ResourceSet? set) =>
        set is null ? [] : [.. set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key)];

    private static MinimalAppExecutionContext Context(string lang)
    {
        FakeAppEnvironmentGateway environment = new("/home/alice", "alice");
        environment.SetVariable("LANG", lang);
        return new MinimalAppExecutionContext(Manifest(), environment: environment);
    }

    private static async Task<IReadOnlyList<string>> RenderBadgeAsync(IAppExecutionContext? context, bool locked)
    {
        using TextRenderer renderer = new();
        await renderer.RenderAsync(new BadgeHost { Context = context, Locked = locked });
        return renderer.Texts;
    }

    private static AppManifest Manifest() => new()
    {
        Id = "org.kazoos.test-localization",
        Name = "Localization Probe",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "SDK localization test.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.AppSdk.Blazor.Tests", "BadgeHost"),
        SdkCompatibility = new AppSdkCompatibilityManifest("1.0.0"),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    /// <summary>Cascades the app context (when any) around a badge, as the window host does.</summary>
    private sealed class BadgeHost : ComponentBase
    {
        public IAppExecutionContext? Context { get; init; }

        public bool Locked { get; init; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            RenderFragment badge = child =>
            {
                child.OpenComponent<SettingOriginBadge>(0);
                child.AddComponentParameter(1, nameof(SettingOriginBadge.Source), SettingLayer.Default);
                child.AddComponentParameter(2, nameof(SettingOriginBadge.IsLocked), Locked);
                child.CloseComponent();
            };
            if (Context is null)
            {
                builder.AddContent(0, badge);
                return;
            }

            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(1);
            builder.AddComponentParameter(2, "Value", Context);
            builder.AddComponentParameter(3, "IsFixed", true);
            builder.AddComponentParameter(4, "ChildContent", badge);
            builder.CloseComponent();
        }
    }

    /// <summary>Collects every text and string attribute the render batches carry (no bUnit in this solution).</summary>
    private sealed class TextRenderer() : Renderer(new EmptyServices(), NullLoggerFactory.Instance)
    {
        private readonly List<string> _texts = [];

        public IReadOnlyList<string> Texts => _texts;

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public Task RenderAsync(IComponent component) =>
            Dispatcher.InvokeAsync(async () => await RenderRootComponentAsync(AssignRootComponentId(component)));

        protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            for (int index = 0; index < renderBatch.ReferenceFrames.Count; index++)
            {
                RenderTreeFrame frame = renderBatch.ReferenceFrames.Array[index];
                if (frame.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
                {
                    _texts.Add(frame.TextContent.Trim());
                }
                else if (frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeValue is string value)
                {
                    _texts.Add(value);
                }
            }

            return Task.CompletedTask;
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
