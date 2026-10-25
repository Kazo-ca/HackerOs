using HackerOs.AppSdk;

namespace HackerOs.Apps.FileExplorer.FileControl;

/// <summary>
/// Controls what happens when the user activates (double-click, Enter, or the context menu's default
/// action on) a directory item in a <see cref="FileView"/>. See
/// <c>doc/wasm/dev/old/Global-FileView-And-MessagingSystem/FileViewControl.md#folder-double-click-behavior</c>.
/// </summary>
public enum FileViewFolderActivationMode
{
    /// <summary>Navigate this <see cref="FileView"/> instance into the folder. The default.</summary>
    Navigate,

    /// <summary>
    /// Let the OS-level file-association Shell decide, via
    /// <c>IAppIntentGateway.OpenFileAsync(path, mediaType: "inode/directory")</c> — never a hardcoded
    /// app ID.
    /// </summary>
    NewWindow,

    /// <summary>Invoke the host-supplied <c>OnCustomFolderActivate</c> callback instead.</summary>
    Custom
}
