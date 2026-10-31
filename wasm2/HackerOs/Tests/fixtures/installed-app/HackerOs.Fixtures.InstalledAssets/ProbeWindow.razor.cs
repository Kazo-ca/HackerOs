using HackerOs.AppSdk.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HackerOs.Fixtures.InstalledAssets;

/// <summary>
/// Imports <c>probe.js</c> from the app's own <c>_content/</c> address, which starts a Web Worker
/// that loads <c>engine.js</c> next to it; shows what the worker answered (ADR 0206). Also imports
/// its collocated <c>ProbeWindow.razor.js</c>, which only <c>HackerOsPack</c> puts in the package
/// (ADR 0220).
/// </summary>
public partial class ProbeWindow : WindowAppBase
{
    private string _status = "starting";
    private string _collocated = "starting";

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    /// <inheritdoc />
    protected override async Task OnAppAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            await using IJSObjectReference module = await JS.InvokeAsync<IJSObjectReference>(
                "import", "./_content/HackerOs.Fixtures.InstalledAssets/probe.js");
            _status = await module.InvokeAsync<string>("run");
        }
        catch (JSException exception)
        {
            _status = $"failed: {exception.Message}";
        }

        try
        {
            await using IJSObjectReference collocated = await JS.InvokeAsync<IJSObjectReference>(
                "import", "./_content/HackerOs.Fixtures.InstalledAssets/ProbeWindow.razor.js");
            _collocated = await collocated.InvokeAsync<string>("label");
        }
        catch (JSException exception)
        {
            _collocated = $"failed: {exception.Message}";
        }

        StateHasChanged();
    }
}
