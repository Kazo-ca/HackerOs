using System.ComponentModel.DataAnnotations;

namespace Kazo.Os.Samples.Intranet.Models;

/// <summary>
/// A note of the team wiki: edited by the HTML forms of <c>/Notes</c> (views, anti-forgery) and by the
/// REST API <c>/api/notes</c> (JSON). The same <c>DataAnnotations</c> rules validate both.
/// </summary>
public sealed class Note
{
    /// <summary>
    /// Gets or sets the identifier given by the key generator of the <c>notes</c> store
    /// (<see cref="Services.NoteStore"/>). Not written when 0: IndexedDB numbers a record whose key
    /// path is absent, and would take 0 as the key.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int Id { get; set; }

    /// <summary>Gets or sets the title (3 to 60 characters).</summary>
    [Required]
    [StringLength(60, MinimumLength = 3)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the text of the note.</summary>
    [StringLength(500)]
    public string Body { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the note is pinned at the top of the list.</summary>
    public bool Pinned { get; set; }

    /// <summary>Gets or sets who wrote the note last (the session's display name).</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>Gets or sets when the note was last saved (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A partial update of a note (<c>PATCH /api/notes/{id}</c>): only the fields given change.</summary>
/// <param name="Title">The new title, or <see langword="null"/> to keep it.</param>
/// <param name="Body">The new text, or <see langword="null"/> to keep it.</param>
/// <param name="Pinned">The new pin state, or <see langword="null"/> to keep it.</param>
public sealed record NotePatch(string? Title, string? Body, bool? Pinned);
