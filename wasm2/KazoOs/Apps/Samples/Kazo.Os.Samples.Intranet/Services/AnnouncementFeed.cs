using Kazo.Os.Samples.Intranet.Models;

namespace Kazo.Os.Samples.Intranet.Services;

/// <summary>The news of the home page (read-only).</summary>
public sealed class AnnouncementFeed
{
    /// <summary>Gets the news items, most recent first.</summary>
    public IReadOnlyList<Announcement> Latest { get; } =
    [
        new("intranet.hack is live", "The intranet now runs as an ASP.Kazo site: a process of the OS, started on the first request.", new DateOnly(2026, 10, 16), "info"),
        new("Password rotation", "Every account must change its password before the end of the month. Use passwd in a terminal.", new DateOnly(2026, 10, 14), "warning"),
        new("Build farm migration", "The CI runners move to the new rack this weekend. Expect red builds.", new DateOnly(2026, 10, 12), "ops"),
    ];
}
