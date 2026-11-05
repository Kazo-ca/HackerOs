using System.Collections.Immutable;

namespace Kazo.Os.Simulation.Abstractions.Network;

// ---------------------------------------------------------------------------
// Simulated Page — structured content returned by website controllers.
// Each section is a discriminated union variant that Blazor components render.
// No raw HTML is ever produced; this keeps the domain testable and XSS-free.
// ---------------------------------------------------------------------------

/// <summary>
/// Marker base for all typed simulated page section variants.
/// </summary>
public abstract record SimulatedPageSection;

/// <summary>A full-width hero/banner with headline and optional subtitle.</summary>
public sealed record HeroSection(
    string Headline,
    string? Subtitle = null,
    string? Theme = null) : SimulatedPageSection;

/// <summary>A simple paragraph of body text.</summary>
public sealed record ParagraphSection(string Text) : SimulatedPageSection;

/// <summary>A titled list of items.</summary>
public sealed record ListSection(
    string? Title,
    ImmutableArray<string> Items) : SimulatedPageSection;

/// <summary>A login form that posts to the same or a different path.</summary>
public sealed record LoginFormSection(
    string Title,
    string UsernameLabel = "Username",
    string PasswordLabel = "Password",
    string SubmitLabel = "Login",
    string PostPath = "/login") : SimulatedPageSection;

/// <summary>A generic key/value form (contact, registration, etc.).</summary>
public sealed record FormSection(
    string Title,
    ImmutableArray<FormField> Fields,
    string SubmitLabel = "Submit",
    string PostPath = "/submit") : SimulatedPageSection;

/// <summary>A single labeled field in a <see cref="FormSection"/>.</summary>
public sealed record FormField(
    string Id,
    string Label,
    string InputType = "text",
    string? Placeholder = null,
    bool Required = false);

/// <summary>A grid of product/listing cards.</summary>
public sealed record ProductGridSection(
    ImmutableArray<ProductCard> Products) : SimulatedPageSection;

/// <summary>A single card in a <see cref="ProductGridSection"/>.</summary>
public sealed record ProductCard(
    string Title,
    string Description,
    string Price,
    string? Badge = null);

/// <summary>A table with a header row and data rows.</summary>
public sealed record TableSection(
    string? Title,
    ImmutableArray<string> Headers,
    ImmutableArray<ImmutableArray<string>> Rows) : SimulatedPageSection;

/// <summary>A navigation link bar shown inside the simulated page.</summary>
public sealed record NavigationSection(
    ImmutableArray<NavLink> Links) : SimulatedPageSection;

/// <summary>A navigation link entry.</summary>
public sealed record NavLink(string Label, string Href, bool IsActive = false);

/// <summary>A forum thread listing.</summary>
public sealed record ForumSection(
    string SectionTitle,
    ImmutableArray<ForumThread> Threads) : SimulatedPageSection;

/// <summary>A single thread row in a forum section.</summary>
public sealed record ForumThread(
    string Title,
    string Author,
    string TimestampDisplay,
    int Views,
    bool IsHot = false);

/// <summary>An email inbox listing.</summary>
public sealed record EmailListSection(
    ImmutableArray<EmailRow> Emails) : SimulatedPageSection;

/// <summary>A single email row in an inbox.</summary>
public sealed record EmailRow(
    string Subject,
    string Sender,
    string Snippet,
    bool IsRead = true);

/// <summary>A warning/alert banner.</summary>
public sealed record AlertSection(
    string Message,
    AlertLevel Level = AlertLevel.Warning) : SimulatedPageSection;

/// <summary>Severity level for an <see cref="AlertSection"/>.</summary>
public enum AlertLevel
{
    /// <summary>Neutral information.</summary>
    Info,

    /// <summary>Something the visitor should pay attention to (the default).</summary>
    Warning,

    /// <summary>A failure, e.g. a rejected login.</summary>
    Error,
}

/// <summary>
/// Structured response returned by a simulated website controller.
/// The Blazor browser component renders the typed sections; no raw HTML is
/// ever produced, keeping the domain layer XSS-free and headlessly testable.
/// </summary>
public sealed record SimulatedPage(
    string Title,
    string? PageTheme,
    ImmutableArray<SimulatedPageSection> Sections)
{
    /// <summary>Convenience factory for a minimal error page.</summary>
    public static SimulatedPage Error(int statusCode, string message) => new(
        Title: $"Error {statusCode}",
        PageTheme: "error",
        Sections: [new AlertSection($"{statusCode}: {message}", AlertLevel.Error)]);
}
