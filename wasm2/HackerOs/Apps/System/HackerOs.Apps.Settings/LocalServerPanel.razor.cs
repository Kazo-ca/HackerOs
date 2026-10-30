using HackerOs.AppSdk;
using HackerOs.AppSdk.Administration;
using HackerOs.AppSdk.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Settings;

/// <summary>
/// Settings &gt; Server Connection &gt; Local server (ADR 0191 §6, guide
/// <c>doc/en/wasm/dev/installable-server.md</c>): where to download the installable HackerOS server
/// for each system, a "Test" button for its address, and pairing with the one-time code the server
/// printed, without a password.
/// </summary>
/// <remarks>
/// Uses only <see cref="HackServerConnection"/> (capability <c>server.connection.manage</c>).
/// The address is called only when the user presses Test or Pair: the OS never probes
/// <c>localhost</c> by itself, which would show Chrome's "Local Network Access" prompt to every
/// visitor of the GitHub Pages OS (ADR 0191 §3).
/// </remarks>
public sealed partial class LocalServerPanel
{
    /// <summary>Address of a server installed on this computer (install scripts' default port).</summary>
    internal const string DefaultAddress = "http://localhost:5888";

    /// <summary>The releases page; the latest release carries the server archives (ADR 0191 §5).</summary>
    internal const string ReleasesUrl = "https://github.com/Kazo-ca/HackerOs/releases/latest";

    /// <summary>The server image (ADR 0191 §4).</summary>
    internal const string DockerImage = "ghcr.io/kazo-ca/hackeros-server";

    private HackServerProbe? _probe;
    private string? _pairError;
    private bool _isTesting;
    private bool _isPairing;
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>Raised with the new connection once this device is paired.</summary>
    [Parameter]
    public EventCallback<HackServerConnectionState> OnPaired { get; set; }

    /// <summary>The server address typed by the user.</summary>
    internal string Address { get; set; } = DefaultAddress;

    /// <summary>The pairing code typed by the user.</summary>
    internal string Code { get; set; } = string.Empty;

    /// <summary>The last test result, or <see langword="null"/> before the first test.</summary>
    internal HackServerProbe? Probe => _probe;

    /// <summary>Why the last pairing failed, or <see langword="null"/>.</summary>
    internal string? PairError => _pairError;

    private bool IsBusy => _isTesting || _isPairing;

    private bool HasValidAddress => TryGetAddress(out _);

    private bool CanPair => HasValidAddress && !string.IsNullOrWhiteSpace(Code);

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= Context.GetLocalizer<Strings>();

    /// <summary>
    /// The server archives of <paramref name="version"/>, one per system, as release.yml names them:
    /// <c>hackeros-server-&lt;version&gt;-&lt;rid&gt;.zip</c> (Windows) or <c>.tar.gz</c>.
    /// </summary>
    /// <param name="version">The OS version; the server of the same version matches it.</param>
    internal static IReadOnlyList<ServerDownload> Downloads(string version) =>
    [
        new("LocalServer.System.WindowsX64", $"hackeros-server-{version}-win-x64.zip"),
        new("LocalServer.System.WindowsArm64", $"hackeros-server-{version}-win-arm64.zip"),
        new("LocalServer.System.MacArm64", $"hackeros-server-{version}-osx-arm64.tar.gz"),
        new("LocalServer.System.MacX64", $"hackeros-server-{version}-osx-x64.tar.gz"),
        new("LocalServer.System.LinuxX64", $"hackeros-server-{version}-linux-x64.tar.gz"),
        new("LocalServer.System.LinuxArm64", $"hackeros-server-{version}-linux-arm64.tar.gz"),
    ];

    /// <summary>Asks the typed address whether it is a HackerOS server.</summary>
    internal async Task TestAsync()
    {
        if (IsBusy || !TryGetAddress(out Uri? address))
        {
            return;
        }

        _isTesting = true;
        _probe = null;
        StateHasChanged();
        try
        {
            _probe = await HackServerConnection.TestAsync(Context, address, Context.CancellationToken);
        }
        catch (Exception exception) when (exception is NotSupportedException or UnauthorizedAccessException)
        {
            _probe = new HackServerProbe(false, null, null, exception.Message);
        }
        finally
        {
            _isTesting = false;
        }
    }

    /// <summary>Links this device with the typed code, then raises <see cref="OnPaired"/>.</summary>
    internal async Task PairAsync()
    {
        if (IsBusy || !CanPair || !TryGetAddress(out Uri? address))
        {
            return;
        }

        _isPairing = true;
        _pairError = null;
        StateHasChanged();
        HackServerConnectionState? state = null;
        try
        {
            state = await HackServerConnection.PairAsync(Context, address, Code, Context.CancellationToken);
            Code = string.Empty;
        }
        catch (Exception exception) when (exception is HackServerConnectionException or NotSupportedException or UnauthorizedAccessException)
        {
            _pairError = L["LocalServer.PairFailed", exception.Message];
        }
        finally
        {
            _isPairing = false;
        }

        if (state is { IsConnected: true })
        {
            await OnPaired.InvokeAsync(state);
        }
    }

    private bool TryGetAddress([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? address)
    {
        address = null;
        if (!Uri.TryCreate(Address?.Trim(), UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            return false;
        }

        // A base address ends with '/': the API paths are resolved relative to it.
        address = parsed.AbsolutePath.EndsWith('/') ? parsed : new Uri(parsed + "/");
        return true;
    }
}

/// <summary>One server archive of the download list.</summary>
/// <param name="SystemKey">Text key of the system's name.</param>
/// <param name="FileName">The archive's file name in the release.</param>
internal sealed record ServerDownload(string SystemKey, string FileName);
