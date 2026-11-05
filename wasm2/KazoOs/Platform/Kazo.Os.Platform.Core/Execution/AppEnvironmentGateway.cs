using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// <see cref="IAppEnvironmentGateway"/> bound to one process's entry in the
/// <see cref="IProcessEnvironmentTable"/> (ADR 0050, section 6). It can only ever read or change
/// that one PID, which is what makes it safe to hand out without any capability.
/// </summary>
internal sealed class AppEnvironmentGateway : IAppEnvironmentGateway
{
    private readonly IProcessEnvironmentTable _table;
    private readonly ProcessId _pid;
    private readonly Func<string> _machineName;
    private readonly string _fallbackDirectory;

    /// <summary>Binds the gateway to <paramref name="pid"/>.</summary>
    /// <param name="table">Process environment table holding the entry.</param>
    /// <param name="pid">The calling app's own process.</param>
    /// <param name="userName">Login name of the process owner.</param>
    /// <param name="machineName">Reads the current machine name.</param>
    /// <param name="fallbackDirectory">
    /// Directory reported once the process has ended and its entry is gone (the home directory),
    /// so late callbacks of a closed app still resolve paths instead of failing.
    /// </param>
    public AppEnvironmentGateway(
        IProcessEnvironmentTable table,
        ProcessId pid,
        string userName,
        Func<string> machineName,
        string fallbackDirectory)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _pid = pid;
        UserName = userName ?? throw new ArgumentNullException(nameof(userName));
        _machineName = machineName ?? throw new ArgumentNullException(nameof(machineName));
        _fallbackDirectory = fallbackDirectory ?? throw new ArgumentNullException(nameof(fallbackDirectory));
    }

    /// <inheritdoc />
    public string UserName { get; }

    /// <inheritdoc />
    public string MachineName => _machineName();

    /// <inheritdoc />
    public string CurrentDirectory => _table.TryGet(_pid, out ProcessEnvironmentSnapshot snapshot)
        ? snapshot.WorkingDirectory
        : _fallbackDirectory;

    /// <inheritdoc />
    public IReadOnlyList<string> GetCommandLineArgs() => _table.Get(_pid).Arguments;

    /// <inheritdoc />
    public void SetCurrentDirectory(VirtualPath directory) => _table.SetWorkingDirectory(_pid, directory);

    /// <inheritdoc />
    public string? GetVariable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _table.TryGet(_pid, out ProcessEnvironmentSnapshot snapshot)
            && snapshot.Variables.TryGetValue(name, out string? value)
                ? value
                : null;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetVariables() => _table.TryGet(_pid, out ProcessEnvironmentSnapshot snapshot)
        ? snapshot.Variables
        : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public void SetVariable(string name, string? value) => _table.SetVariable(_pid, name, value);
}

/// <summary>
/// Read-only <see cref="IAppEnvironmentGateway"/> used when the factory has no process environment
/// table (tests with a stub process manager): home directory, <c>HOME</c>, and the app ID as argv.
/// </summary>
internal sealed class DetachedAppEnvironmentGateway(
    string appId, string homeDirectory, string userName, Func<string> machineName) : IAppEnvironmentGateway
{
    /// <inheritdoc />
    public string CurrentDirectory => homeDirectory;

    /// <inheritdoc />
    public string UserName => userName;

    /// <inheritdoc />
    public string MachineName => machineName();

    /// <inheritdoc />
    public IReadOnlyList<string> GetCommandLineArgs() => [appId];

    /// <inheritdoc />
    public string? GetVariable(string name) => name == "HOME" ? homeDirectory : null;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetVariables() =>
        new Dictionary<string, string>(StringComparer.Ordinal) { ["HOME"] = homeDirectory };

    /// <inheritdoc />
    public void SetCurrentDirectory(VirtualPath directory) =>
        throw new NotSupportedException("This execution context has no live process environment.");

    /// <inheritdoc />
    public void SetVariable(string name, string? value) =>
        throw new NotSupportedException("This execution context has no live process environment.");
}
