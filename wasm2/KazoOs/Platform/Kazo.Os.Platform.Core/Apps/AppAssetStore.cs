using System.Collections.Concurrent;
using System.Security.Cryptography;
using Kazo.Os.App.Abstractions;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.Platform.Core.Apps;

/// <summary>
/// Where the bytes of an app's declared assets come from (ADR 0098 §5). The browser host fetches
/// <c>_content/&lt;assembly&gt;/&lt;path&gt;</c>, the static web asset Blazor publishes from the app
/// project's <c>wwwroot/</c>; tests and other hosts supply their own.
/// </summary>
public interface IAppAssetSource
{
    /// <summary>Reads one asset's bytes.</summary>
    /// <param name="manifest">The app the asset belongs to.</param>
    /// <param name="assetPath">The package-relative path declared in <see cref="AppManifest.Assets"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes, or <see langword="null"/> when the host has no such file.</returns>
    ValueTask<byte[]?> ReadAsync(AppManifest manifest, string assetPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// The source of a host that serves no app asset (tests, a host without static assets): every read finds no
/// file. <see cref="AppAssetStore"/> then reports the asset missing, never damaged.
/// </summary>
public sealed class NoAppAssetSource : IAppAssetSource
{
    /// <summary>Gets the shared instance.</summary>
    public static NoAppAssetSource Instance { get; } = new();

    private NoAppAssetSource()
    {
    }

    /// <inheritdoc />
    public ValueTask<byte[]?> ReadAsync(AppManifest manifest, string assetPath, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<byte[]?>(null);
}

/// <summary>One app whose asset did not match the hash of its manifest (ADR 0098 §5).</summary>
/// <param name="AppId">The damaged app.</param>
/// <param name="AssetPath">The first asset found altered.</param>
/// <param name="Reason">Human-readable reason (expected and actual hash).</param>
public sealed record AppIntegrityProblem(string AppId, string AssetPath, string Reason);

/// <summary>Thrown when an asset's bytes do not match the SHA-256 its manifest declares.</summary>
public sealed class AppAssetIntegrityException : IOException
{
    /// <summary>Creates the exception for one altered asset.</summary>
    /// <param name="problem">What was found.</param>
    public AppAssetIntegrityException(AppIntegrityProblem problem)
        : base($"Asset '{problem.AssetPath}' of '{problem.AppId}' is damaged: {problem.Reason}") =>
        Problem = problem ?? throw new ArgumentNullException(nameof(problem));

    /// <summary>Gets what was found.</summary>
    public AppIntegrityProblem Problem { get; }
}

/// <summary>
/// Reads app assets and checks each one against its manifest hash at its <b>first read</b> in the
/// session (ADR 0098 §5): icons, intent payload schemas, embedded data. The verdict is kept in
/// memory, so later reads cost no hashing. On a mismatch the asset is refused
/// (<see cref="AppAssetIntegrityException"/>), the app is marked damaged (<see cref="Damaged"/>,
/// shown by Settings) and the event goes to <c>/var/log/syslog</c> through the logger.
/// </summary>
/// <remarks>
/// Like <c>dpkg --verify</c> or a package manager's checksum, this catches a file altered after the
/// build, not a malicious publisher: the hash comes from the same manifest. The app's lazily loaded
/// assemblies stay covered by the integrity of Blazor's boot manifest.
/// </remarks>
public sealed class AppAssetStore
{
    private readonly AppCatalog _catalog;
    private readonly IAppAssetSource _source;
    private readonly ILogger<AppAssetStore>? _logger;
    private readonly ConcurrentDictionary<(string AppId, string Path), bool> _verified = new();
    private readonly ConcurrentDictionary<string, AppIntegrityProblem> _damaged = new(StringComparer.Ordinal);

    /// <summary>Creates the store over the host's catalog.</summary>
    /// <param name="catalog">The catalog whose manifests declare the assets and their hashes.</param>
    /// <param name="source">Where asset bytes come from.</param>
    /// <param name="logger">Receives one warning per damaged asset (written to <c>/var/log/syslog</c>).</param>
    public AppAssetStore(AppCatalog catalog, IAppAssetSource source, ILogger<AppAssetStore>? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _logger = logger;
    }

    /// <summary>Raised when an app becomes damaged.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the damaged apps found so far in this session, by app id.</summary>
    public IReadOnlyDictionary<string, AppIntegrityProblem> Damaged => _damaged;

    /// <summary>Gets whether an altered asset of <paramref name="appId"/> was found in this session.</summary>
    /// <param name="appId">Application identifier.</param>
    /// <returns><see langword="true"/> when the app is damaged.</returns>
    public bool IsDamaged(string appId) => _damaged.ContainsKey(appId);

    /// <summary>
    /// Reads one declared asset, verifying its hash the first time it is read in the session.
    /// </summary>
    /// <param name="appId">The app the asset belongs to.</param>
    /// <param name="assetPath">The package-relative path declared in its manifest.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The asset's bytes, which match the manifest.</returns>
    /// <exception cref="ArgumentException">The app is not in the catalog or does not declare the asset.</exception>
    /// <exception cref="FileNotFoundException">The host has no file for the asset.</exception>
    /// <exception cref="AppAssetIntegrityException">The bytes do not match the declared hash.</exception>
    public async ValueTask<byte[]> ReadAsync(string appId, string assetPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        AppManifest manifest = _catalog.Manifests.TryGetValue(appId, out AppManifest? found)
            ? found
            : throw new ArgumentException($"App '{appId}' is not in the catalog.", nameof(appId));
        AssetManifest asset = manifest.Assets.FirstOrDefault(item => string.Equals(item.Path, assetPath, StringComparison.Ordinal))
            ?? throw new ArgumentException($"'{appId}' declares no asset '{assetPath}'.", nameof(assetPath));

        byte[] bytes = await _source.ReadAsync(manifest, assetPath, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Asset '{assetPath}' of '{appId}' is missing.", assetPath);

        (string, string) key = (appId, assetPath);
        if (_verified.TryGetValue(key, out bool valid))
        {
            return valid ? bytes : throw new AppAssetIntegrityException(_damaged[appId]);
        }

        string actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        valid = string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase);
        _verified[key] = valid;
        if (valid)
        {
            return bytes;
        }

        AppIntegrityProblem problem = new(appId, assetPath, $"expected SHA-256 {asset.Sha256}, got {actual}");
        if (_damaged.TryAdd(appId, problem))
        {
            _logger?.LogWarning("catalog.asset.damaged: asset '{AssetPath}' of '{AppId}' does not match its manifest ({Reason}); the app is marked damaged.", assetPath, appId, problem.Reason);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        throw new AppAssetIntegrityException(_damaged[appId]);
    }
}
