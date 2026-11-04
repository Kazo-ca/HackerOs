using System.Text.RegularExpressions;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.AppSdk.Settings;

/// <summary>
/// Typed access to an app's layered settings files (ADR 0044): defaults declared by the app, then
/// <c>/etc/&lt;appId&gt;/&lt;document&gt;.config</c> (system), then <c>~/.config/&lt;appId&gt;/&lt;document&gt;.config</c>
/// (user), with keys an administrator can lock in the system file's <c>[locked]</c> section.
/// </summary>
/// <remarks>
/// <para>
/// The schema (keys, types, defaults) comes from <see cref="Kazo.Os.App.Abstractions.AppManifest.Settings"/>.
/// The app needs <c>filesystem.private.read</c> to read its two layers and
/// <c>filesystem.private.write</c> to write its user layer.
/// </para>
/// <code>
/// await using KazoSettingsDocument settings = await KazoSettings.OpenAsync(AppContext);
/// long tabSize = settings.Get&lt;long&gt;("Editor.tab-size");
/// await settings.SetAsync("theme", "light");
/// </code>
/// <para>
/// Each method exists in two forms: one taking the app's <see cref="IAppExecutionContext"/>
/// explicitly, and one using the ambient context of ADR 0043 (<see cref="KazoExecutionContext.Current"/>),
/// like the <c>Hack*</c> file API.
/// </para>
/// </remarks>
public static partial class KazoSettings
{
    /// <summary>Default document identifier, giving <c>settings.config</c>.</summary>
    public const string DefaultDocumentId = "settings";

    /// <summary>Opens one settings document of the calling app and starts watching its files.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="documentId">Document name, i.e. the file name without <c>.config</c>.</param>
    /// <param name="cancellationToken">Cancels the initial reads.</param>
    /// <returns>An open document; dispose it to stop watching.</returns>
    /// <exception cref="InvalidOperationException">The app's manifest declares no settings schema.</exception>
    /// <exception cref="ArgumentException"><paramref name="documentId"/> is not a plain file name.</exception>
    /// <exception cref="UnauthorizedAccessException">The app may not read one of its two layers.</exception>
    public static ValueTask<KazoSettingsDocument> OpenAsync(
        IAppExecutionContext context,
        string documentId = DefaultDocumentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateDocumentId(documentId);
        SettingsSchema schema = context.Manifest.Settings is { } declared
            ? SettingsSchema.FromManifest(declared)
            : throw new InvalidOperationException(
                $"App '{context.Manifest.Id}' declares no settings schema in its manifest.");
        return KazoSettingsDocument.OpenAsync(context, schema, documentId, cancellationToken);
    }

