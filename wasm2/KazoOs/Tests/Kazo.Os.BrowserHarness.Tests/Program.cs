using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.Icons;
using Kazo.Os.BrowserHarness.Tests;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MudBlazor.Services;

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddMudServices();
// KazoIcon (used throughout Taskbar.Blazor, Terminal, CodeEditor, KazoPaint) injects
// IIconCatalog. Without it, any scenario that renders one crashes the WebAssembly renderer
// on first render, which leaves the page stuck on index.html's "Loading..." placeholder
// forever instead of surfacing an error - see Kazo.Os.Ecosystem's own AddSingleton<IIconCatalog>.
builder.Services.AddSingleton<IIconCatalog, IconCatalog>();
// ADR 0104: the platform dialogs inject IStringLocalizer<Strings>; this harness has no Kazo OS
// session, so they speak English, as Kazo OS does before anyone changes the language.
builder.Services.AddSingleton(typeof(IStringLocalizer<>), typeof(EnglishStringLocalizer<>));

// ADR 0127: the "unresponsive" scenario's window, scripted process and real monitor.
builder.Services.AddSingleton<UnresponsiveScenarioState>();
builder.Services.AddSingleton(provider => provider.GetRequiredService<UnresponsiveScenarioState>().Monitor);

await builder.Build().RunAsync();