using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Platform.WorkerHost.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.RemoteRendering.Worker;

/// <summary>
/// Renders one window app in a worker (ADR 0129 trial, ADR 0133): the worker host creates it by the
/// type name the OS sends, gives it the process, and ends the process when it returns.
/// </summary>
/// <remarks>
/// Inputs from the page (<c>uin</c>): <c>{k:"e"}</c> a DOM event, <c>{k:"dlgr"}</c> the answer to a
/// dialog, <c>{k:"close"}</c> the window was closed. Outputs (<c>ui</c>): <c>{k:"b"}</c> a batch of DOM
/// operations, <c>{k:"dlg"}</c> a dialog request.
/// </remarks>
internal sealed class WorkerWindowRunner : IWorkerWindowRunner
{
    /// <summary>Kept by the trimmer for the worker host's <c>Activator.CreateInstance</c>.</summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(WorkerWindowRunner))]
    public WorkerWindowRunner()
    {
    }

    /// <inheritdoc />
    public async Task<int> RunAsync(WorkerWindowRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        RemoteDialogService dialogs = new(run);
        WorkerWindowServices services = new(dialogs);
        await using WorkerWindowRenderer renderer = new(services, NullLoggerFactory.Instance, run);
        run.SetReceiver(input => Receive(renderer, dialogs, input));
        using CancellationTokenRegistration stop = cancellationToken.Register(renderer.Close);

        await renderer.Dispatcher.InvokeAsync(() => renderer.MountAsync(run.Context, run.EntryPointType)).ConfigureAwait(true);
        Exception? failure = await renderer.Ended.ConfigureAwait(true);
        dialogs.CancelAll();
        if (failure is not null)
        {
            throw new InvalidOperationException(failure.Message, failure);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }

    private static void Receive(WorkerWindowRenderer renderer, RemoteDialogService dialogs, JsonElement input)
    {
        switch (input.GetProperty("k").GetString())
        {
            case "e":
                _ = renderer.DispatchDomEventAsync(input);
                break;
            case "dlgr":
                dialogs.Complete(input);
                break;
            case "close":
                renderer.Close();
                break;
        }
    }

    /// <summary>
    /// The window's services: what <c>WindowAppBase</c> injects. An app that injects anything else
    /// cannot run in a worker yet (ADR 0133, section 4): Blazor names the missing service.
    /// </summary>
    private sealed class WorkerWindowServices(RemoteDialogService dialogs) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IDialogService) || serviceType == typeof(IFileDialogService))
            {
                return dialogs;
            }

            if (serviceType == typeof(IWindowAppFrameworkLifecycle))
            {
                return NullWindowAppFrameworkLifecycle.Instance;
            }

            if (serviceType == typeof(IAdminElevationVerifier))
            {
                return NoElevationInWorker.Instance;
            }

            return serviceType == typeof(IServiceProvider) ? this : null;
        }
    }

    /// <summary>No one-shot approval from a worker in the trial: the app sees "Permission denied".</summary>
    private sealed class NoElevationInWorker : IAdminElevationVerifier
    {
        public static NoElevationInWorker Instance { get; } = new();

        public Task<AdminElevationVerificationResult> VerifyAsync(string loginName, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(AdminElevationVerificationResult.Failed("Approval is not available to a window rendered in a worker."));
    }
}
