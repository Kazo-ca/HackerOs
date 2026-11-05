using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Tests.Support;

/// <summary>Scriptable <see cref="IAppIntentGateway"/> double for command/app tests.</summary>
public sealed class FakeAppIntentGateway : IAppIntentGateway
{
    private readonly Dictionary<string, AppIntentLaunchResult> _results = new(StringComparer.Ordinal);
    private readonly HashSet<string> _denied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AppIntentOpenFileResult> _openResults = new(StringComparer.Ordinal);

    /// <summary>Every launch request received, in call order.</summary>
    public List<(string AppId, IReadOnlyList<string> Arguments)> Requests { get; } = [];

    /// <summary>Every open-file request received, in call order.</summary>
    public List<VirtualPath> OpenFileRequests { get; } = [];

    /// <summary>Every open-file request's media type, in call order (parallel to <see cref="OpenFileRequests"/>).</summary>
    public List<string?> OpenFileMediaTypes { get; } = [];

    public FakeAppIntentGateway WithResult(string appId, AppIntentLaunchResult result)
    {
        _results[appId] = result;
        return this;
    }

    public FakeAppIntentGateway WithOpenResult(string path, AppIntentOpenFileResult result)
    {
        _openResults[VirtualPath.Parse(path).Value] = result;
        return this;
    }

    public FakeAppIntentGateway WithCapabilityDenied(string appId)
    {
        _denied.Add(appId);
        return this;
    }

    public ValueTask<AppIntentLaunchResult> LaunchAsync(
        string appId, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        Requests.Add((appId, arguments));

        if (_denied.Contains(appId))
        {
            throw new AppGatewayAccessDeniedException("apps.launch", CapabilityPolicyEvaluation.DenyMissing(1));
        }

        AppIntentLaunchResult result = _results.TryGetValue(appId, out AppIntentLaunchResult? scripted)
            ? scripted
            : new AppIntentLaunchResult(AppIntentLaunchOutcome.Launched);
        return ValueTask.FromResult(result);
    }

    public ValueTask<AppIntentOpenFileResult> OpenFileAsync(
        VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default)
    {
        OpenFileRequests.Add(path);
        OpenFileMediaTypes.Add(mediaType);

        AppIntentOpenFileResult result = _openResults.TryGetValue(path.Value, out AppIntentOpenFileResult? scripted)
            ? scripted
            : new AppIntentOpenFileResult(AppIntentOpenFileOutcome.Opened);
        return ValueTask.FromResult(result);
    }

    /// <summary>Every intent of ADR 0096 received (edit, reveal, URI, settings, app-defined), in call order, as "kind:target".</summary>
    public List<string> IntentRequests { get; } = [];

    /// <summary>Result returned by the ADR 0096 members; <see cref="AppIntentOutcome.Dispatched"/> by default.</summary>
    public AppIntentResult NextIntentResult { get; set; } = new(AppIntentOutcome.Dispatched);

    public ValueTask<AppIntentOpenFileResult> EditFileAsync(
        VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default)
    {
        IntentRequests.Add($"edit:{path.Value}");
        return ValueTask.FromResult(new AppIntentOpenFileResult(AppIntentOpenFileOutcome.Opened));
    }

    public ValueTask<AppIntentResult> RevealFileAsync(VirtualPath path, CancellationToken cancellationToken = default)
    {
        IntentRequests.Add($"reveal:{path.Value}");
        return ValueTask.FromResult(NextIntentResult);
    }

    public ValueTask<AppIntentResult> OpenUriAsync(Uri uri, string? preferredAppId = null, CancellationToken cancellationToken = default)
    {
        IntentRequests.Add($"uri:{uri.OriginalString}");
        return ValueTask.FromResult(NextIntentResult);
    }

    public ValueTask<AppIntentResult> ShowAppSettingsAsync(string appId, CancellationToken cancellationToken = default)
    {
        IntentRequests.Add($"settings:{appId}");
        return ValueTask.FromResult(NextIntentResult);
    }

    public ValueTask<AppIntentResult> SendIntentAsync(
        string intentId, string? payloadJson, string? targetAppId = null, bool rememberChoice = false, CancellationToken cancellationToken = default)
    {
        IntentRequests.Add($"send:{intentId}:{targetAppId}:{rememberChoice}");
        return ValueTask.FromResult(NextIntentResult);
    }
}
