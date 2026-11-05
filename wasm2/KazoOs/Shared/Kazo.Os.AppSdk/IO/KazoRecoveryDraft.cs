using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kazo.Os.AppSdk.IO;

/// <summary>A recovery draft read back by <see cref="KazoRecoveryDraft.TryReadAsync"/>.</summary>
/// <param name="Content">The unsaved text.</param>
/// <param name="OriginalPath">The file the text was being edited from, or <see langword="null"/> for a new buffer.</param>
/// <param name="SavedAt">Simulated time the draft was written.</param>
public sealed record KazoRecoveryDraftContent(string Content, string? OriginalPath, DateTimeOffset SavedAt);

/// <summary>
/// A recovery draft of unsaved work, kept in the app's own state directory
/// (<c>~/.local/state/&lt;appId&gt;/recovery/&lt;name&gt;.json</c>, <c>XDG_STATE_HOME</c>), like the
/// <c>.swp</c> file of vim or the autosave of a desktop editor. An app writes it when the OS asks it
/// to stop with unsaved changes (ADR 0069: logout, <c>kill</c>, "End task") and offers it back at its
/// next start, so nothing is lost silently.
/// </summary>
/// <example>
/// <code>
/// protected override Task OnAppStopRequestedAsync(KazoStopRequestedEventArgs e) =>
///     _document.IsDirty
///         ? KazoRecoveryDraft.Open().SaveAsync(_document.Content, _document.Path)
///         : Task.CompletedTask;
///
/// // At the next start:
/// if (await KazoRecoveryDraft.Open().TakeAsync() is { } draft)
/// {
///     _document.Restore(draft.Content);
/// }
/// </code>
/// </example>
/// <remarks>
/// Everything goes through the app's own file gateway and ambient context: the draft belongs to the
/// user running the app, under their home, with the permissions of a new file there. The directory
/// is created <c>0700</c>, so other users never read a draft.
/// </remarks>
public sealed class KazoRecoveryDraft
{
    private KazoRecoveryDraft(string directory, string path)
    {
        Directory = directory;
        Path = path;
    }

    /// <summary>Gets the absolute path of the draft file.</summary>
    public string Path { get; }

    /// <summary>Gets the absolute path of the app's recovery directory.</summary>
    public string Directory { get; }

    /// <summary>Opens the draft <paramref name="name"/> of the app running the current code.</summary>
    /// <param name="name">Draft name: letters, digits, <c>.</c>, <c>-</c> and <c>_</c>; one per document an app may keep.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or contains another character.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static KazoRecoveryDraft Open(string name = "draft")
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || !name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            throw new ArgumentException($"'{name}' is not a valid draft name.", nameof(name));
        }

        IAppExecutionContext context = KazoExecutionContext.Current;
        string home = context.IsRootElevated ? "/root" : context.HomeDirectory;
        string directory = $"{home.TrimEnd('/')}/.local/state/{context.Manifest.Id}/recovery";
        return new KazoRecoveryDraft(directory, $"{directory}/{name}.json");
    }

    /// <summary>Writes (or replaces) the draft.</summary>
    /// <param name="content">The unsaved text.</param>
    /// <param name="originalPath">The file being edited, if any.</param>
    /// <param name="cancellationToken">Cancels the write; pass <see cref="CancellationToken.None"/> from a stop handler, whose process token is about to be cancelled.</param>
    public async Task SaveAsync(string content, string? originalPath = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        DateTimeOffset now = KazoExecutionContext.Current.Clock.UtcNow;
        await KazoDirectory.CreateDirectoryAsync(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, cancellationToken)
            .ConfigureAwait(false);
        string json = JsonSerializer.Serialize(new KazoRecoveryDraftContent(content, originalPath, now), KazoRecoveryDraftJsonContext.Default.KazoRecoveryDraftContent);
        await KazoFile.WriteAllTextAsync(Path, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the draft, or <see langword="null"/> when there is none or it is unreadable.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<KazoRecoveryDraftContent?> TryReadAsync(CancellationToken cancellationToken = default)
    {
        if (!await KazoFile.ExistsAsync(Path, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            string json = await KazoFile.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize(json, KazoRecoveryDraftJsonContext.Default.KazoRecoveryDraftContent) is { Content: not null } draft ? draft : null;
        }
        catch (Exception exception) when (exception is JsonException or KazoIOException or KazoUnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads the draft and deletes it: the app now holds the text again, as unsaved work.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The draft, or <see langword="null"/> when there was none.</returns>
    public async Task<KazoRecoveryDraftContent?> TakeAsync(CancellationToken cancellationToken = default)
    {
        KazoRecoveryDraftContent? draft = await TryReadAsync(cancellationToken).ConfigureAwait(false);
        if (draft is not null)
        {
            await DeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        return draft;
    }

    /// <summary>Deletes the draft, if any.</summary>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (await KazoFile.ExistsAsync(Path, cancellationToken).ConfigureAwait(false))
        {
            await KazoFile.DeleteAsync(Path, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Trim-safe serializer of <see cref="KazoRecoveryDraftContent"/>.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(KazoRecoveryDraftContent))]
internal sealed partial class KazoRecoveryDraftJsonContext : JsonSerializerContext;
