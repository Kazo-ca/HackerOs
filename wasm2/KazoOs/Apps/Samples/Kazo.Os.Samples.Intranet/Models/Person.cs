namespace Kazo.Os.Samples.Intranet.Models;

/// <summary>An entry of the staff directory (<c>/People</c>, <c>/api/people</c>).</summary>
/// <param name="Id">The identifier used in URLs.</param>
/// <param name="Name">The full name.</param>
/// <param name="Role">The job title.</param>
/// <param name="Team">The team the person belongs to.</param>
/// <param name="Extension">The internal phone extension.</param>
/// <param name="Handle">The chat handle, shown with an <c>@</c>.</param>
public sealed record Person(int Id, string Name, string Role, string Team, string Extension, string Handle)
{
    /// <summary>Returns whether the name, role, team or handle contains <paramref name="query"/> (case ignored).</summary>
    /// <param name="query">The text typed in the search box; empty matches everybody.</param>
    public bool Matches(string? query) =>
        string.IsNullOrWhiteSpace(query)
        || Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
        || Role.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
        || Team.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
        || Handle.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
}
