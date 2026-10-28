using HackerOs.Ecosystem;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// The whole OS comes from HackerOs.Ecosystem.Host; the apps are the packages referenced by the
// project, aggregated into this assembly by the build (HackerOsAppHost).
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.AddHackerOsBrowserHost(HostAppCatalog.Load(typeof(Program).Assembly));
// ADR 0182: apps installed from packages (hackpkg) join the catalog before the OS starts.
WebAssemblyHost host = builder.Build();
await host.LoadInstalledAppsAsync();
await host.RunAsync();
