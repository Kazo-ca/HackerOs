using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Platform.Core.Intents;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Trusted <see cref="IAppIntentGateway"/> implementation that routes launch requests through
/// the real <see cref="AppIntentDispatcher"/>, so any app can request another app be started the
/// same way the desktop shell and terminal already do, instead of only components with direct
/// Blazor DI access to the dispatcher.
/// </summary>
internal sealed class AppIntentGateway(
    Func<AppIntentDispatcher> dispatcherProvider,
    string callerAppId,
    string userId,
    AuthenticatedPrincipal principal) : IAppIntentGateway
{
    public async ValueTask<AppIntentLaunchResult> LaunchAsync(
        string appId, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(arguments);

        AppIntentDispatcher dispatcher = dispatcherProvider();
        AppIntentDispatchResult result = await dispatcher.DispatchAsync(
            new AppIntentRequest(Guid.NewGuid(), callerAppId, userId, new LaunchAppIntent(appId, arguments)),
            principal,
            fullScreen: null,
            cancellationToken);

        return result.Status switch
        {
            AppIntentDispatchStatus.Dispatched =>
                new AppIntentLaunchResult(AppIntentLaunchOutcome.Launched),
            AppIntentDispatchStatus.NotFound =>
                new AppIntentLaunchResult(AppIntentLaunchOutcome.NotFound, result.ErrorCode),
            AppIntentDispatchStatus.Disabled =>
                new AppIntentLaunchResult(AppIntentLaunchOutcome.Disabled, result.ErrorCode),
            AppIntentDispatchStatus.CapabilityDenied =>
                throw new AppGatewayAccessDeniedException(
                    AppCapabilities.AppsLaunch, CapabilityPolicyEvaluation.DenyMissing(1)),
            _ => new AppIntentLaunchResult(AppIntentLaunchOutcome.Faulted, result.ErrorCode)
        };
    }

    public async ValueTask<AppIntentOpenFileResult> OpenFileAsync(
        VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default)
    {
        AppIntentDispatcher dispatcher = dispatcherProvider();
        AppIntentDispatchResult result = await dispatcher.DispatchAsync(
            new AppIntentRequest(
                Guid.NewGuid(), callerAppId, userId, new OpenFileIntent(path, FileIntentAction.Open, mediaType)),
            principal,
            fullScreen: null,
            cancellationToken);

        return result.Status switch
        {
            AppIntentDispatchStatus.Dispatched =>
                new AppIntentOpenFileResult(AppIntentOpenFileOutcome.Opened),
            AppIntentDispatchStatus.ChooserRequired =>
                new AppIntentOpenFileResult(AppIntentOpenFileOutcome.ChooserRequired, result.CandidateAppIds),
            AppIntentDispatchStatus.NotFound =>
                new AppIntentOpenFileResult(AppIntentOpenFileOutcome.NoHandler, ErrorCode: result.ErrorCode),
            AppIntentDispatchStatus.TargetInvalid =>
                new AppIntentOpenFileResult(AppIntentOpenFileOutcome.NoHandler, ErrorCode: result.ErrorCode),
            AppIntentDispatchStatus.CapabilityDenied =>
                throw new AppGatewayAccessDeniedException(
                    AppCapabilities.AppsLaunch, CapabilityPolicyEvaluation.DenyMissing(1)),
            _ => new AppIntentOpenFileResult(AppIntentOpenFileOutcome.Faulted, ErrorCode: result.ErrorCode)
        };
    }

    public async ValueTask<AppIntentOpenFileResult> EditFileAsync(
        VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default)
    {
        AppIntentDispatchResult result = await DispatchAsync(new EditFileIntent(path, mediaType), cancellationToken);
        return result.Status switch
        {
            AppIntentDispatchStatus.Dispatched =>
                new AppIntentOpenFileResult(AppIntentOpenFileOutcome.Opened, FellBackToOpen: result.FellBackToOpen),
            AppIntentDispatchStatus.ChooserRequired =>
                new AppIntentOpenFileResult(AppIntentOpenFileOutcome.ChooserRequired, result.CandidateAppIds, FellBackToOpen: result.FellBackToOpen),
            AppIntentDispatchStatus.NotFound or AppIntentDispatchStatus.TargetInvalid =>
                new AppIntentOpenFileResult(AppIntentOpenFileOutcome.NoHandler, ErrorCode: result.ErrorCode),
            AppIntentDispatchStatus.CapabilityDenied => throw AppsLaunchDenied(),
            _ => new AppIntentOpenFileResult(AppIntentOpenFileOutcome.Faulted, ErrorCode: result.ErrorCode)
        };
    }

    public async ValueTask<AppIntentResult> RevealFileAsync(VirtualPath path, CancellationToken cancellationToken = default) =>
        ToIntentResult(await DispatchAsync(new RevealFileIntent(path), cancellationToken));

    public async ValueTask<AppIntentResult> OpenUriAsync(Uri uri, string? preferredAppId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return ToIntentResult(await DispatchAsync(new OpenUriIntent(uri, preferredAppId), cancellationToken));
    }

    public async ValueTask<AppIntentResult> ShowAppSettingsAsync(string appId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return ToIntentResult(await DispatchAsync(new ShowAppSettingsIntent(appId), cancellationToken));
    }

    public async ValueTask<AppIntentResult> SendIntentAsync(
        string intentId,
        string? payloadJson,
        string? targetAppId = null,
        bool rememberChoice = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        return ToIntentResult(await DispatchAsync(
            new CustomIntent(intentId, payloadJson, targetAppId, rememberChoice), cancellationToken));
    }

    private ValueTask<AppIntentDispatchResult> DispatchAsync(IAppIntent intent, CancellationToken cancellationToken) =>
        dispatcherProvider().DispatchAsync(
            new AppIntentRequest(Guid.NewGuid(), callerAppId, userId, intent),
            principal,
            fullScreen: null,
            cancellationToken);

    /// <summary>Maps a dispatch outcome onto the gateway's (ADR 0096); a missing <c>apps.launch</c> throws like the other members.</summary>
    private static AppIntentResult ToIntentResult(AppIntentDispatchResult result) => result.Status switch
    {
        AppIntentDispatchStatus.Dispatched => new AppIntentResult(AppIntentOutcome.Dispatched),
        AppIntentDispatchStatus.ChooserRequired =>
            new AppIntentResult(AppIntentOutcome.ChooserRequired, result.CandidateAppIds, result.ErrorCode),
        AppIntentDispatchStatus.NotFound when result.ErrorCode is { } code
            && (code.EndsWith(".no-handler", StringComparison.Ordinal) || code == "intent.unknown") =>
            new AppIntentResult(AppIntentOutcome.NoHandler, ErrorCode: code),
        AppIntentDispatchStatus.NotFound => new AppIntentResult(AppIntentOutcome.NotFound, ErrorCode: result.ErrorCode),
        AppIntentDispatchStatus.AccessDenied => new AppIntentResult(AppIntentOutcome.AccessDenied, ErrorCode: result.ErrorCode),
        AppIntentDispatchStatus.Disabled => new AppIntentResult(AppIntentOutcome.Disabled, ErrorCode: result.ErrorCode),
        AppIntentDispatchStatus.Invalid or AppIntentDispatchStatus.TargetInvalid =>
            new AppIntentResult(AppIntentOutcome.Invalid, ErrorCode: result.ErrorCode),
        AppIntentDispatchStatus.CapabilityDenied => throw AppsLaunchDenied(),
        _ => new AppIntentResult(AppIntentOutcome.Faulted, ErrorCode: result.ErrorCode)
    };

    private static AppGatewayAccessDeniedException AppsLaunchDenied() =>
        new(AppCapabilities.AppsLaunch, CapabilityPolicyEvaluation.DenyMissing(1));
}
