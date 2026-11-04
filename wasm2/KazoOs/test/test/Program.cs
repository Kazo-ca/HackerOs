using Kazo.Os.Ecosystem;
using Kazo.Os.Platform.Blazor.Hosting;
using Kazo.Os.Platform.Blazor.LazyLoading;
using test;
using test.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TestHarnessOptions.FromArgs(args));

builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = false;
    options.ValidateOnBuild = false;
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddSingleton<IEcosystemHostEnvironment, WebAssemblyEcosystemHostEnvironment>();
builder.Services.AddSingleton<IBuildKnownAssemblyTransport, WebAssemblyLazyAssemblyTransport>();
builder.Services.AddSingleton(provider => new BuildKnownAssemblyLoaderRegistry(
    BuildKnownLazyAssemblies.Names,
    provider.GetRequiredService<IBuildKnownAssemblyTransport>()));
builder.Services.AddSingleton(provider => new BuildKnownLazyAppDescriptorRegistry(
    BuildKnownLazyApps.Catalog,
    provider.GetRequiredService<BuildKnownAssemblyLoaderRegistry>()));
// ADR 0066: optional measured CPU/memory, decided once from HackerOs:ResourceAccounting in
// appsettings.json and what the build compiled in. Must precede AddKazoOsEcosystem, and runs before
// any app code so the context-switch hook can be installed.
builder.Services.AddKazoOsResourceAccounting(builder.Configuration, BuildKnownLazyApps.Images);
// ADR 0125: process limits (ulimit, limits.conf), on unless HackerOs:ResourceLimits:Enabled is false.
builder.Services.AddKazoOsResourceLimits(builder.Configuration);
builder.Services.AddKazoOsHardware(builder.Configuration);
builder.Services.AddKazoOsEcosystem(
    BuildKnownLazyApps.Catalog,
    provider => provider.GetRequiredService<BuildKnownLazyAppDescriptorRegistry>().Descriptors,
    provider => provider.GetRequiredService<BuildKnownLazyAppDescriptorRegistry>());

var app = builder.Build();

// ADR 0131, section 2: COOP/COEP on every response, so the OS's page and its Web Workers are
// cross-origin isolated (HackerOs:CrossOriginIsolation: auto, on or off). The PWA's service worker
// adds the same headers once it is installed; this covers the first load and a host without it.
IReadOnlyList<KeyValuePair<string, string>> isolationHeaders = Kazo.Os.Platform.Core.Isolation.CrossOriginIsolationPolicy.ServerHeaders(
    Kazo.Os.Platform.Core.Isolation.CrossOriginIsolationPolicy.Parse(app.Configuration[Kazo.Os.Platform.Core.Isolation.CrossOriginIsolationPolicy.SettingKey]));
if (isolationHeaders.Count > 0)
{
    app.Use((context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            foreach ((string name, string value) in isolationHeaders)
            {
                context.Response.Headers[name] = value;
            }

            return Task.CompletedTask;
        });
        return next(context);
    });
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<test.Components.App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(
        typeof(Kazo.Os.Ecosystem.App).Assembly,
        typeof(Kazo.Os.Platform.Blazor.Shell.DesktopShell).Assembly);

app.Run();
