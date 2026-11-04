namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Who may provide a component contract and how the provider is chosen (ADR 0148 §3.3, ADR 0156).
/// Most contracts are open to any app, like <c>hackeros.terminal</c> (ADR 0063). The file view and
/// the file dialogs are open too, but an administrator chooses which app provides them for the
/// whole machine. The context menu stays the platform's own.
/// </summary>
public enum AppComponentProviderScope
{
    /// <summary>Any installed window app may register a component for the contract.</summary>
    AnyApp,

    /// <summary>
    /// Any window app may register it (ADR 0156 §1), but the OS uses the one an administrator chose
    /// in <c>/etc/hackeros/components.config</c>, the File Explorer when none is chosen, and falls
    /// back to the next provider, with a <c>component.fallback</c> warning, when the chosen one does
    /// not load. These are the OS's own controls: no working provider at all is an OS error.
    /// </summary>
    AdministratorSelected,

    /// <summary>The platform provides the contract itself, without a manifest; no app may register it.</summary>
    Platform
}

/// <summary>
/// The component contracts the manifest validator and the OS registry know by name (ADR 0148), with
/// who may provide them. The SDK repeats these names in <c>KazoComponentContracts</c>, next to the
/// contracts' controllers.
/// </summary>
public static class AppComponentContracts
{
    /// <summary>The shared file view (<c>IHackFileView</c>), provided by the File Explorer unless an administrator chose another app (ADR 0156).</summary>
    public const string FileView = "hackeros.file-view";

    /// <summary>The shared file dialogs (Open with, Properties, name conflicts, file pickers), provided by the File Explorer unless an administrator chose another app (ADR 0156).</summary>
    public const string FileDialogs = "hackeros.file-dialogs";

    /// <summary>The OS context menu (<c>IHackContextMenu</c>, ADR 0150), provided by the platform itself.</summary>
    public const string ContextMenu = "hackeros.context-menu";

    /// <summary>Gets who may provide <paramref name="contract"/> (ordinal comparison).</summary>
    /// <param name="contract">Contract name.</param>
    /// <returns><see cref="AppComponentProviderScope.AnyApp"/> for every contract ADR 0148 and ADR 0156 do not name.</returns>
    public static AppComponentProviderScope GetProviderScope(string contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return contract switch
        {
            FileView or FileDialogs => AppComponentProviderScope.AdministratorSelected,
            ContextMenu => AppComponentProviderScope.Platform,
            _ => AppComponentProviderScope.AnyApp
        };
    }
}
