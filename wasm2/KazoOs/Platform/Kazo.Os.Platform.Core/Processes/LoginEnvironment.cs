using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Simulation.Abstractions.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>
/// Pure rules of the login environment (ADR 0050, section 5; ADR 0104, section 2): the variables
/// every root process of a session starts with, then the system locale (<c>/etc/locale.conf</c>),
/// the <c>NAME=value</c> lines of <c>/etc/environment</c> read like Linux's <c>pam_env</c>, and
/// last the user's own locale (<c>~/.config/locale.conf</c>), as a systemd user session does.
/// </summary>
public static class LoginEnvironment
{
    /// <summary>System-wide environment file, <c>0644</c>, owned by root.</summary>
    public static readonly VirtualPath EnvironmentFilePath = VirtualPath.Parse("/etc/environment");

    /// <summary>Kernel host name file, <c>0644</c>, owned by root; its first line is the machine name.</summary>
    public static readonly VirtualPath HostNameFilePath = VirtualPath.Parse("/etc/hostname");

    /// <summary>Host name used when <c>/etc/hostname</c> is missing, empty or unreadable.</summary>
    public const string DefaultHostName = "hackeros";

    /// <summary>Informative search path: commands are resolved through the app catalog, not this list.</summary>
    public const string DefaultPath = "/bin:/usr/bin";

    /// <summary>Login shell recorded in <c>SHELL</c>.</summary>
    public const string DefaultShell = "/bin/hsh";

    /// <summary>Default locale recorded in <c>LANG</c>.</summary>
    public const string DefaultLanguage = HackLocale.DefaultLocale;

    /// <summary>System locale file (systemd's <c>locale.conf</c>, written by <c>localectl</c>), <c>0644</c>, owned by root.</summary>
    public static readonly VirtualPath SystemLocaleFilePath = VirtualPath.Parse("/etc/locale.conf");

    /// <summary>Location of the user's locale file under their home directory (<c>~/.config/locale.conf</c>).</summary>
    public const string UserLocaleFileRelativePath = ".config/locale.conf";

    /// <summary>Content written to a freshly seeded <c>/etc/locale.conf</c>.</summary>
    public const string DefaultSystemLocaleFileContent =
        "# /etc/locale.conf: system locale (ADR 0104), changed with 'localectl set-locale'.\n" +
        "LANG=" + HackLocale.DefaultLocale + "\n";

