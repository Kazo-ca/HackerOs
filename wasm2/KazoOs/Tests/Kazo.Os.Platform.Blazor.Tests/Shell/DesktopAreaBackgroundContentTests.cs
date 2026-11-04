using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.Platform.Blazor.Tests.Shell;

/// <summary>
/// Tests for `INT-011`/`INT-013` — <see cref="DesktopArea.BackgroundContent"/> renders in a dedicated
/// layer between the desktop-grid background and the window layer, and is backward compatible (renders
/// nothing extra) when not supplied. `DesktopShell`'s own `BackgroundContent` (`INT-012`) is a one-line
/// pass-through with nothing of its own to test beyond what the manifest-diffing compiler already
/// guarantees for a straight parameter forward.
/// </summary>
public sealed class DesktopAreaBackgroundContentTests
{
    [Fact]
    public async Task BackgroundContent_renders_between_the_desktop_grid_and_the_window_layer()
    {
        FrameCapturingRenderer renderer = new(EmptyServices());
        DesktopArea area = new()
        {
            Windows = [],
            BackgroundContent = builder => builder.AddContent(0, "probe")
        };

        ArrayRange<RenderTreeFrame> frames = await renderer.RenderAsync(area);

        int gridIndex = IndexOfElementWithClass(frames, "desktop-grid");
        int backgroundIndex = IndexOfElementWithClass(frames, "background-layer");
        int windowLayerIndex = IndexOfElementWithClass(frames, "window-layer");

        Assert.True(gridIndex >= 0, "desktop-grid element not found");
        Assert.True(backgroundIndex >= 0, "background-layer element not found");
        Assert.True(windowLayerIndex >= 0, "window-layer element not found");
        Assert.True(gridIndex < backgroundIndex, "background-layer must come after desktop-grid");
        Assert.True(backgroundIndex < windowLayerIndex, "background-layer must come before window-layer");
    }

    [Fact]
    public async Task Omitting_BackgroundContent_renders_no_background_layer_element()
    {
        FrameCapturingRenderer renderer = new(EmptyServices());
        DesktopArea area = new() { Windows = [] };

        ArrayRange<RenderTreeFrame> frames = await renderer.RenderAsync(area);

        Assert.True(IndexOfElementWithClass(frames, "desktop-grid") >= 0);
        Assert.True(IndexOfElementWithClass(frames, "window-layer") >= 0);
        Assert.Equal(-1, IndexOfElementWithClass(frames, "background-layer"));
    }

    /// <summary>
    /// Finds the frame index of an element whose <c>class</c> equals <paramref name="className"/> — as
    /// either an <c>Element</c> frame followed by an <c>Attribute</c> frame (an element with dynamic
    /// content, e.g. <c>window-layer</c>'s <c>@foreach</c>), or a single <c>Markup</c> frame (the Blazor
    /// compiler's static-content optimization for an element with nothing dynamic inside it, e.g.
    /// <c>desktop-grid</c> — it never varies, so the compiler serializes it to one raw-HTML frame instead
    /// of a live Element/Attribute sequence).
    /// </summary>
    private static int IndexOfElementWithClass(ArrayRange<RenderTreeFrame> frames, string className)
    {
        string classAttribute = $"class=\"{className}\"";
        for (int i = 0; i < frames.Count; i++)
        {
            RenderTreeFrame frame = frames.Array[i];
            if (frame.FrameType == RenderTreeFrameType.Markup
                && frame.MarkupContent.Contains(classAttribute, StringComparison.Ordinal))
            {
                return i;
            }

            if (frame.FrameType != RenderTreeFrameType.Element)
            {
                continue;
            }

            for (int j = i + 1; j < frames.Count && frames.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
            {
                if (frames.Array[j].AttributeName == "class"
                    && string.Equals(frames.Array[j].AttributeValue as string, className, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static IServiceProvider EmptyServices() => new ServiceCollection().BuildServiceProvider();
}
