using Kazo.Os.Theming.Abstractions;
using Kazo.Os.Theming.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.Platform.Blazor.Tests.Shell;

/// <summary>Verifies the rendered attribute contract shared by theme hosts and previews.</summary>
public sealed class ThemeComponentRenderTests
{
    [Fact]
    public async Task ThemeScope_renders_validated_attributes_content_and_static_asset_link()
    {
        FrameCapturingRenderer renderer = new(EmptyServices());
        ThemeScope scope = new()
        {
            ThemeId = WellKnownThemeIds.Windows7,
            Platform = ThemePlatform.Desktop,
            AccentId = WellKnownAccentIds.Cyan,
            AnimationsEnabled = false,
            ChildContent = builder => builder.AddContent(0, "themed-content")
        };

        ArrayRange<RenderTreeFrame> frames = await renderer.RenderAsync(scope);

        AssertAttribute(frames, "data-theme", WellKnownThemeIds.Windows7);
        AssertAttribute(frames, "data-form-factor", "desktop");
        AssertAttribute(frames, "data-accent", WellKnownAccentIds.Cyan);
        AssertAttribute(frames, "data-motion", "reduced");
        Assert.Contains(frames.Array[..frames.Count], frame =>
            frame.FrameType == RenderTreeFrameType.Text
            && string.Equals(frame.TextContent, "themed-content", StringComparison.Ordinal));

        RenderFragment headContent = FindComponentChildContent<HeadContent>(frames);
        using RenderTreeBuilder headBuilder = new();
        headContent(headBuilder);
        ArrayRange<RenderTreeFrame> headFrames = headBuilder.GetFrames();
        RenderTreeFrame linkMarkup = Assert.Single(
            headFrames.Array[..headFrames.Count],
            frame => frame.FrameType == RenderTreeFrameType.Markup);
        Assert.Contains("rel=\"stylesheet\"", linkMarkup.MarkupContent, StringComparison.Ordinal);
        Assert.Contains(
            "href=\"_content/Kazo.Os.Theming.Blazor/themes.css\"",
            linkMarkup.MarkupContent,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 1.0, "standard", "100")]
    [InlineData(true, 1.25, "high", "125")]
    [InlineData(false, 1.5, "standard", "150")]
    [InlineData(true, 2.0, "high", "200")]
    // A factor outside ADR 0105's list renders at 100 %.
    [InlineData(false, 3.0, "standard", "100")]
    public async Task ThemeScope_renders_the_accessibility_attributes(
        bool highContrast, double textScale, string expectedContrast, string expectedScale)
    {
        FrameCapturingRenderer renderer = new(EmptyServices());
        ThemeScope scope = new()
        {
            HighContrast = highContrast,
            TextScale = textScale,
            ChildContent = _ => { }
        };

        ArrayRange<RenderTreeFrame> frames = await renderer.RenderAsync(scope);

        AssertAttribute(frames, "data-contrast", expectedContrast);
        AssertAttribute(frames, "data-text-scale", expectedScale);
    }

    [Fact]
    public async Task ThemeScope_rejects_a_theme_from_the_wrong_form_factor()
    {
        FrameCapturingRenderer renderer = new(EmptyServices());
        ThemeScope scope = new()
        {
            ThemeId = WellKnownThemeIds.Android,
            Platform = ThemePlatform.Desktop,
            ChildContent = _ => { }
        };

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(scope));

        Assert.Equal("themeId", exception.ParamName);
    }

    [Theory]
    [InlineData(WellKnownThemeIds.HackerOs, "desktop", "preview-taskbar")]
    [InlineData(WellKnownThemeIds.Ios, "mobile", "preview-mobile-navigation")]
    public async Task ThemePreview_renders_the_catalog_form_factor_chrome(
        string themeId,
        string expectedFormFactor,
        string expectedChromeClass)
    {
        FrameCapturingRenderer renderer = new(EmptyServices());
        ThemePreview preview = new()
        {
            ThemeId = themeId,
            AccentId = WellKnownAccentIds.Purple,
            AnimationsEnabled = false
        };

        ArrayRange<RenderTreeFrame> frames = await renderer.RenderAsync(preview);

        AssertAttribute(frames, "data-theme", themeId);
        AssertAttribute(frames, "data-form-factor", expectedFormFactor);
        AssertAttribute(frames, "data-motion", "reduced");
        AssertAttribute(frames, "role", "img");
        Assert.True(ContainsClass(frames, expectedChromeClass), $"Expected '{expectedChromeClass}' to render.");
    }

    private static RenderFragment FindComponentChildContent<TComponent>(ArrayRange<RenderTreeFrame> frames)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            RenderTreeFrame frame = frames.Array[i];
            if (frame.FrameType != RenderTreeFrameType.Component || frame.ComponentType != typeof(TComponent))
            {
                continue;
            }

            int subtreeEnd = i + frame.ComponentSubtreeLength;
            for (int j = i + 1; j < subtreeEnd; j++)
            {
                RenderTreeFrame candidate = frames.Array[j];
                if (candidate.FrameType == RenderTreeFrameType.Attribute
                    && candidate.AttributeName == nameof(HeadContent.ChildContent)
                    && candidate.AttributeValue is RenderFragment content)
                {
                    return content;
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"{typeof(TComponent).Name} ChildContent was not found.");
    }

    private static void AssertAttribute(ArrayRange<RenderTreeFrame> frames, string name, string expectedValue) =>
        Assert.Contains(frames.Array[..frames.Count], frame =>
            frame.FrameType == RenderTreeFrameType.Attribute
            && string.Equals(frame.AttributeName, name, StringComparison.Ordinal)
            && string.Equals(frame.AttributeValue as string, expectedValue, StringComparison.Ordinal));

    private static bool ContainsClass(ArrayRange<RenderTreeFrame> frames, string className)
    {
        string markupAttribute = $"class=\"{className}\"";
        return frames.Array[..frames.Count].Any(frame =>
            frame.FrameType == RenderTreeFrameType.Attribute
                && frame.AttributeName == "class"
                && (frame.AttributeValue as string)?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(className) == true
            || frame.FrameType == RenderTreeFrameType.Markup
                && frame.MarkupContent.Contains(markupAttribute, StringComparison.Ordinal));
    }

    private static IServiceProvider EmptyServices() => new ServiceCollection().BuildServiceProvider();
}
