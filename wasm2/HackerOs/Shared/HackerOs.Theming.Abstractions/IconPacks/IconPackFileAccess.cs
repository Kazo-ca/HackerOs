namespace HackerOs.Theming.Abstractions.IconPacks;

/// <summary>
/// The caller's own file access, through which <see cref="IconPackLibrary"/> reads packs: an app passes
/// <c>HackDirectory</c>/<c>HackFile</c>, the shell its gateways, so the files' permissions decide what
/// each one sees (ADR 0203 §2). Each function may throw when the path is missing or unreadable.
/// </summary>
/// <param name="ListDirectories">Lists the sub-folders of a folder, as absolute paths.</param>
/// <param name="ListFiles">Lists the files of a folder, as absolute paths.</param>
/// <param name="ReadAllBytes">Reads a whole file.</param>
public sealed record IconPackFileAccess(
    Func<string, CancellationToken, Task<IReadOnlyList<string>>> ListDirectories,
    Func<string, CancellationToken, Task<IReadOnlyList<string>>> ListFiles,
    Func<string, CancellationToken, Task<byte[]>> ReadAllBytes);
