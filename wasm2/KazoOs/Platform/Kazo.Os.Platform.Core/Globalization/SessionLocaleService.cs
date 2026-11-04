using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Simulation.Abstractions.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Core.Globalization;

/// <summary>
/// The language of the platform's own surfaces (ADR 0104, section 2): the desktop, the taskbar,
/// the launcher, the platform dialogs and the sign-in screens.
/// </summary>
/// <remarks>
/// <para>
/// Mimics a Linux display manager and desktop session: before anyone signs in, the greeter speaks
/// the system language of <c>/etc/locale.conf</c>; once a user signs in, the desktop takes the
/// <c>LANG</c>/<c>LC_*</c> of that user's login environment (system locale, <c>/etc/environment</c>,
/// then <c>~/.config/locale.conf</c>) and keeps it until the session ends. Changing the user's
/// language in Settings rewrites <c>~/.config/locale.conf</c> and takes effect at the next sign-in,
/// as on GNOME.
/// </para>
/// <para>
/// Apps never read this service: each one takes its language from its own process environment
/// (<c>IAppExecutionContext.Culture</c>), which starts as a copy of the same login environment.
/// </para>
/// </remarks>
public sealed class SessionLocaleService
{
    private readonly LoginEnvironmentProvider _loginEnvironment;
    private readonly IFileSystemService _fileSystem;
    private readonly ISimulationClock _clock;
    private readonly ILogger _logger;
    private IReadOnlyDictionary<string, string> _variables = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [KazoLocale.LangVariable] = KazoLocale.DefaultLocale
    };

    /// <summary>Creates the service.</summary>
    /// <param name="loginEnvironment">Builds the login environment of a signed-in user.</param>
    /// <param name="fileSystem">Filesystem engine <c>/etc/locale.conf</c> is read from, as root, before sign-in.</param>
    /// <param name="clock">Simulation clock for authorization timestamps.</param>
    /// <param name="logger">Optional logger; its lines reach <c>/var/log/syslog</c>.</param>
    public SessionLocaleService(
        LoginEnvironmentProvider loginEnvironment,
        IFileSystemService fileSystem,
        ISimulationClock clock,
        ILogger<SessionLocaleService>? logger = null)
    {
        _loginEnvironment = loginEnvironment ?? throw new ArgumentNullException(nameof(loginEnvironment));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Gets the language of the platform's texts: <c>en</c> or <c>fr</c>.</summary>
    public CultureInfo Culture { get; private set; } = KazoLocale.DefaultLanguage;

    /// <summary>Gets the culture of the platform's dates and numbers (the taskbar clock), e.g. <c>fr-CA</c>.</summary>
    public CultureInfo FormatCulture { get; private set; } = KazoLocale.DefaultFormatCulture;

    /// <summary>Gets the effective <c>LANG</c> of the platform, e.g. <c>fr_CA.UTF-8</c>.</summary>
    public string LocaleName => _variables.GetValueOrDefault(KazoLocale.LangVariable) ?? KazoLocale.DefaultLocale;

    /// <summary>Raised after <see cref="Culture"/> or <see cref="FormatCulture"/> changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Takes the system language (<c>/etc/locale.conf</c>) for the sign-in screens, like a display
    /// manager at boot or after a sign-out. Never throws on a missing or unreadable file: English is kept.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task UseSystemLocaleAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> variables = new(StringComparer.Ordinal)
        {
            [KazoLocale.LangVariable] = KazoLocale.DefaultLocale
        };

        IAppFileSystemGateway root = new AppFileSystemGateway(_fileSystem, _clock, RootOperation(), ["system"]);
        SettingsFileReadResult read = await SettingsTextFile
            .ReadAsync(root, LoginEnvironment.SystemLocaleFilePath, cancellationToken)
            .ConfigureAwait(false);
        if (read.Error is { } error)
        {
            _logger.LogWarning("locale: cannot read {Path}: {Code}, English used", LoginEnvironment.SystemLocaleFilePath.Value, error.Code);
        }

        foreach (KeyValuePair<string, string> variable in KazoLocale.ParseLocaleConf(read.Content))
        {
            variables[variable.Key] = variable.Value;
        }

        Apply(variables);
    }

    /// <summary>
    /// Takes the language of <paramref name="principal"/>'s login environment, read once for the
    /// session (ADR 0104 section 2: a change made during the session waits for the next sign-in).
    /// </summary>
    /// <param name="principal">The user who just signed in.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task UseSessionLocaleAsync(AuthenticatedPrincipal principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        IReadOnlyDictionary<string, string> variables = await _loginEnvironment
            .GetAsync(principal, cancellationToken)
            .ConfigureAwait(false);
        Apply(variables);
    }

    /// <summary>Sets the platform language from explicit locale variables (tests, and the two methods above).</summary>
    /// <param name="variables">The environment holding <c>LANG</c>/<c>LC_*</c>.</param>
    public void Apply(IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        CultureInfo culture = KazoLocale.ResolveLanguage(name => variables.GetValueOrDefault(name));
        CultureInfo format = KazoLocale.ResolveFormatCulture(name => variables.GetValueOrDefault(name));
        bool changed = !culture.Equals(Culture) || !format.Equals(FormatCulture);
        _variables = new Dictionary<string, string>(variables, StringComparer.Ordinal);
        Culture = culture;
        FormatCulture = format;
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Reads as the kernel, like a display manager running as root.</summary>
    private static AppOperationContext RootOperation() => new()
    {
        AppId = "org.hackeros.kernel",
        UserId = "system",
        UserAuthority = AppAuthority.System,
        GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
        IsSystemOperation = true
    };
}
