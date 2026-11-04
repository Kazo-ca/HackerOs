using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Blazor.Files;

/// <summary>
/// The dialogs of the contract <c>kazoos.file-dialogs</c> (ADR 0148 §6, ADR 0155 §5), provided by
/// the File Explorer. An app opens them through <see cref="IDialogService"/>; the OS shows the
/// provider in a window modal to the app's window, with the app's rights.
/// </summary>
/// <remarks>
/// The provider's component receives three parameters: <c>Dialog</c> (one of these names),
/// <c>Request</c> (the matching request record) and <c>Completed</c>
/// (<see cref="EventCallback{TValue}"/> of <see cref="object"/>, invoked once with the matching result
/// record, or <see langword="null"/> for a cancellation).
/// </remarks>
public static class KazoFileDialogs
{
    /// <summary>"Open with": every installed window app, and "Always use this app" (<see cref="OpenWithDialogRequest"/>).</summary>
    public const string OpenWith = "open-with";

    /// <summary>The properties of a file or folder (<see cref="FilePropertiesDialogRequest"/>).</summary>
    public const string Properties = "properties";

    /// <summary>"Replace / Keep both / Skip" when a name is taken (<see cref="FileConflictDialogRequest"/>).</summary>
    public const string Conflict = "conflict";
}

/// <summary>Configures the "Open with" dialog.</summary>
/// <param name="Path">The file or folder to open.</param>
public sealed record OpenWithDialogRequest(string Path)
{
    /// <summary>
    /// Gets the apps to choose from, by id, in display order; <see langword="null"/> lists every
    /// installed window app (the "Choose another app…" of the menu).
    /// </summary>
    public IReadOnlyList<string>? CandidateAppIds { get; init; }
}

/// <summary>Whether the user picked an app in the "Open with" dialog.</summary>
public enum OpenWithDialogStatus
{
    /// <summary>The user picked an app.</summary>
    Selected,

    /// <summary>The user closed the dialog.</summary>
    Cancelled,

    /// <summary>No provider of <c>kazoos.file-dialogs</c> could be shown.</summary>
    Unavailable
}

/// <summary>What the "Open with" dialog returns.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="AppId">The app picked, when <see cref="OpenWithDialogStatus.Selected"/>.</param>
/// <param name="AlwaysUse">
/// Whether the user ticked "Always use this app" for the file's extension (ADR 0121 §4): the caller
/// writes it with <c>KazoApps.SetDefaultHandlerAsync</c>.
/// </param>
public sealed record OpenWithDialogResult(OpenWithDialogStatus Status, string? AppId, bool AlwaysUse);

/// <summary>Configures the properties dialog.</summary>
/// <param name="Path">The file or folder described.</param>
public sealed record FilePropertiesDialogRequest(string Path);

/// <summary>Whether the properties dialog could be shown.</summary>
public enum FilePropertiesDialogStatus
{
    /// <summary>The dialog was shown and closed.</summary>
    Closed,

    /// <summary>No provider of <c>kazoos.file-dialogs</c> could be shown.</summary>
    Unavailable
}

/// <summary>What the properties dialog returns.</summary>
/// <param name="Status">The outcome.</param>
public sealed record FilePropertiesDialogResult(FilePropertiesDialogStatus Status);

/// <summary>Configures the name conflict dialog of a move or a copy.</summary>
/// <param name="SourcePath">The entry being moved.</param>
/// <param name="DestinationPath">The path already taken.</param>
/// <param name="SourceIsDirectory">Whether the source is a folder.</param>
/// <param name="DestinationIsDirectory">Whether what takes the name is a folder.</param>
public sealed record FileConflictDialogRequest(string SourcePath, string DestinationPath, bool SourceIsDirectory, bool DestinationIsDirectory)
{
    /// <summary>Gets whether more items follow, so "Apply to all" is offered.</summary>
    public bool OfferApplyToAll { get; init; } = true;
}

/// <summary>The answer to a name conflict.</summary>
public enum FileConflictDialogChoice
{
    /// <summary>Delete what is at the destination, then move the source there.</summary>
    Replace = 0,

    /// <summary>Move the source under a free <c>(copy)</c> name, keeping both.</summary>
    KeepBoth = 1,

    /// <summary>Leave this source where it is (also the answer when the dialog is closed or unavailable).</summary>
    Skip = 2
}

/// <summary>What the name conflict dialog returns.</summary>
/// <param name="Choice">What to do with this source.</param>
/// <param name="ApplyToAll">Whether the same answer goes for every later conflict of the operation.</param>
public sealed record FileConflictDialogResult(FileConflictDialogChoice Choice, bool ApplyToAll);