    /// <summary>Reads one value of the default document (opens, reads and closes it).</summary>
    /// <typeparam name="T">See <see cref="KazoSettingsDocument.Get{T}"/>.</typeparam>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="qualifiedKey"><c>key</c> or <c>Group.key</c>, as declared.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async ValueTask<T> GetAsync<T>(
        IAppExecutionContext context,
        string qualifiedKey,
        CancellationToken cancellationToken = default)
    {
        await using KazoSettingsDocument document = await OpenAsync(context, DefaultDocumentId, cancellationToken).ConfigureAwait(false);
        return document.Get<T>(qualifiedKey);
    }

    /// <summary>Writes one value to the user layer of the default document.</summary>
    /// <typeparam name="T">See <see cref="KazoSettingsDocument.Get{T}"/>.</typeparam>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="qualifiedKey"><c>key</c> or <c>Group.key</c>, as declared.</param>
    /// <param name="value">New value.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async ValueTask SetAsync<T>(
        IAppExecutionContext context,
        string qualifiedKey,
        T value,
        CancellationToken cancellationToken = default)
    {
        await using KazoSettingsDocument document = await OpenAsync(context, DefaultDocumentId, cancellationToken).ConfigureAwait(false);
        await document.SetAsync(qualifiedKey, value, SettingLayer.User, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens one settings document of the ambient app (<see cref="KazoExecutionContext.Current"/>).</summary>
    /// <param name="documentId">Document name, i.e. the file name without <c>.config</c>.</param>
    /// <param name="cancellationToken">Cancels the initial reads.</param>
    /// <exception cref="KazoExecutionContextMissingException">No ambient app context is active.</exception>
    /// <exception cref="InvalidOperationException">The app declares no settings schema.</exception>
    public static ValueTask<KazoSettingsDocument> OpenAsync(
        string documentId = DefaultDocumentId,
        CancellationToken cancellationToken = default) =>
        OpenAsync(KazoExecutionContext.Current, documentId, cancellationToken);

    /// <summary>Reads one value of the ambient app's default document.</summary>
    /// <typeparam name="T">See <see cref="KazoSettingsDocument.Get{T}"/>.</typeparam>
    /// <param name="qualifiedKey"><c>key</c> or <c>Group.key</c>, as declared.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static ValueTask<T> GetAsync<T>(string qualifiedKey, CancellationToken cancellationToken = default) =>
        GetAsync<T>(KazoExecutionContext.Current, qualifiedKey, cancellationToken);

    /// <summary>Writes one value to the user layer of the ambient app's default document.</summary>
    /// <typeparam name="T">See <see cref="KazoSettingsDocument.Get{T}"/>.</typeparam>
    /// <param name="qualifiedKey"><c>key</c> or <c>Group.key</c>, as declared.</param>
    /// <param name="value">New value.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static ValueTask SetAsync<T>(string qualifiedKey, T value, CancellationToken cancellationToken = default) =>
        SetAsync(KazoExecutionContext.Current, qualifiedKey, value, cancellationToken);

    /// <summary>
    /// Opens one of the OS's own settings documents (ADR 0056, section 8):
    /// <c>/etc/hackeros/&lt;document&gt;.config</c> (system layer) and
    /// <c>~/.config/hackeros/&lt;document&gt;.config</c> (user layer), with the schema the OS declares.
    /// </summary>
    /// <remarks>
    /// No capability is required: the files' permissions decide (decision D16). Everybody reads;
    /// the session user writes their own layer; writing <see cref="SettingLayer.System"/> needs
    /// root (<c>sudo</c>) or the administrator approval of ADR 0042, otherwise
    /// <see cref="UnauthorizedAccessException"/> is thrown. Locks, layering and tolerance to invalid
    /// lines are the same as for an app document (ADR 0044). The OS applies its own changes itself
    /// (for <c>appearance</c>, the shell retheme as soon as the file changes).
    /// <code>
    /// await using KazoSettingsDocument appearance = await KazoSettings.OpenSystemAsync(context, "appearance");
    /// await appearance.SetAsync("desktopThemeId", "gothic");
    /// </code>
    /// </remarks>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="documentId">OS document id: <c>appearance</c>, <c>ui-platform-preference</c> or <c>security</c> (system only).</param>
    /// <param name="cancellationToken">Cancels the initial reads.</param>
    /// <returns>An open document; dispose it to stop watching.</returns>
    /// <exception cref="ArgumentException">The OS declares no such document.</exception>
    /// <exception cref="NotSupportedException">The host provides no OS settings access.</exception>
    public static ValueTask<KazoSettingsDocument> OpenSystemAsync(
        IAppExecutionContext context,
        string documentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateDocumentId(documentId);
        return KazoSettingsDocument.OpenSystemAsync(context.SystemSettings, documentId, cancellationToken);
    }

    /// <summary>Opens one OS settings document for the ambient app (<see cref="KazoExecutionContext.Current"/>).</summary>
    /// <param name="documentId">OS document id, such as <c>appearance</c>.</param>
    /// <param name="cancellationToken">Cancels the initial reads.</param>
    /// <exception cref="KazoExecutionContextMissingException">No ambient app context is active.</exception>
    /// <exception cref="ArgumentException">The OS declares no such document.</exception>
    public static ValueTask<KazoSettingsDocument> OpenSystemAsync(string documentId, CancellationToken cancellationToken = default) =>
        OpenSystemAsync(KazoExecutionContext.Current, documentId, cancellationToken);

    private static void ValidateDocumentId(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        if (!DocumentIdPattern().IsMatch(documentId))
        {
            throw new ArgumentException(
                $"Settings document id '{documentId}' must be a plain name (letters, digits, '.', '-', '_').",
                nameof(documentId));
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-][A-Za-z0-9._-]*$")]
    private static partial Regex DocumentIdPattern();
}
