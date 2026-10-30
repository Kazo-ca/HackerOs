using System.Globalization;

namespace HackerOs.AppSdk.WhatsNew;

/// <summary>Category of a change of the change list (ADR 0186 §2).</summary>
public enum HackChangeCategory
{
    /// <summary>Something the user can do that was not possible before (<c>new</c>).</summary>
    New,

    /// <summary>Something that works better than before (<c>improved</c>).</summary>
    Improved,

    /// <summary>A bug that was fixed (<c>fixed</c>).</summary>
    Fixed,

    /// <summary>A change for app authors: SDK, templates, tools (<c>developer</c>).</summary>
    Developer,
}

/// <summary>One change of the change list, in the language of the caller (ADR 0186 §2, §4).</summary>
/// <param name="Id">Its id (a lowercase GUID), which never changes.</param>
/// <param name="Category">Its category.</param>
/// <param name="Title">Its title, in the caller's language (English when it has no text in that language).</param>
/// <param name="Description">Its longer text, or <see langword="null"/>.</param>
/// <param name="PullRequestUrl">The pull request that made it, or <see langword="null"/>.</param>
/// <param name="Links">Pages that tell more (documentation, decision records).</param>
/// <param name="IsNew">Whether the user has not seen it yet: the page shows a <b>New</b> badge.</param>
public sealed record HackChangeEntry(
    string Id,
    HackChangeCategory Category,
    string Title,
    string? Description,
    Uri? PullRequestUrl,
    IReadOnlyList<Uri> Links,
    bool IsNew);

/// <summary>The changes of one version (ADR 0186 §3, §4.5).</summary>
/// <param name="Version">The published version, or <see langword="null"/> for the next one, already deployed.</param>
/// <param name="Since">For the next version: the version it follows (« Since 2.1.0 »); otherwise <see langword="null"/>.</param>
/// <param name="Date">The release date of a published version, when known.</param>
/// <param name="Entries">Its changes, by category (new, improved, fixed, developer).</param>
public sealed record HackChangeGroup(
    string? Version,
    string? Since,
    DateOnly? Date,
    IReadOnlyList<HackChangeEntry> Entries)
{
    /// <summary>Gets whether the group holds the changes deployed before their version is published.</summary>
    public bool IsNextVersion => Version is null;
}

/// <summary>What changed in HackerOS, as the session user sees it (ADR 0186 §4).</summary>
/// <param name="Groups">One group per version, the most recent first.</param>
/// <param name="NewCount">The number of changes the user has not seen yet.</param>
public sealed record HackChangeList(IReadOnlyList<HackChangeGroup> Groups, int NewCount)
{
    /// <summary>Gets an empty list: no change list was served (offline, or a host without one).</summary>
    public static HackChangeList Empty { get; } = new([], 0);
}

/// <summary>
/// Trusted OS gateway behind <see cref="HackWhatsNew"/> (ADR 0186 §4). It always works for the
/// session user: no capability is needed to read what changed or to mark it seen.
/// </summary>
public interface IAppWhatsNewGateway
{
    /// <summary>Gets whether the host serves a change list.</summary>
    bool IsSupported { get; }

    /// <summary>Reads the change list and which changes the session user has seen.</summary>
    /// <param name="culture">The language of the texts.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<HackChangeList> GetAsync(CultureInfo culture, CancellationToken cancellationToken = default);

    /// <summary>Marks changes as seen by the session user: they lose their <b>New</b> badge.</summary>
    /// <param name="ids">The ids of the changes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask MarkSeenAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);

    /// <summary>Calls <paramref name="onChanged"/> each time the changes seen by the session user change.</summary>
    /// <param name="onChanged">The callback.</param>
    /// <param name="cancellationToken">Cancels the subscription.</param>
    /// <returns>Disposing it ends the subscription.</returns>
    ValueTask<IAsyncDisposable> WatchAsync(Func<ValueTask> onChanged, CancellationToken cancellationToken = default);
}
