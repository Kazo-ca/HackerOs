using HackerOs.App.Abstractions;
using HackerOs.AppSdk.IO;

namespace HackerOs.AppSdk;

/// <summary>
/// The current process's environment, modeled on <see cref="System.Environment"/> (ADR 0050,
/// section 6): working directory, environment variables, command-line arguments, PID, user and
/// machine names.
/// </summary>
/// <remarks>
/// <para>
/// Every member targets the process of the app running the current code, found through the
/// ambient context of ADR 0043 (<see cref="HackExecutionContext"/>). No capability is needed, as on
/// Linux: a process can always read and change its own environment, never another's. A command
/// that runs <c>export</c> or <c>cd</c> therefore changes only itself, not the shell that started
/// it; changing the shell goes through the controlled access of ADR 0051.
/// </para>
/// <para>
/// Values are copied from the parent when the process is created: a later change on either side
/// never reaches the other.
/// </para>
/// </remarks>
public static class HackEnvironment
{
    /// <summary>
    /// Gets the canonical absolute working directory of the current process, against which
    /// <see cref="HackPath"/> and every <c>Hack*</c> file API resolve relative paths.
    /// </summary>
    /// <remarks>
    /// Read-only because changing it must check the filesystem, which is asynchronous here: use
    /// <see cref="SetCurrentDirectoryAsync"/> (ADR 0053).
    /// </remarks>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static string CurrentDirectory => HackExecutionContext.WorkingDirectory;

    /// <summary>Gets the line terminator of KazoOS text files, <c>\n</c> as on Linux.</summary>
    public static string NewLine => "\n";

    /// <summary>Gets the current process's PID.</summary>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static int ProcessId => checked((int)HackExecutionContext.Current.ProcessId.Value);

    /// <summary>Gets the login name of the user the current process runs as.</summary>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static string UserName => Gateway.UserName;

    /// <summary>Gets the machine name: the first line of <c>/etc/hostname</c>, read at session start.</summary>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static string MachineName => Gateway.MachineName;

    /// <summary>
    /// Gets the language of the current process's texts, <c>en</c> or <c>fr</c> (ADR 0104):
    /// <c>LC_ALL</c>, <c>LC_MESSAGES</c> then <c>LANG</c>, like a program's <c>setlocale</c>.
    /// </summary>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static System.Globalization.CultureInfo Culture => HackExecutionContext.Current.Culture;

    /// <summary>
    /// Gets the culture of the current process's dates and numbers, the full locale of
    /// <c>LC_TIME</c> (or <c>LC_ALL</c>, then <c>LANG</c>), e.g. <c>fr-CA</c> (ADR 0104).
    /// </summary>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static System.Globalization.CultureInfo FormatCulture => HackExecutionContext.Current.FormatCulture;

    /// <summary>
    /// Changes the current process's working directory, like <c>chdir</c>. A relative path is
    /// resolved against the current directory and <c>~</c> expands to the home directory.
    /// </summary>
    /// <param name="path">Absolute, relative or <c>~</c>-prefixed path of an existing directory.</param>
    /// <param name="cancellationToken">Cancels the existence check.</param>
    /// <exception cref="ArgumentException">The path is empty or malformed.</exception>
    /// <exception cref="HackDirectoryNotFoundException">The path does not exist.</exception>
    /// <exception cref="HackIOException">The path exists but is not a directory.</exception>
    /// <exception cref="HackUnauthorizedAccessException">The path cannot be looked up by this user.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task SetCurrentDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        VirtualPath target = HackPath.Resolve(path, nameof(path));
        await HackFileSystemCore.RequireDirectoryAsync(HackFileSystemCore.Gateway, target, cancellationToken).ConfigureAwait(false);
        Gateway.SetCurrentDirectory(target);
        HackExecutionContext.UnpinWorkingDirectory();
    }

    /// <summary>Gets one environment variable of the current process, or <see langword="null"/> when it is not set.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static string? GetEnvironmentVariable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Gateway.GetVariable(name);
    }

    /// <summary>Gets a copy of every environment variable of the current process.</summary>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static IReadOnlyDictionary<string, string> GetEnvironmentVariables() => Gateway.GetVariables();

    /// <summary>
    /// Sets one environment variable of the current process, or removes it when
    /// <paramref name="value"/> is <see langword="null"/>. An empty string is a valid value, as on Linux.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or contains <c>=</c> or NUL, or the value contains NUL.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static void SetEnvironmentVariable(string name, string? value) => Gateway.SetVariable(name, value);

    /// <summary>Gets the current process's arguments, the command name first (like <see cref="System.Environment.GetCommandLineArgs"/>).</summary>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static string[] GetCommandLineArgs() => [.. Gateway.GetCommandLineArgs()];

    /// <summary>
    /// Gets the path of a well-known folder of the effective user (ADR 0056, section 1), like
    /// <see cref="System.Environment.GetFolderPath(System.Environment.SpecialFolder)"/> and the
    /// XDG base directories on Linux. The folder may not exist yet: the path is computed, not checked.
    /// </summary>
    /// <param name="folder">The folder to locate.</param>
    /// <returns>A canonical absolute path, under <c>/root</c> for a process started through <c>sudo</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="folder"/> is not a defined value.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static string GetFolderPath(HackSpecialFolder folder) =>
        GetFolderPath(HackUser.Current.HomeDirectory, folder);

    /// <summary>Combines a home directory with the relative location of <paramref name="folder"/>.</summary>
    internal static string GetFolderPath(string home, HackSpecialFolder folder)
    {
        string relative = folder switch
        {
            HackSpecialFolder.UserProfile => string.Empty,
            HackSpecialFolder.Desktop => "Desktop",
            HackSpecialFolder.MyDocuments => "Documents",
            HackSpecialFolder.ApplicationData => ".config",
            HackSpecialFolder.LocalApplicationData => ".local/share",
            _ => throw new ArgumentOutOfRangeException(nameof(folder), folder, "Unknown special folder.")
        };

        return relative.Length == 0 ? home : $"{home.TrimEnd('/')}/{relative}";
    }

    private static Simulation.Abstractions.Gateways.IAppEnvironmentGateway Gateway => HackExecutionContext.Current.Environment;
}

/// <summary>
/// Well-known folders of a KazoOS user, modeled on <see cref="System.Environment.SpecialFolder"/>
/// and mapped to the Linux/XDG layout (ADR 0056, section 1).
/// </summary>
public enum HackSpecialFolder
{
    /// <summary>The home directory, <c>~</c>.</summary>
    UserProfile = 0,

    /// <summary><c>~/Desktop</c>.</summary>
    Desktop = 1,

    /// <summary><c>~/Documents</c>.</summary>
    MyDocuments = 2,

    /// <summary><c>~/.config</c> (<c>XDG_CONFIG_HOME</c>), where app settings live (ADR 0044).</summary>
    ApplicationData = 3,

    /// <summary><c>~/.local/share</c> (<c>XDG_DATA_HOME</c>).</summary>
    LocalApplicationData = 4
}
