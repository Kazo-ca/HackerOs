using HackerOs.Ecosystem;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// The standalone HackerOS PWA (ADR 0098 §2): the whole OS comes from HackerOs.Ecosystem.Host; this
// project only adds its page (wwwroot/index.html, service worker) and its apps (ProjectReference
// items aggregated into this assembly by the build, read back by BuildKnownLazyApps).
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.AddHackerOsBrowserHost(BuildKnownLazyApps.Host);

// AddHackerOsEcosystem's ILoggerProvider (HackerOsDiagnosticLoggerProvider) depends only on
// singletons since ADR 0045, so this host keeps it: ILogger<T> entries land in /var/log/syslog.

// ADR 0182: apps installed from packages (hackpkg) join the catalog before the OS starts.
WebAssemblyHost host = builder.Build();
await host.LoadInstalledAppsAsync();
await host.RunAsync();
