using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Platform.Blazor.Dialogs;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Ecosystem;

/// <summary>Creates a file-dialog queue after authentication binds it to a real session.</summary>
public sealed class FileDialogServiceFactory
{
    private readonly IFileSystemSelectedResourceHandleRegistry _handleRegistry;

    /// <summary>Initializes the factory over the process-wide selected-resource registry.</summary>
    public FileDialogServiceFactory(IFileSystemSelectedResourceHandleRegistry handleRegistry)
    {
        _handleRegistry = handleRegistry ?? throw new ArgumentNullException(nameof(handleRegistry));
    }

    /// <summary>Creates one disposable FIFO dialog coordinator for the supplied session.</summary>
    public IFileDialogService Create(SessionId sessionId) =>
        new FileDialogCoordinator(sessionId, _handleRegistry);
}