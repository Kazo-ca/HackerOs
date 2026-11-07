using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Shows the connected server's network relay policy (ADR 0102 §5) inside Settings &gt; Server
/// Connection: allowed ports per protocol, private networks, quotas, the apps the server grants
/// the relay to, and a warning per setting that weakens the defaults.
/// </summary>
/// <remarks>
/// Uses only <see cref="KazoServerConnection.GetRelayPolicyAsync(IAppExecutionContext, CancellationToken)"/>
/// (capability <c>server.connection.manage</c>, which Settings declares). Rendered only while the
/// device is connected; a server that cannot be reached shows its error instead of the table.
/// </remarks>
public sealed partial class ServerRelayPolicyPanel
{
    private KazoServerRelayPolicy? _policy;
    private string? _error;
    private bool _isLoading = true;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        try
        {
            _policy = await KazoServerConnection.GetRelayPolicyAsync(Context, Context.CancellationToken);
        }
        catch (KazoServerConnectionException exception)
        {
            _error = exception.Message;
        }
        catch (Exception exception) when (exception is NotSupportedException or UnauthorizedAccessException)
        {
            // A host without the relay client, or a Settings build without the capability.
            _policy = null;
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>Formats a port list; an empty list means every port.</summary>
    private string Ports(IReadOnlyList<int> ports) =>
        _policy is { AllowAllPorts: true } || ports.Count == 0 ? L["Relay.AllPorts"] : string.Join(", ", ports);

    /// <summary>A block-list, or "none" when the server blocks no port of that protocol by list.</summary>
    private string BlockedPorts(IReadOnlyList<int> ports) =>
        ports.Count == 0 ? L["Relay.NoBlockedPorts"] : string.Join(", ", ports);

    private string Quotas(KazoServerRelayPolicy policy) =>
        policy.QuotasEnabled
            ? L["Relay.QuotasValue",
                policy.MaxConcurrentRequests,
                policy.OpenStreams,
                policy.MaxStreams,
                policy.MaxResponseBytes / (1024 * 1024),
                policy.StreamIdleTimeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)]
            : L["Relay.Disabled"];
}
