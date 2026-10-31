namespace HackerOs.Theming.Abstractions;

/// <summary>Result of loading the cursor themes of the files: the accepted themes and every warning.</summary>
/// <param name="Themes">Accepted themes: <c>/etc</c> first, then <c>~</c>, each folder by folder name.</param>
/// <param name="Diagnostics">Warnings and errors of every theme.</param>
public sealed record CursorThemeLibraryResult(IReadOnlyList<CursorTheme> Themes, IReadOnlyList<ThemeFileDiagnostic> Diagnostics)
{
    /// <summary>Gets an empty result.</summary>
    public static CursorThemeLibraryResult Empty { get; } = new([], []);

    /// <summary>Gets the built-in themes followed by <see cref="Themes"/>, as Settings lists them.</summary>
    public IEnumerable<CursorTheme> All => CursorThemeCatalog.Themes.Concat(Themes);

    /// <summary>
    /// Resolves the id written in <c>cursors.config</c>: a built-in theme, a theme of the files, or
    /// <c>system</c> when no theme has that id (a deleted folder, a mistyped id).
    /// </summary>
    /// <param name="id">Requested id.</param>
    public CursorTheme Resolve(string? id) =>
        CursorThemeCatalog.TryGet(id, out CursorTheme builtIn)
            ? builtIn
            : Themes.FirstOrDefault(theme => theme.Id == id) ?? CursorThemeCatalog.System;
}

/// <summary>
/// The cursor themes of the files (ADR 0205 §1): one folder per theme in
/// <c>/etc/hackeros/cursors/</c> (administrator, every user) and <c>~/.config/hackeros/cursors/</c>
/// (the user), each with a <c>cursor.theme</c> index and its PNG images. Like the <c>.theme</c>
/// files (ADR 0103 §2.2), an id that repeats a built-in theme or an <c>/etc</c> theme is refused.
/// </summary>
public static class CursorThemeLibrary
{
    /// <summary>Folder of the cursor themes installed for every user.</summary>
    public const string SystemDirectory = "/etc/hackeros/cursors";

    /// <summary>Gets the folder of a user's own cursor themes.</summary>
    /// <param name="homeDirectory">The user's home, such as <c>/home/alice</c>.</param>
    public static string GetUserDirectory(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return $"{homeDirectory.TrimEnd('/')}/.config/hackeros/cursors";
    }

    /// <summary>
    /// Reads every theme folder of both places through the caller's own file access (an app passes
    /// <c>HackDirectory</c>/<c>HackFile</c>, the shell its gateways). A missing place is normal; an
    /// unreadable index or image is skipped with a warning.
    /// </summary>
    /// <param name="listDirectories">Lists the sub-folders of a folder (absolute paths); may throw when it is missing.</param>
    /// <param name="readText">Reads a file as UTF-8 text.</param>
    /// <param name="readBytes">Reads a whole file; the library refuses files over <see cref="CursorThemeFile.MaxImageBytes"/> after reading.</param>
    /// <param name="homeDirectory">Session user's home, or <see langword="null"/> to read <c>/etc</c> only.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<CursorThemeLibraryResult> LoadAsync(
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> listDirectories,
        Func<string, CancellationToken, Task<string>> readText,
        Func<string, CancellationToken, Task<byte[]>> readBytes,
        string? homeDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listDirectories);
        ArgumentNullException.ThrowIfNull(readText);
        ArgumentNullException.ThrowIfNull(readBytes);
        List<CursorTheme> themes = [];
        List<ThemeFileDiagnostic> diagnostics = [];
        List<(string Directory, ThemeOrigin Origin)> places = [(SystemDirectory, ThemeOrigin.System)];
        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            places.Add((GetUserDirectory(homeDirectory), ThemeOrigin.User));
        }

        foreach ((string directory, ThemeOrigin origin) in places)
        {
            IReadOnlyList<string> folders;
            try
            {
                folders = await listDirectories(directory, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Nobody installed a cursor theme there: the normal case, not a warning.
                continue;
            }

            foreach (string folder in folders.Order(StringComparer.Ordinal))
            {
                CursorTheme? theme = await LoadThemeAsync(folder, origin, readText, readBytes, diagnostics, cancellationToken).ConfigureAwait(false);
                if (theme is null)
                {
                    continue;
                }

                string indexPath = theme.SourcePath!;
                if (CursorThemeCatalog.TryGet(theme.Id, out _))
                {
                    diagnostics.Add(ThemeFileDiagnostic.Create(null, new ThemeText("Library.BuiltInId", theme.Id), isError: true, indexPath));
                }
                else if (themes.FirstOrDefault(existing => existing.Id == theme.Id) is { } previous)
                {
                    diagnostics.Add(ThemeFileDiagnostic.Create(null, new ThemeText("Library.IdInUse", theme.Id, previous.SourcePath), isError: true, indexPath));
                }
                else
                {
                    themes.Add(theme);
                }
            }
        }

        return new CursorThemeLibraryResult(themes, diagnostics);
    }

    private static async Task<CursorTheme?> LoadThemeAsync(
        string folder,
        ThemeOrigin origin,
        Func<string, CancellationToken, Task<string>> readText,
        Func<string, CancellationToken, Task<byte[]>> readBytes,
        List<ThemeFileDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        string root = folder.TrimEnd('/');
        string indexPath = $"{root}/{CursorThemeFile.IndexFileName}";
        string content;
        try
        {
            content = await readText(indexPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(ThemeFileDiagnostic.Create(null, new ThemeText("Library.Unreadable", exception.Message), isError: true, indexPath));
            return null;
        }

        CursorThemeParseResult parsed = CursorThemeFile.Parse(content, indexPath);
        diagnostics.AddRange(parsed.Diagnostics);
        if (parsed.Index is not { } index)
        {
            return null;
        }

        Dictionary<string, CursorImage> images = new(StringComparer.Ordinal);
        foreach (CursorThemeEntry entry in index.Entries)
        {
            string imagePath = $"{root}/{entry.FileName}";
            try
            {
                byte[] bytes = await readBytes(imagePath, cancellationToken).ConfigureAwait(false);
                if (!CursorThemeFile.TryValidateImage(bytes, entry.HotspotX, entry.HotspotY, out string? reason))
                {
                    diagnostics.Add(ThemeFileDiagnostic.Create(
                        entry.Line,
                        new ThemeText("Cursor.ImageRefused", entry.FileName, new ThemeText(reason!, CursorThemeFile.MaxImageBytes / 1024, CursorThemeFile.MaxImageSize)),
                        isError: false,
                        indexPath));
                    continue;
                }

                images[entry.Name] = new CursorImage(CursorThemeFile.ToDataUrl(bytes), entry.HotspotX, entry.HotspotY);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(ThemeFileDiagnostic.Create(
                    entry.Line,
                    new ThemeText("Cursor.ImageRefused", entry.FileName, new ThemeText("Library.Unreadable", exception.Message)),
                    isError: false,
                    indexPath));
            }
        }

        return new CursorTheme(index.Id, index.Name, origin, indexPath, images);
    }
}