    /// <summary>Content written to a freshly seeded <c>/etc/environment</c>.</summary>
    public const string DefaultEnvironmentFileContent =
        "# /etc/environment: system-wide environment of every login session (ADR 0050).\n" +
        "# One NAME=value per line; HOME, USER and LOGNAME cannot be overridden here.\n";

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.Ordinal) { "HOME", "USER", "LOGNAME" };

    /// <summary>Returns the path of a user's <c>~/.config/locale.conf</c>.</summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    public static VirtualPath UserLocaleFilePath(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return VirtualPath.Parse($"{homeDirectory.TrimEnd('/')}/{UserLocaleFileRelativePath}");
    }

    /// <summary>
    /// Builds the login environment of one user: the fixed variables of ADR 0050, then
    /// <paramref name="systemLocaleFile"/>, the lines of <paramref name="environmentFile"/>, and
    /// <paramref name="userLocaleFile"/> on top (ADR 0104, section 2).
    /// </summary>
    /// <param name="loginName">Login name, for <c>USER</c> and <c>LOGNAME</c>.</param>
    /// <param name="homeDirectory">Home directory, for <c>HOME</c> and the initial <c>PWD</c>.</param>
    /// <param name="environmentFile">Content of <c>/etc/environment</c>, or <see langword="null"/> when absent.</param>
    /// <param name="warnings">Receives one message per ignored line, for the system log.</param>
    /// <param name="systemLocaleFile">Content of <c>/etc/locale.conf</c>, or <see langword="null"/> when absent.</param>
    /// <param name="userLocaleFile">Content of <c>~/.config/locale.conf</c>, or <see langword="null"/> when absent.</param>
    public static IReadOnlyDictionary<string, string> Build(
        string loginName,
        string homeDirectory,
        string? environmentFile,
        ICollection<string>? warnings = null,
        string? systemLocaleFile = null,
        string? userLocaleFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);

        Dictionary<string, string> variables = new(StringComparer.Ordinal)
        {
            ["HOME"] = homeDirectory,
            ["USER"] = loginName,
            ["LOGNAME"] = loginName,
            ["SHELL"] = DefaultShell,
            ["PATH"] = DefaultPath,
            ["PWD"] = homeDirectory,
            ["LANG"] = DefaultLanguage
        };

        ApplyLocaleFile(variables, systemLocaleFile, SystemLocaleFilePath.Value, warnings);
        ApplyEnvironmentFile(variables, environmentFile, warnings);
        ApplyLocaleFile(variables, userLocaleFile, $"{homeDirectory.TrimEnd('/')}/{UserLocaleFileRelativePath}", warnings);
        return variables;
    }

    /// <summary>Applies a systemd <c>locale.conf</c>: only <c>LANG</c>, <c>LANGUAGE</c> and <c>LC_*</c> lines count.</summary>
    private static void ApplyLocaleFile(
        Dictionary<string, string> variables, string? content, string path, ICollection<string>? warnings)
    {
        List<(int Line, string Message)> problems = [];
        foreach (KeyValuePair<string, string> variable in HackLocale.ParseLocaleConf(content, problems))
        {
            variables[variable.Key] = variable.Value;
        }

        foreach ((int line, string message) in problems)
        {
            warnings?.Add($"locale: {path}:{line}: {message}");
        }
    }

    /// <summary>Applies the <c>NAME=value</c> lines of <c>/etc/environment</c>, like <c>pam_env</c>.</summary>
    private static void ApplyEnvironmentFile(
        Dictionary<string, string> variables, string? environmentFile, ICollection<string>? warnings)
    {
        if (environmentFile is null)
        {
            return;
        }

        string[] lines = environmentFile.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int lineNumber = index + 1;
            if (!TryParseLine(line, out string name, out string value))
            {
                warnings?.Add($"environment: {EnvironmentFilePath.Value}:{lineNumber}: ignored invalid line (expected NAME=value)");
                continue;
            }

            if (ProtectedNames.Contains(name))
            {
                warnings?.Add($"environment: {EnvironmentFilePath.Value}:{lineNumber}: {name} cannot be overridden, line ignored");
                continue;
            }

            variables[name] = value;
        }
    }

    /// <summary>Returns the machine name held by <c>/etc/hostname</c>: its first non-blank line, trimmed.</summary>
    /// <param name="hostNameFile">File content, or <see langword="null"/> when absent.</param>
    public static string ParseHostName(string? hostNameFile)
    {
        if (hostNameFile is null)
        {
            return DefaultHostName;
        }

        foreach (string line in hostNameFile.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0 && trimmed[0] != '#')
            {
                return trimmed;
            }
        }

        return DefaultHostName;
    }

    /// <summary>
    /// Parses <c>NAME=value</c>, accepting the value wrapped in one pair of single or double
    /// quotes. The name follows the shell rule <c>[A-Za-z_][A-Za-z0-9_]*</c>.
    /// </summary>
    private static bool TryParseLine(string line, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;
        int separator = line.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        string candidate = line[..separator].Trim();
        if (!IsShellName(candidate))
        {
            return false;
        }

        string raw = line[(separator + 1)..].Trim();
        if (raw.Length >= 2 && (raw[0] == '"' || raw[0] == '\'') && raw[^1] == raw[0])
        {
            raw = raw[1..^1];
        }

        if (raw.Contains('\0'))
        {
            return false;
        }

        name = candidate;
        value = raw;
        return true;
    }

    private static bool IsShellName(string name) =>
        name.Length > 0
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

