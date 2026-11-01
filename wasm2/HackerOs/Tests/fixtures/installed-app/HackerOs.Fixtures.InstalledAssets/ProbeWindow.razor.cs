using System.Globalization;
using System.Resources;
using HackerOs.AppSdk.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HackerOs.Fixtures.InstalledAssets;

/// <summary>
/// Imports <c>probe.js</c> from the app's own <c>_content/</c> address, which starts a Web Worker
/// that loads <c>engine.js</c> next to it; shows what the worker answered (ADR 0206). Also imports
/// its collocated <c>ProbeWindow.razor.js</c>, which only <c>HackerOsPack</c> puts in the package
/// (ADR 0221). Shows its French greeting, read from the satellite assembly
/// <c>lib/fr/HackerOs.Fixtures.InstalledAssets.resources.dll</c> of the package (ADR 0222).
/// </summary>
public partial class ProbeWindow : WindowAppBase
{
    private string _status = "starting";
    private string _collocated = "starting";

    // ADR 0222: French whatever the user's language, so the test does not depend on the session's.
    private readonly string _french = ReadFrenchGreeting();

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

    private static string ReadFrenchGreeting()
    {
        try
        {
            ResourceManager strings = new("HackerOs.Fixtures.InstalledAssets.Resources.ProbeStrings", typeof(ProbeWindow).Assembly);
            return strings.GetString("Greeting", CultureInfo.GetCultureInfo("fr")) ?? "missing";
        }
        catch (Exception exception) when (exception is MissingManifestResourceException or CultureNotFoundException)
        {
            return $"failed: {exception.Message}";
        }
    }
}
