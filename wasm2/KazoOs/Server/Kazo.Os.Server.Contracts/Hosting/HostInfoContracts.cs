using System.Text.Json.Serialization;

namespace Kazo.Os.Server.Contracts.Hosting;

/// <summary>
/// What the origin that served the OS says about itself (ADR 0130 §4, ADR 0137). The standalone
/// PWA ships a static <see cref="HostInfoRoutes.DiscoveryFile"/> with <see cref="IsServer"/> set
/// to <see langword="false"/>; <c>Kazo.Os.Server</c> answers the same path (and
/// <see cref="HostInfoRoutes.ApiPath"/>) with <see langword="true"/>, so the OS learns at boot,
/// without a failing request, whether a Kazo OS server delivered it.
/// </summary>
/// <param name="Product">Product name, <see cref="HostInfoRoutes.ServerProduct"/> on a server.</param>
/// <param name="IsServer">Whether the origin is a Kazo OS server with its API.</param>
/// <param name="ServerVersion">Server software version; <see langword="null"/> on a static host.</param>
/// <param name="ApiVersion">Preferred API version; <see langword="null"/> on a static host.</param>
public sealed record HostInfoResponse(
    string Product,
    bool IsServer,
    string? ServerVersion = null,
    string? ApiVersion = null);

/// <summary>Paths and names of the host discovery (ADR 0137).</summary>
public static class HostInfoRoutes
{
    /// <summary>The API route a client may call on a known server.</summary>
    public const string ApiPath = "/api/hackeros/host-info";

    /// <summary>
    /// The file the OS reads next to its <c>index.html</c>: a static file on a static host, an
    /// endpoint of the server on <c>Kazo.Os.Server</c>. Relative, so it follows the base path.
    /// </summary>
    public const string DiscoveryFile = "hackeros-host.json";

    /// <summary><see cref="HostInfoResponse.Product"/> of <c>Kazo.Os.Server</c>.</summary>
    public const string ServerProduct = "Kazo OS Server";
}

/// <summary>Source-generated JSON context of the host discovery contracts (trimming and AOT safe).</summary>
[JsonSerializable(typeof(HostInfoResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public sealed partial class HostingContractsJsonContext : JsonSerializerContext { }
