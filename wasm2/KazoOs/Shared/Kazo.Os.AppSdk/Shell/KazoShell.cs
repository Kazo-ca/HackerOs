using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.Shell;

/// <summary>
/// Starts interactive shells for a terminal emulator (ADR 0056, section 7). Requires the
/// dangerous capability <c>shell.host</c>: the commands a shell runs act with <b>their</b>
/// rights, with no window to show what they do.
/// </summary>
/// <example>
/// <code>
/// await using IHackShell shell = await KazoShell.StartAsync(new KazoShellOptions
/// {
///     WorkingDirectory = KazoEnvironment.GetFolderPath(...),
///     CredentialPrompt = myTerminalPrompt,
/// });
/// int status = await shell.ExecuteAsync("ls ~ | grep notes", null, stdout, stderr, ct);
/// </code>
/// </example>
public static class KazoShell
{
    /// <summary>
    /// Starts an <c>hsh</c> shell for the current user, as a child process of the current app.
    /// </summary>
    /// <param name="options">Starting directory and credential prompt; defaults when omitted.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running shell; dispose it to end its process.</returns>
    /// <exception cref="KazoUnauthorizedAccessException">The app was not granted <c>shell.host</c>.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task<IHackShell> StartAsync(KazoShellOptions? options = null, CancellationToken cancellationToken = default) =>
        StartAsync(KazoExecutionContext.Current, options, cancellationToken);

    /// <summary>
    /// Starts a shell for the app of <paramref name="context"/>, for a component that holds its
    /// app's context outside the ambient one (e.g. a Razor component's parameter).
    /// </summary>
    /// <param name="context">The app hosting the shell; its process becomes the shell's parent.</param>
    /// <param name="options">Starting directory and credential prompt; defaults when omitted.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running shell; dispose it to end its process.</returns>
    /// <exception cref="KazoUnauthorizedAccessException">The app was not granted <c>shell.host</c>.</exception>
    public static async Task<IHackShell> StartAsync(
        IAppExecutionContext context, KazoShellOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return await context.ShellHost.StartAsync(options ?? new KazoShellOptions(), cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw new KazoUnauthorizedAccessException(
                $"Starting a shell is denied: {exception.Message}", requiredCapability: exception.Capability);
        }
    }
}

/// <summary>
/// Gateway behind <see cref="KazoShell"/> (<see cref="IAppExecutionContext.ShellHost"/>). Checks
/// <c>shell.host</c>, then starts the shell's process as a child of the app's.
/// </summary>
public interface IAppShellHostGateway
{
    /// <summary>Starts an interactive shell for the app's user, as a child of the app's process.</summary>
    /// <param name="options">Starting directory and credential prompt.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running shell.</returns>
    /// <exception cref="AppGatewayAccessDeniedException">The app was not granted <c>shell.host</c>.</exception>
    Task<IHackShell> StartAsync(KazoShellOptions options, CancellationToken cancellationToken = default);
}
