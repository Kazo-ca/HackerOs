namespace Kazo.Os.Samples.Intranet.Models;

/// <summary>A news item of the home page.</summary>
/// <param name="Title">The headline.</param>
/// <param name="Text">The body.</param>
/// <param name="PostedOn">The day it was posted.</param>
/// <param name="Severity">The badge: <c>info</c>, <c>warning</c> or <c>ops</c>.</param>
public sealed record Announcement(string Title, string Text, DateOnly PostedOn, string Severity);
