using Kazo.Os.Samples.Intranet.Models;

namespace Kazo.Os.Samples.Intranet.Services;

/// <summary>The staff directory: a read-only list, searched by the <c>/People</c> page and <c>/api/people</c>.</summary>
public sealed class StaffDirectory
{
    private static readonly Person[] People =
    [
        new(1, "Ada Lovelace", "Principal engineer", "Platform", "4101", "ada"),
        new(2, "Alan Turing", "Cryptanalyst", "Security", "4102", "aturing"),
        new(3, "Grace Hopper", "Compiler lead", "Platform", "4103", "grace"),
        new(4, "Thomas Anderson", "Developer", "Web", "4210", "neo"),
        new(5, "Trinity", "Network operator", "Security", "4211", "trinity"),
        new(6, "Morpheus", "Team lead", "Web", "4200", "morpheus"),
        new(7, "Margaret Hamilton", "Flight software", "Platform", "4104", "margaret"),
        new(8, "Kevin Flynn", "Game designer", "Labs", "4300", "flynn"),
    ];

    /// <summary>Returns the people that match <paramref name="query"/>, sorted by name.</summary>
    /// <param name="query">The search text; empty returns everybody.</param>
    public IReadOnlyList<Person> Search(string? query) =>
        [.. People.Where(person => person.Matches(query)).OrderBy(person => person.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Returns the person <paramref name="id"/>, or <see langword="null"/>.</summary>
    /// <param name="id">The identifier.</param>
    public Person? Find(int id) => Array.Find(People, person => person.Id == id);
}
