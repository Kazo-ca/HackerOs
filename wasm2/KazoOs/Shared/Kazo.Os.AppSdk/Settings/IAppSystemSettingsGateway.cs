using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.AppSdk.Settings;

/// <summary>Where the two layers of an OS settings document live, and their schema.</summary>
/// <param name="DocumentId">Document id, the file name without <c>.config</c>.</param>
/// <param name="Schema">Keys, types and defaults declared by the OS.</param>
/// <param name="SystemPath"><c>/etc/hackeros/&lt;document&gt;.config</c>.</param>
/// <param name="UserPath"><c>~/.config/hackeros/&lt;document&gt;.config</c>, or an empty string for a system-only document.</param>
/// <param name="IsSystemOnly">Whether the document has no user layer (an administrator policy).</param>
public sealed record AppSystemSettingsDocumentInfo(
    string DocumentId,
    SettingsSchema Schema,
    string SystemPath,
    string UserPath,
    bool IsSystemOnly);

/// <summary>
/// Access to the OS's own layered settings documents (ADR 0056, section 8), behind
/// <see cref="HackSettings.OpenSystemAsync(string, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// No capability is required: the files' permissions decide (decision D16). Everybody reads both
/// layers; the session user writes their own <c>~/.config/hackeros</c> layer; the <c>/etc</c> layer
/// belongs to root and is written with the calling app's own filesystem rights, so it needs
/// <c>sudo</c> or the administrator approval of ADR 0042.
/// </remarks>
public interface IAppSystemSettingsGateway
{
    /// <summary>Describes one OS document.</summary>
    /// <param name="documentId">Document id, such as <c>appearance</c>.</param>
    /// <exception cref="ArgumentException">The OS declares no such document.</exception>
    AppSystemSettingsDocumentInfo Describe(string documentId);

    /// <summary>Reads and merges both layers over the declared defaults; never fails on a bad file.</summary>
    /// <param name="documentId">Document id.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    ValueTask<LayeredConfigSnapshot> ReadAsync(string documentId, CancellationToken cancellationToken = default);

    /// <summary>Writes already-normalized values to one layer in a single file edit.</summary>
    /// <param name="documentId">Document id.</param>
    /// <param name="values">Declared keys and their serialized values.</param>
    /// <param name="target"><see cref="SettingLayer.User"/> or <see cref="SettingLayer.System"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="UnauthorizedAccessException">The file's permissions deny the write.</exception>
    /// <exception cref="InvalidOperationException">A key is locked, or the user layer of a system-only document is targeted.</exception>
    ValueTask SetAsync(
        string documentId,
        IReadOnlyList<KeyValuePair<string, string>> values,
        SettingLayer target,
        CancellationToken cancellationToken = default);

    /// <summary>Removes one key from one layer so the next layer applies again.</summary>
    /// <param name="documentId">Document id.</param>
    /// <param name="qualifiedKey">Declared key.</param>
    /// <param name="target">Layer to edit.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask ResetAsync(string documentId, string qualifiedKey, SettingLayer target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls <paramref name="onChanged"/> after either file of the document changed, whoever wrote
    /// it. Dispose the result to stop. The callback runs inside the app's ambient scope.
    /// </summary>
    /// <param name="documentId">Document id.</param>
    /// <param name="onChanged">Callback; exceptions it throws are ignored.</param>
    /// <param name="cancellationToken">Cancels the subscription.</param>
    ValueTask<IAsyncDisposable> WatchAsync(string documentId, Func<ValueTask> onChanged, CancellationToken cancellationToken = default);
}
