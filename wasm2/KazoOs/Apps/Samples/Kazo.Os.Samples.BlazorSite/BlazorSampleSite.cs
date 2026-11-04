using System.Text.Json.Serialization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Web.Hosting;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Mvc;

namespace Kazo.Os.Samples.BlazorSite;

/// <summary>
/// Manifest and server side of "Blazor Sample Site" (ADR 0084): a web app whose pages are Blazor
/// components drawn by the Kazo OS browser (<see cref="BlazorRoot"/>), with an API route the pages
/// call through <c>Site.Http</c>. The build generates <c>manifest.json</c> and
/// <c>BlazorSampleSiteWebHost</c>, the entry point that serves the site.
/// </summary>
[AppManifest]
public sealed partial class BlazorSampleSite : WebAppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.samples.blazor-site";

    /// <inheritdoc />
    public override string Name => "Blazor Sample Site";

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description =>
        "Sample interactive Blazor site: pages by URL, links without reload, an API called from the pages, a crash page";

    /// <inheritdoc />
    public override IReadOnlyList<string> Urls => ["http://blazor-sample.hack/"];

    /// <inheritdoc />
    public override Type? BlazorRoot => typeof(App);

    /// <summary>Listening for the server side; simulated network for the pages' <c>Site.Http</c> calls.</summary>
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.NetworkListen, AppCapabilities.NetworkSimulatedRead, AppCapabilities.NetworkSimulatedWrite];

    /// <summary>The site's routes besides its Blazor pages: the host page comes last, after these.</summary>
    public override void Configure(KazoWebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Json.SerializerOptions.TypeInfoResolverChain.Insert(0, BlazorSampleJsonContext.Default);
        KazoWebApplication app = builder.Build();
        app.MapGet("/api/greeting/{name}", context =>
            KazoResults.Json(new Greeting($"Hello, {context.Request.RouteValues["name"]}!", DateTimeOffset.UtcNow)));
        app.MapControllers();
    }
}

/// <summary>What <c>/api/greeting/{name}</c> answers.</summary>
/// <param name="Message">The greeting.</param>
/// <param name="At">When the server answered.</param>
public sealed record Greeting(string Message, DateTimeOffset At);

/// <summary>JSON source generation for the site's API (trim-safe on every host).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Greeting))]
public sealed partial class BlazorSampleJsonContext : JsonSerializerContext;
