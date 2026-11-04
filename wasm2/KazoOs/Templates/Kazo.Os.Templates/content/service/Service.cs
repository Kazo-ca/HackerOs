using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.Template.Service;

/// <summary>The service itself: runs until the session ends, on the simulated clock.</summary>
public sealed class Service : ServiceAppBase
{
    /// <summary>
    /// Creates the service for the manifest the OS loaded from <c>manifest.json</c>. The OS starts
    /// an app through this constructor (one <see cref="AppManifest"/> argument), as <c>exec</c> hands a
    /// program its arguments.
    /// </summary>
    /// <param name="manifest">This app's manifest.</param>
    public Service(AppManifest manifest)
        : base(manifest)
    {
    }

    /// <summary>Creates the service with the manifest built from <see cref="TemplateServiceApp"/>, for unit tests.</summary>
    public Service()
        : this(AppDefinitionMapper.ToManifest(new TemplateServiceApp()))
    {
    }

    /// <inheritdoc />
    protected override async Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            while (!sessionCancellationToken.IsCancellationRequested)
            {
                await context.Clock.DelayAsync(TimeSpan.FromMinutes(1), sessionCancellationToken);
                context.Logging.Log(DiagnosticSeverity.Information, $"Still running at {context.Clock.UtcNow:o}.");
            }
        }
        catch (OperationCanceledException)
        {
            // Logout or shutdown: nothing to clean up.
        }
    }
}
