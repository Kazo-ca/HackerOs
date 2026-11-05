using System.Text.Json.Serialization;

namespace Kazo.Os.Samples.Intranet.Models;

/// <summary>
/// JSON source generation for everything the site sends or receives (trim-safe on every host). The
/// server registers it in <c>builder.Json</c>; the Blazor board uses it with <c>Site.Http</c>.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Note))]
[JsonSerializable(typeof(NotePatch))]
[JsonSerializable(typeof(IReadOnlyList<Note>))]
[JsonSerializable(typeof(Person))]
[JsonSerializable(typeof(IReadOnlyList<Person>))]
[JsonSerializable(typeof(BoardTask))]
[JsonSerializable(typeof(IReadOnlyList<BoardTask>))]
[JsonSerializable(typeof(SiteStatus))]
[JsonSerializable(typeof(Visitor))]
// The meta store of the notes database keeps plain strings.
[JsonSerializable(typeof(string))]
public sealed partial class IntranetJsonContext : JsonSerializerContext;

/// <summary>What <c>/health</c> answers.</summary>
/// <param name="Status">Always <c>up</c> when the site answers.</param>
/// <param name="Notes">How many notes the site holds.</param>
/// <param name="Tasks">How many cards the board holds.</param>
/// <param name="StartedAt">When the site process started (UTC): it changes when the OS restarts the site.</param>
public sealed record SiteStatus(string Status, int Notes, int Tasks, DateTimeOffset StartedAt);

/// <summary>What <c>/api/me</c> answers: the display name of the caller's cookie session.</summary>
/// <param name="DisplayName">The name chosen on <c>/Home/Profile</c>, or <see langword="null"/>.</param>
public sealed record Visitor(string? DisplayName);
