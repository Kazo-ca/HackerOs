using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using HackerOs.App.Abstractions;

namespace HackerOs.Platform.Core.Discovery;

/// <summary>
/// Pairs a validated manifest with its resolved, concrete entry-point type and owning assembly,
/// per `P1-APP-001`. Constructing a descriptor never instantiates app code.
/// </summary>
/// <param name="Manifest">Validated manifest already accepted into an <see cref="AppCatalog"/>.</param>
/// <param name="EntryPointType">Concrete, non-instantiated entry-point type declared by the manifest.</param>
/// <param name="Assembly">Assembly that owns <paramref name="EntryPointType"/>.</param>
public sealed record AppDescriptor(
    AppManifest Manifest,
    [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type EntryPointType,
    Assembly Assembly)
{
    /// <summary>
    /// Gets the entry-point types the manifest declares for other platforms than the one discovery
    /// resolved <see cref="EntryPointType"/> for, such as a mobile window (<c>MobileEntryPoint</c>, ADR 0171 §1).
    /// Empty when the app has one entry point for every platform.
    /// </summary>
    public IReadOnlyDictionary<AppPlatformId, Type> PlatformEntryPointTypes { get; init; } =
        new Dictionary<AppPlatformId, Type>();

    /// <summary>
    /// Gets the entry-point type to show on <paramref name="platform"/>: the one the manifest declares for it,
    /// or <see cref="EntryPointType"/>.
    /// </summary>
    /// <param name="platform">The active platform (Desktop or Mobile).</param>
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2073",
        Justification = "Every platform entry point lives in an app assembly a trimmed host keeps whole (ADR 0113).")]
    public Type EntryPointTypeFor(AppPlatformId platform) =>
        PlatformEntryPointTypes.TryGetValue(platform, out Type? type) ? type : EntryPointType;
}

/// <summary>Describes one deterministic entry-point resolution failure.</summary>
/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="AppId">App whose entry point failed to resolve.</param>
/// <param name="Message">Human-readable diagnostic.</param>
public sealed record AppDiscoveryError(string Code, string AppId, string Message);

/// <summary>Contains either every resolved descriptor or every deterministic discovery error.</summary>
/// <param name="Descriptors">Resolved descriptors keyed by app ID, or <see langword="null"/> on failure.</param>
/// <param name="Errors">Every discovery error found, in deterministic order.</param>
public sealed record AppDiscoveryResult(
    IReadOnlyDictionary<string, AppDescriptor>? Descriptors,
    IReadOnlyList<AppDiscoveryError> Errors)
{
    /// <summary>Gets whether every manifest in the catalog resolved to a valid entry point.</summary>
    public bool IsSuccess => Descriptors is not null && Errors.Count == 0;
}