/// <summary>
/// Supplies the login environment and the machine name to the platform (ADR 0050, sections 5 and 6).
/// </summary>
/// <remarks>
/// <para>
/// The environment is built once per session, at its first root launch, from
/// <c>/etc/environment</c>, like <c>pam_env</c> at login: editing the file affects the next
/// session, not processes already running. Invalid lines are logged once, to the system log.
/// </para>
/// <para>
/// The machine name mimics the kernel host name: it is read from <c>/etc/hostname</c> at the same
/// moment and kept until the next session, as Linux keeps it until the next boot.
/// </para>
/// </remarks>
public sealed class LoginEnvironmentProvider
{
    private readonly IFileSystemService _fileSystem;
    private readonly ISimulationClock _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SessionId? _sessionId;
    private IReadOnlyDictionary<string, string>? _loginVariables;
    private string _hostName = LoginEnvironment.DefaultHostName;

    /// <summary>Creates the provider.</summary>
    /// <param name="fileSystem">Filesystem engine the two files are read from, as root.</param>
    /// <param name="clock">Simulation clock for authorization timestamps.</param>
    /// <param name="logger">Optional logger; its lines reach <c>/var/log/syslog</c> (ADR 0045).</param>
    public LoginEnvironmentProvider(IFileSystemService fileSystem, ISimulationClock clock, ILogger<LoginEnvironmentProvider>? logger = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Gets the machine name read at the last session start (<c>hackeros</c> before any).</summary>
    public string HostName => Volatile.Read(ref _hostName);

    /// <summary>
    /// Returns the login environment of <paramref name="principal"/>'s session, reading
    /// <c>/etc/environment</c> and <c>/etc/hostname</c> the first time the session asks.
    /// </summary>
    /// <param name="principal">Authenticated principal whose session starts a root process.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async ValueTask<IReadOnlyDictionary<string, string>> GetAsync(
        AuthenticatedPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionId == principal.SessionId && _loginVariables is not null)
            {
                return _loginVariables;
            }

            IAppFileSystemGateway root = new AppFileSystemGateway(_fileSystem, _clock, RootOperation(), ["system"]);
            string? environmentFile = await ReadAsync(root, LoginEnvironment.EnvironmentFilePath, cancellationToken).ConfigureAwait(false);
            string? hostNameFile = await ReadAsync(root, LoginEnvironment.HostNameFilePath, cancellationToken).ConfigureAwait(false);
            // ADR 0104: the system locale, then the user's own, around /etc/environment.
            string? systemLocaleFile = await ReadAsync(root, LoginEnvironment.SystemLocaleFilePath, cancellationToken).ConfigureAwait(false);
            string? userLocaleFile = await ReadAsync(
                root, LoginEnvironment.UserLocaleFilePath(principal.HomePath), cancellationToken).ConfigureAwait(false);

            List<string> warnings = [];
            IReadOnlyDictionary<string, string> variables = LoginEnvironment.Build(
                principal.LoginName.Value, principal.HomePath, environmentFile, warnings, systemLocaleFile, userLocaleFile);
            foreach (string warning in warnings)
            {
                _logger.LogWarning("{EnvironmentWarning}", warning);
            }

            Volatile.Write(ref _hostName, LoginEnvironment.ParseHostName(hostNameFile));
            _sessionId = principal.SessionId;
            _loginVariables = variables;
            return variables;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<string?> ReadAsync(IAppFileSystemGateway gateway, VirtualPath path, CancellationToken cancellationToken)
    {
        SettingsFileReadResult read = await SettingsTextFile.ReadAsync(gateway, path, cancellationToken).ConfigureAwait(false);
        if (read.Error is { } error)
        {
            _logger.LogWarning("environment: cannot read {Path}: {Code}, defaults used", path.Value, error.Code);
        }

        return read.Content;
    }

    /// <summary>Reads as the kernel, like <c>pam_env</c> running as root during login.</summary>
    private static AppOperationContext RootOperation() => new()
    {
        AppId = "org.hackeros.kernel",
        UserId = "system",
        UserAuthority = AppAuthority.System,
        GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
        IsSystemOperation = true
    };
}
