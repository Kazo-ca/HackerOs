using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk;

/// <summary>
/// Turns gateway-level errors into the <c>System.IO</c>-style exceptions the <c>Hack*</c> facades
/// document (ADR 0043, section 4), so app code catches one family of exceptions.
/// </summary>
internal static class HackGatewayErrors
{
    /// <summary>Maps a capability denial to <see cref="HackUnauthorizedAccessException"/>, keeping the capability.</summary>
    /// <param name="exception">The gateway's denial.</param>
    public static HackUnauthorizedAccessException ToUnauthorized(AppGatewayAccessDeniedException exception) =>
        new($"This app lacks the capability '{exception.Capability}' ({exception.Evaluation.Reason}).",
            requiredCapability: exception.Capability);
}
