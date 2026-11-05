using Kazo.Os.Simulation.Abstractions.Events;

namespace Kazo.Os.Platform.Core.Events;

/// <summary>
/// The trusted platform kernel's own <see cref="PublisherIdentity"/>, used to register and publish to
/// kernel-only shared channels (e.g. the filesystem-change channel). <see cref="AppId"/> can never be a
/// real installed app's ID: <c>AppManifestValidator.ValidateAppId</c> requires at least three
/// dot-separated reverse-domain segments, so no validated manifest can ever produce this single-segment
/// value — no app can present this identity through the trusted <c>AppExecutionContextFactory</c> path.
/// </summary>
public static class KernelPublisherIdentity
{
    /// <summary>The reserved app ID no installed app manifest can ever validate to.</summary>
    public const string AppId = "kernel";

    /// <summary>The kernel's own publisher/owner identity.</summary>
    public static readonly PublisherIdentity Value = new(AppId, "system", "0");
}
