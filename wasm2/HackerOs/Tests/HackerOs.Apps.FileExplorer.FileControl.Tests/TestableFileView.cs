using Microsoft.AspNetCore.Components.Rendering;

namespace HackerOs.Apps.FileExplorer.FileControl.Tests;

/// <summary>
/// Suppresses the real <c>FileView.razor</c> markup (which would otherwise instantiate
/// <c>FileViewDetails</c>/<c>FileViewIcons</c>/<c>FileViewTree</c>/<c>FileViewContextMenu</c> and pull in
/// MudBlazor's JSInterop-dependent render pipeline) so tests can attach a real
/// <see cref="Microsoft.AspNetCore.Components.RenderHandle"/> and exercise <see cref="FileView"/>'s C#
/// backing in isolation. See <c>doc/wasm/dev/old/Global-FileView-And-MessagingSystem/FileViewControl.md</c>.
/// </summary>
internal sealed class TestableFileView : FileView
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        // Intentionally empty: tests exercise the backing logic directly, not rendered markup.
    }
}
