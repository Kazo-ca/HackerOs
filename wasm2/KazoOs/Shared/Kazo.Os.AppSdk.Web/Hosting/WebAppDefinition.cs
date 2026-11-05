using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Web.Mvc;

namespace Kazo.Os.AppSdk.Web.Hosting;

/// <summary>
/// The manifest class of an ASP.Kazo site (ADR 0073 §1): kind <see cref="AppKind.Web"/>, its
/// <see cref="Urls"/> and the start-up code (<see cref="Configure"/>, ADR 0076 §2).
/// </summary>
/// <remarks>
/// <para>
/// Declare it <c>partial</c>: the route generator (ADR 0082) then supplies <see cref="AppDefinition.EntryPoint"/>,
/// a generated <c>&lt;Name&gt;WebHost</c> process class that starts the site with the controllers of the
/// assembly. A definition can still give its own entry point (a class deriving from <see cref="KazoWebHost"/>).
/// </para>
/// <code>
/// [AppManifest]
/// public sealed partial class IntranetApp : WebAppDefinition
/// {
///     public override string Id =&gt; "com.example.intranet";
///     public override string Name =&gt; "Intranet";
///     public override IReadOnlyList&lt;string&gt; Urls =&gt; ["http://intranet.hack/"];
///
///     public override void Configure(KazoWebApplicationBuilder builder)
///     {
///         builder.Services.AddSingleton&lt;NotesStore&gt;();
///         KazoWebApplication app = builder.Build();
///         app.UseCookieSession();
///         app.MapControllers();
///     }
/// }
/// </code>
/// <para>
/// A site runs in the shared web worker (ADR 0073 §1) and has no <c>filesystem.*</c> capability
/// (<c>KOSAPP0050</c>).
/// </para>
/// </remarks>
public abstract class WebAppDefinition : AppDefinition
{
    /// <inheritdoc />
    public sealed override AppKind Kind => AppKind.Web;

    /// <inheritdoc />
    public override string Category => "internet";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <summary>Gets the URLs the site serves (<c>web.urls</c>); the first is its main address.</summary>
    public abstract override IReadOnlyList<string> Urls { get; }

    /// <summary>
    /// Configures the site's services and pipeline, then calls <see cref="KazoWebApplicationBuilder.Build"/>.
    /// Runs in the site's process each time it starts (never at build time). The default maps the controllers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    public virtual void Configure(KazoWebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Build().MapControllers();
    }
}
