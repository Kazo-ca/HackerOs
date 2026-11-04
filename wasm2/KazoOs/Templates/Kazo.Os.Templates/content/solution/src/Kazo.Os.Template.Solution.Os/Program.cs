using Kazo.Os.Ecosystem;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// The whole OS comes from Kazo.Os.Ecosystem.Host; the apps are the packages referenced by the
// project, aggregated into this assembly by the build (KazoOsAppHost).
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.AddKazoOsBrowserHost(HostAppCatalog.Load(typeof(Program).Assembly));
// ADR 0182: apps installed from packages (kazopkg) join the catalog before the OS starts.
WebAssemblyHost host = builder.Build();
await host.LoadInstalledAppsAsync();
await host.RunAsync();
