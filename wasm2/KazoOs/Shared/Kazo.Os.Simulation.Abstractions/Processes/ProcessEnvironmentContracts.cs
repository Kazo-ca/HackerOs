using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Simulation.Abstractions.Processes;

/// <summary>
/// One immutable view of a process's arguments, working directory and environment (ADR 0050,
/// section 5). A new snapshot is returned on every read; later changes never alter it.
/// </summary>
/// <param name="Arguments">The process's <c>argv</c>, command name included (never empty).</param>
/// <param name="WorkingDirectory">Canonical absolute working directory.</param>
/// <param name="Variables">Environment variables, ordinal name to value.</param>
public sealed record ProcessEnvironmentSnapshot(
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Variables);

/// <summary>
/// Per-process arguments, working directory and environment, indexed by PID (ADR 0050, section 5).
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from <see cref="ProcessRecord"/>, which stays an immutable lifecycle snapshot: these
/// values change while the process runs (<c>cd</c>, <c>export</c>). The process manager fills an
/// entry when it creates a process (copying its parent's directory and environment, like
/// <c>fork</c>) and drops it when the process ends.
/// </para>
/// <para>
/// The mutators act on one PID only. Gateways bind them to the calling app's own process, so an
/// app can change its own directory and environment but never another process's — including its
/// parent shell's, exactly like on Linux.
/// </para>
/// </remarks>
public interface IProcessEnvironmentTable
{
    /// <summary>Attempts to read the current state of one active process.</summary>
    bool TryGet(ProcessId pid, out ProcessEnvironmentSnapshot snapshot);

    /// <summary>Reads the current state of one active process.</summary>
    /// <exception cref="KeyNotFoundException">No active process has this PID.</exception>
    ProcessEnvironmentSnapshot Get(ProcessId pid);

    /// <summary>
    /// Changes one process's working directory. The table only stores the path: checking that it
    /// exists and is a directory is the caller's job (it needs the filesystem, which is async).
    /// </summary>
    /// <exception cref="KeyNotFoundException">No active process has this PID.</exception>
    void SetWorkingDirectory(ProcessId pid, VirtualPath directory);

    /// <summary>Sets one environment variable of one process, or removes it when <paramref name="value"/> is <see langword="null"/>.</summary>
    /// <exception cref="ArgumentException">The name is empty or contains <c>=</c> or NUL, or the value contains NUL.</exception>
    /// <exception cref="KeyNotFoundException">No active process has this PID.</exception>
    void SetVariable(ProcessId pid, string name, string? value);
}

/// <summary>Validation rules shared by every holder of environment variables.</summary>
public static class ProcessEnvironmentRules
{
    /// <summary>
    /// Throws when <paramref name="name"/> cannot be an environment variable name. Like the C
    /// library's <c>setenv</c>, any non-empty name without <c>=</c> (and, here, without NUL, which
    /// would corrupt <c>/proc/&lt;pid&gt;/environ</c>) is accepted.
    /// </summary>
    /// <exception cref="ArgumentException">The name is invalid.</exception>
    public static void ValidateName(string name, string parameterName = "name")
    {
        ArgumentNullException.ThrowIfNull(name, parameterName);
        if (name.Length == 0 || name.Contains('=') || name.Contains('\0'))
        {
            throw new ArgumentException(
                $"'{name}' is not a valid environment variable name: it must be non-empty and contain neither '=' nor NUL.",
                parameterName);
        }
    }

    /// <summary>Throws when <paramref name="value"/> contains NUL, the <c>environ</c> separator.</summary>
    /// <exception cref="ArgumentException">The value contains NUL.</exception>
    public static void ValidateValue(string value, string parameterName = "value")
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Contains('\0'))
        {
            throw new ArgumentException("Environment variable values cannot contain NUL.", parameterName);
        }
    }
}
