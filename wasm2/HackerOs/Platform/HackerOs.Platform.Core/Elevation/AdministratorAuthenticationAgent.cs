using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.Simulation.Abstractions.Diagnostics;

namespace HackerOs.Platform.Core.Elevation;

/// <summary>
/// Default <see cref="IAdministratorAuthenticationAgent"/> (ADR 0055, section 5).
/// </summary>
/// <remarks>
/// <para>
/// The flow mirrors <c>polkit</c> with <c>auth_admin</c>: the gateway has already checked the app's
/// capability, then asks here. A process running as root through <c>sudo</c> passes at once (logged
/// with <c>via=sudo</c>). Otherwise the prompt shows the app and the operation, and each submitted
/// login and password goes through <see cref="IAdminElevationVerifier"/>, the verifier of
/// <c>sudo</c>. Even an administrator types their password (ADR 0049, D24). After three failures
/// further attempts are refused without verification.
/// </para>
/// <para>
/// Every attempt and the final outcome land in <c>/var/log/auth.log</c> as <c>admin.authenticate</c>
/// entries (app, operation, requesting user, administrator, outcome); the password never does.
/// </para>
/// </remarks>
public sealed class AdministratorAuthenticationAgent : IAdministratorAuthenticationAgent
{
    /// <summary>Audit action of every entry this agent writes.</summary>
    public const string AuditAction = "admin.authenticate";

    private readonly IAdminElevationVerifier _verifier;
    private readonly IAuditLog _auditLog;
    private readonly IAdministratorAuthenticationPrompt? _prompt;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates the agent.</summary>
    /// <param name="verifier">Administrator credential verifier shared with <c>sudo</c> (ADR 0042).</param>
    /// <param name="auditLog">Audit log writing <c>/var/log/auth.log</c> (ADR 0045).</param>
    /// <param name="prompt">Host prompt (the secure-desktop overlay), or <see langword="null"/> when the host has none.</param>
    /// <param name="clock">Clock for audit timestamps; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public AdministratorAuthenticationAgent(
        IAdminElevationVerifier verifier,
        IAuditLog auditLog,
        IAdministratorAuthenticationPrompt? prompt = null,
        Func<DateTimeOffset>? clock = null)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _prompt = prompt;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Gets the passkey verifier of ADR 0168 section 4 (an administrator's login in, the verification
    /// out); <see langword="null"/> offers the password only.
    /// </summary>
    public Func<string, CancellationToken, Task<AdminElevationVerificationResult>>? PasskeyVerifier { get; init; }

    /// <inheritdoc />
    public async Task<AdministratorAuthenticationResult> AuthenticateAsync(
        AdministratorAuthenticationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Action);

        if (request.IsRootElevated)
        {
            // Already root through sudo: the administrator authenticated when sudo started it.
            Record(request, "root", "success", AuditOutcome.Success, via: "sudo");
            return new AdministratorAuthenticationResult(true, "root", null);
        }

        if (_prompt is null)
        {
            Record(request, "-", "failure:unavailable", AuditOutcome.Denied);
            return AdministratorAuthenticationResult.Failed("unavailable");
        }

        int failures = 0;
        string? authenticatedAs = null;
        SemaphoreSlim attemptGate = new(1, 1);

        Task<AdministratorAuthenticationAttempt> AttemptAsync(string login, string password, CancellationToken ct) =>
            AttemptCoreAsync(login, token => _verifier.VerifyAsync(login ?? string.Empty, password ?? string.Empty, token), ct);

        async Task<AdministratorAuthenticationAttempt> PasskeyAttemptAsync(string login, CancellationToken ct)
        {
            try
            {
                return await AttemptCoreAsync(login, token => PasskeyVerifier!(login ?? string.Empty, token), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The browser's prompt was closed: not a failed attempt.
                return new AdministratorAuthenticationAttempt(false, AdministratorAuthenticationPromptRequest.MaxAttempts - failures, "passkey-cancelled");
            }
            catch (NotSupportedException)
            {
                return new AdministratorAuthenticationAttempt(false, AdministratorAuthenticationPromptRequest.MaxAttempts - failures, "passkey-unsupported");
            }
        }

        async Task<AdministratorAuthenticationAttempt> AttemptCoreAsync(
            string login, Func<CancellationToken, Task<AdminElevationVerificationResult>> verify, CancellationToken ct)
        {
            await attemptGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (authenticatedAs is not null)
                {
                    return new AdministratorAuthenticationAttempt(true, AdministratorAuthenticationPromptRequest.MaxAttempts - failures);
                }

                if (failures >= AdministratorAuthenticationPromptRequest.MaxAttempts)
                {
                    return new AdministratorAuthenticationAttempt(false, 0, "too-many-attempts");
                }

                AdminElevationVerificationResult verification = await verify(ct).ConfigureAwait(false);
                if (!verification.Success)
                {
                    failures++;
                    Record(request, login ?? string.Empty, $"failure:{verification.FailureReason ?? "unknown"}", AuditOutcome.Denied);
                    return new AdministratorAuthenticationAttempt(
                        false, AdministratorAuthenticationPromptRequest.MaxAttempts - failures, verification.FailureReason);
                }

                authenticatedAs = verification.LoginName ?? login ?? string.Empty;
                Record(request, authenticatedAs, "success", AuditOutcome.Success);
                return new AdministratorAuthenticationAttempt(true, AdministratorAuthenticationPromptRequest.MaxAttempts - failures);
            }
            finally
            {
                attemptGate.Release();
            }
        }

        AdministratorAuthenticationPromptRequest promptRequest = new(
            request.App,
            request.Operation,
            request.RequestedBy,
            request.RequestedBy is { } principal && AppAuthorityPolicy.Satisfies(principal.Authority, AppAuthority.Administrator)
                ? principal.LoginName.Value
                : null,
            AttemptAsync)
        {
            AuthenticateWithPasskeyAsync = PasskeyVerifier is null ? null : PasskeyAttemptAsync
        };

        AdministratorAuthenticationOutcome outcome;
        try
        {
            outcome = await _prompt.RequestAsync(promptRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = AdministratorAuthenticationOutcome.Cancelled;
        }

        // The prompt surface cannot forge an approval: only a verified attempt counts.
        if (authenticatedAs is not null)
        {
            return new AdministratorAuthenticationResult(true, authenticatedAs, null);
        }

        string reason = outcome switch
        {
            AdministratorAuthenticationOutcome.Unavailable => "unavailable",
            _ when failures >= AdministratorAuthenticationPromptRequest.MaxAttempts => "too-many-attempts",
            _ => "cancelled"
        };
        Record(request, "-", $"failure:{reason}", AuditOutcome.Denied);
        return AdministratorAuthenticationResult.Failed(reason);
    }

    private void Record(AdministratorAuthenticationRequest request, string admin, string outcome, AuditOutcome auditOutcome, string? via = null)
    {
        Dictionary<string, string> properties = new()
        {
            ["app"] = request.App.Id,
            ["operation"] = request.Action,
            ["requestedBy"] = request.RequestedBy?.LoginName.Value ?? "-",
            ["admin"] = string.IsNullOrWhiteSpace(admin) ? "-" : admin,
            ["outcome"] = outcome
        };
        if (via is not null)
        {
            properties["via"] = via;
        }

        _auditLog.Record(new AuditEntry(
            _clock(),
            Guid.NewGuid(),
            request.RequestedBy?.UserId.ToString() ?? "system",
            AuditAction,
            request.App.Id,
            auditOutcome,
            properties));
    }
}
