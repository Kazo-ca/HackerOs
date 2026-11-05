using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Simulation.Abstractions.Gateways;

/// <summary>
/// One app instance's view of its own process: arguments, working directory, environment, user
/// and machine names (ADR 0050, section 6). Every member targets the calling process only, so no
/// capability is required, like <c>getenv</c>/<c>chdir</c> on Linux.
/// </summary>
/// <remarks>
/// App code reaches this through the static <c>KazoEnvironment</c> facade of the SDK, which adds the
/// filesystem checks (an existing directory for a change of working directory).
/// </remarks>
public interface IAppEnvironmentGateway
{
    /// <summary>Gets the process's <c>argv</c>, command name first.</summary>
    IReadOnlyList<string> GetCommandLineArgs();

    /// <summary>Gets the process's canonical absolute working directory.</summary>
    string CurrentDirectory { get; }

    /// <summary>
    /// Records a new working directory for the process. The gateway does not check the path
    /// exists: <c>KazoEnvironment.SetCurrentDirectoryAsync</c> does, before calling this.
    /// </summary>
    /// <exception cref="NotSupportedException">This context has no live process environment.</exception>
    void SetCurrentDirectory(VirtualPath directory);

    /// <summary>Gets one variable, or <see langword="null"/> when it is not set.</summary>
    string? GetVariable(string name);

    /// <summary>Gets a copy of every variable.</summary>
    IReadOnlyDictionary<string, string> GetVariables();

    /// <summary>Sets one variable, or removes it when <paramref name="value"/> is <see langword="null"/>.</summary>
    /// <exception cref="ArgumentException">The name is empty or contains <c>=</c> or NUL, or the value contains NUL.</exception>
    /// <exception cref="NotSupportedException">This context has no live process environment.</exception>
    void SetVariable(string name, string? value);

    /// <summary>Gets the login name of the user the process runs as.</summary>
    string UserName { get; }

    /// <summary>Gets the machine name (first line of <c>/etc/hostname</c>, read at session start).</summary>
    string MachineName { get; }
}
