using HackerOs.Simulation.Abstractions.Recovery;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Ecosystem;

/// <summary>Projects boot, authentication, recovery, desktop, and update state for the host renderer.</summary>
public sealed class EcosystemHostState
{
    /// <summary>Gets the currently rendered host surface.</summary>
    public EcosystemHostView View { get; private set; } = EcosystemHostView.Initializing;

    /// <summary>Gets users eligible for the login surface.</summary>
    public IReadOnlyList<LocalUser> Users { get; private set; } = [];

    /// <summary>Gets the active storage recovery presentation.</summary>
    public StorageRecoveryPresentation? Recovery { get; private set; }

    /// <summary>Gets the fatal correlation ID shown to the user.</summary>
    public Guid? FatalCorrelationId { get; private set; }

    /// <summary>Gets whether a newer release waits for a restart: a PWA update, or a build the origin replaced under the page (ADR 0162).</summary>
    public bool UpdateAvailable { get; private set; }

    /// <summary>Returns the host to its non-ready initialization surface.</summary>
    public void BeginInitialization()
    {
        View = EcosystemHostView.Initializing;
        Recovery = null;
        FatalCorrelationId = null;
    }

    /// <summary>Selects first-run setup or login after persistent users are loaded.</summary>
    public void CompleteIdentityLoad(IReadOnlyList<LocalUser> users)
    {
        ArgumentNullException.ThrowIfNull(users);
        Users = [.. users.Where(user => user.Enabled).OrderBy(user => user.LoginName.Value, StringComparer.Ordinal)];
        View = Users.Count == 0 ? EcosystemHostView.FirstRun : EcosystemHostView.Login;
    }

    /// <summary>Shows the login progress transition screen during session initialization.</summary>
    public void BeginLoginProgress() => View = EcosystemHostView.LoginProgress;

    /// <summary>Shows the desktop only after session activation succeeds.</summary>
    public void CompleteLogin() => View = EcosystemHostView.Desktop;

    /// <summary>Shows a typed recovery condition while ordinary readiness remains blocked.</summary>
    public void ShowRecovery(StorageRecoveryPresentation recovery)
    {
        Recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        View = EcosystemHostView.Recovery;
    }

    /// <summary>Shows a user-safe fatal state linked to structured diagnostics.</summary>
    public void ShowFatal(Guid correlationId)
    {
        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException("A fatal error requires a non-empty correlation ID.", nameof(correlationId));
        }

        FatalCorrelationId = correlationId;
        View = EcosystemHostView.Fatal;
    }

    /// <summary>Gets the newer version the origin serves (ADR 0174), or <see langword="null"/> when only the build changed or nothing waits.</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>Updates the non-blocking update indicator.</summary>
    public void SetUpdateAvailable(bool available) => UpdateAvailable = available;

    /// <summary>Shows the update indicator with the version the origin serves now (ADR 0174).</summary>
    /// <param name="version">The served version, for example <c>2.2.0</c>.</param>
    public void SetAvailableVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        AvailableVersion = version;
        UpdateAvailable = true;
    }
}

/// <summary>Identifies the single boot-critical surface rendered by the ecosystem host.</summary>
public enum EcosystemHostView
{
    /// <summary>Persistent platform state is being validated.</summary>
    Initializing,
    /// <summary>A clean profile requires its first Administrator.</summary>
    FirstRun,
    /// <summary>Existing local users may authenticate.</summary>
    Login,
    /// <summary>Session startup progress is displayed.</summary>
    LoginProgress,
    /// <summary>An authenticated session may use the desktop shell.</summary>
    Desktop,
    /// <summary>A typed storage failure blocks ordinary startup.</summary>
    Recovery,
    /// <summary>An unexpected unrecoverable host failure occurred.</summary>
    Fatal
}