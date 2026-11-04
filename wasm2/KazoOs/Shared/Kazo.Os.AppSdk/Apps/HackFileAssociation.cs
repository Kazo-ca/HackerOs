namespace Kazo.Os.AppSdk.Apps;

/// <summary>Which file names a default handler (ADR 0121, section 1).</summary>
public enum HackFileAssociationLayer
{
    /// <summary><c>/etc/hackeros/file-associations.json</c>: chosen by an administrator for the whole machine.</summary>
    System = 0,

    /// <summary><c>~/.config/hackeros/file-associations.json</c>: the user's own choice, which comes first.</summary>
    User = 1
}

/// <summary>
/// One configured default handler (ADR 0121, section 3), like a line of the
/// <c>[Default Applications]</c> section of freedesktop's <c>mimeapps.list</c>.
/// </summary>
/// <param name="Target">Extension with its dot (<c>.md</c>) or media type (<c>text/markdown</c>, <c>x-scheme-handler/https</c>), lowercase.</param>
/// <param name="Action"><c>open</c> or <c>edit</c>.</param>
/// <param name="AppId">Default app.</param>
/// <param name="Layer">File that names it.</param>
public sealed record HackFileAssociation(string Target, string Action, string AppId, HackFileAssociationLayer Layer);

/// <summary>How a change of the user's default handler ended (ADR 0121, section 3).</summary>
public enum HackDefaultHandlerStatus
{
    /// <summary>The user's <c>~/.config/hackeros/file-associations.json</c> now holds the change.</summary>
    Changed = 0,

    /// <summary>The target is neither an extension (<c>.md</c>) nor a media type (<c>type/subtype</c>).</summary>
    InvalidTarget = 1,

    /// <summary>The action is neither <c>open</c> nor <c>edit</c>.</summary>
    InvalidAction = 2,

    /// <summary>No installed window app has this ID.</summary>
    AppNotFound = 3,

    /// <summary>The app does not declare this file type or scheme for this action.</summary>
    AppCannotHandle = 4,

    /// <summary>The user's file is not a valid association document; it was left untouched.</summary>
    FileInvalid = 5,

    /// <summary>The file could not be written (permissions of the home).</summary>
    WriteFailed = 6
}
