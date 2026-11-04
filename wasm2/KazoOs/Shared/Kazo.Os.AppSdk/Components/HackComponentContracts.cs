namespace Kazo.Os.AppSdk.Components;

/// <summary>
/// Names of the embeddable component contracts the OS knows (ADR 0063). An app registers a
/// component for a contract in its manifest (<c>AppDefinition.Components</c>); another app embeds
/// it through <c>HackEmbeddedComponent</c> without referencing the provider's assembly, as a KDE
/// app embeds Konsole's KPart.
/// </summary>
/// <remarks>
/// A contract name is stable: it names an interface of this SDK that the embedded component hands
/// back to its host (the controller), and the parameters the host passes to it.
/// </remarks>
public static class HackComponentContracts
{
    /// <summary>
    /// A terminal emulator running its own <c>hsh</c>, controlled through <see cref="IHackTerminal"/>
    /// and configured with <see cref="HackTerminalOptions"/>. Provided by the Terminal app.
    /// </summary>
    public const string Terminal = "hackeros.terminal";

    /// <summary>
    /// The shared file view of the OS (ADR 0148). Reserved: only a system app (the File Explorer)
    /// may provide it; another app's registration is ignored by the OS and fails the host build.
    /// </summary>
    public const string FileView = Kazo.Os.App.Abstractions.AppComponentContracts.FileView;

    /// <summary>
    /// The shared file dialogs of the OS: Open with, Properties, name conflicts and the file pickers
    /// (ADR 0148). Reserved to system apps, like <see cref="FileView"/>.
    /// </summary>
    public const string FileDialogs = Kazo.Os.App.Abstractions.AppComponentContracts.FileDialogs;

    /// <summary>
    /// The OS context menu (ADR 0150). Provided by the platform itself: no app may register it.
    /// </summary>
    public const string ContextMenu = Kazo.Os.App.Abstractions.AppComponentContracts.ContextMenu;
}
