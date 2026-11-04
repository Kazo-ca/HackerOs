using Kazo.Os.AppSdk.Settings;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Administration;

/// <summary>
/// Trusted <see cref="IAppSystemSettingsGateway"/> (ADR 0056, section 8) over
/// <see cref="IOsSettingsService"/>.
/// </summary>
/// <remarks>
/// <para>
/// No capability is checked: the files' permissions decide (decision D16). The OS reads both
/// layers (everybody may read them) and writes the user layer as the session user, so an app
/// without <c>filesystem.user-home.write</c> can still change the user's own
/// <c>~/.config/hackeros</c> preferences, as any process of that user could.
/// </para>
/// <para>
/// The system layer is written with the calling app's own filesystem gateway: <c>/etc/hackeros</c>
/// belongs to root, so the write needs <c>sudo</c> or the administrator approval of ADR 0042, and
/// is otherwise refused with <see cref="UnauthorizedAccessException"/>. Writing through
/// <see cref="IOsSettingsService"/> also raises its change notification, so the platform (for
/// instance <c>ThemePreferenceService</c>) applies the new value at once.
/// </para>
/// </remarks>
internal sealed class AppSystemSettingsGateway : IAppSystemSettingsGateway
{
    private readonly Func<IOsSettingsService> _settings;
    private readonly IAppFileSystemGateway _callerFileSystem;
    private readonly AppAmbientContextBinding _ambient = new();

    /// <summary>Creates the gateway for one app instance.</summary>
    /// <param name="settings">Lazily resolved OS settings service.</param>
    /// <param name="callerFileSystem">The calling app's own filesystem gateway, used for system-layer writes.</param>
    public AppSystemSettingsGateway(Func<IOsSettingsService> settings, IAppFileSystemGateway callerFileSystem)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _callerFileSystem = callerFileSystem ?? throw new ArgumentNullException(nameof(callerFileSystem));
    }

    /// <summary>Binds the app whose ambient scope change callbacks enter (ADR 0043).</summary>
    internal void BindAmbientContext(Kazo.Os.AppSdk.IAppExecutionContext context) => _ambient.Bind(context);

    /// <inheritdoc />
    public AppSystemSettingsDocumentInfo Describe(string documentId)
    {
        OsSettingsDocument document = Require(documentId);
        string? home = _settings().CurrentHomeDirectory;
        return new AppSystemSettingsDocumentInfo(
            document.DocumentId,
            document.Schema,
            document.SystemPath.Value,
            document.SystemOnly || home is null ? string.Empty : document.GetUserPath(home).Value,
            document.SystemOnly);
    }

    /// <inheritdoc />
    public ValueTask<LayeredConfigSnapshot> ReadAsync(string documentId, CancellationToken cancellationToken = default) =>
        _settings().ReadAsync(Require(documentId), cancellationToken);

    /// <inheritdoc />
    public ValueTask SetAsync(
        string documentId,
        IReadOnlyList<KeyValuePair<string, string>> values,
        SettingLayer target,
        CancellationToken cancellationToken = default) =>
        _settings().SetManyAsync(Require(documentId), values, target, WriterFor(target), cancellationToken);

    /// <inheritdoc />
    public ValueTask ResetAsync(string documentId, string qualifiedKey, SettingLayer target, CancellationToken cancellationToken = default) =>
        _settings().ResetAsync(Require(documentId), qualifiedKey, target, WriterFor(target), cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable> WatchAsync(
        string documentId, Func<ValueTask> onChanged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        OsSettingsDocument document = Require(documentId);
        IOsSettingsService settings = _settings();
        await settings.WatchAsync(document, cancellationToken).ConfigureAwait(false);

        void Handler(object? sender, OsSettingsChangedEventArgs args)
        {
            if (args.Concerns(document.DocumentId))
            {
                _ambient.Wrap(() => _ = InvokeAsync(onChanged))();
            }
        }

        settings.DocumentChanged += Handler;
        return new Subscription(() => settings.DocumentChanged -= Handler);
    }

    private static async Task InvokeAsync(Func<ValueTask> onChanged)
    {
        try
        {
            await onChanged().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            // A reload failure keeps the document's last good values, as the app path does.
        }
    }

    /// <summary>The system layer uses the app's own rights; the user layer the session user's (null writer).</summary>
    private IAppFileSystemGateway? WriterFor(SettingLayer target) => target == SettingLayer.System ? _callerFileSystem : null;

    private static OsSettingsDocument Require(string documentId) =>
        OsSettingsDocumentRegistry.TryGet(documentId, out OsSettingsDocument document)
            ? document
            : throw new ArgumentException(
                $"The OS declares no settings document '{documentId}' (known: {string.Join(", ", OsSettingsDocumentRegistry.DocumentIds)}).",
                nameof(documentId));

    private sealed class Subscription(Action dispose) : IAsyncDisposable
    {
        private Action? _dispose = dispose;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
