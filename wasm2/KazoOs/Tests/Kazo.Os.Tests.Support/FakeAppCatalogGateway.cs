using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;

namespace Kazo.Os.Tests.Support;

/// <summary>
/// In-memory <see cref="IAppCatalogGateway"/> double: a fixed list of apps and the configured
/// default handlers (ADR 0121), with the user's choices recorded in <see cref="UserDefaults"/>.
/// </summary>
public sealed class FakeAppCatalogGateway : IAppCatalogGateway
{
    private readonly List<KazoAppInfo> _apps = [];
    private readonly List<KazoFileAssociation> _systemDefaults = [];

    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    /// <summary>The user's choices, in the order they were made (a reset removes the row).</summary>
    public List<KazoFileAssociation> UserDefaults { get; } = [];

    /// <summary>The next result of <see cref="SetUserDefaultHandlerAsync"/>, when a test forces one.</summary>
    public KazoDefaultHandlerStatus? NextSetResult { get; set; }

    /// <summary>Adds an installed app.</summary>
    public FakeAppCatalogGateway WithApp(KazoAppInfo app)
    {
        _apps.Add(app);
        return this;
    }

    /// <summary>Adds a default of <c>/etc/kazoos/file-associations.json</c>.</summary>
    public FakeAppCatalogGateway WithSystemDefault(string target, string appId, string action = "open")
    {
        _systemDefaults.Add(new KazoFileAssociation(target, action, appId, KazoFileAssociationLayer.System));
        return this;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<KazoAppInfo>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<KazoAppInfo>>([.. _apps]);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<KazoAppInfo>> GetHandlersAsync(
        VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default)
    {
        string extension = Path.GetExtension(path.Value).ToLowerInvariant();
        return ValueTask.FromResult<IReadOnlyList<KazoAppInfo>>([.. _apps.Where(app => app.FileHandlers.Any(handler =>
            handler.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)))]);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<KazoFileAssociation>> GetDefaultHandlersAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<KazoFileAssociation>>([.. UserDefaults, .. _systemDefaults]);

    /// <inheritdoc />
    public ValueTask<KazoDefaultHandlerStatus> SetUserDefaultHandlerAsync(
        string target, string action, string? appId, CancellationToken cancellationToken = default)
    {
        if (NextSetResult is { } forced)
        {
            return ValueTask.FromResult(forced);
        }

        string normalized = target.ToLowerInvariant();
        if (!normalized.StartsWith('.') && !normalized.Contains('/'))
        {
            return ValueTask.FromResult(KazoDefaultHandlerStatus.InvalidTarget);
        }

        if (appId is not null)
        {
            KazoAppInfo? app = _apps.FirstOrDefault(candidate => candidate.Id == appId);
            if (app is null)
            {
                return ValueTask.FromResult(KazoDefaultHandlerStatus.AppNotFound);
            }

            bool declares = app.FileHandlers.Any(handler =>
                (handler.Extensions.Contains(normalized, StringComparer.OrdinalIgnoreCase)
                    || string.Equals(handler.MediaType, normalized, StringComparison.OrdinalIgnoreCase))
                && handler.Actions.Contains(action, StringComparer.OrdinalIgnoreCase));
            if (!declares)
            {
                return ValueTask.FromResult(KazoDefaultHandlerStatus.AppCannotHandle);
            }
        }

        UserDefaults.RemoveAll(row => row.Target == normalized && row.Action == action);
        if (appId is not null)
        {
            UserDefaults.Add(new KazoFileAssociation(normalized, action, appId, KazoFileAssociationLayer.User));
        }

        return ValueTask.FromResult(KazoDefaultHandlerStatus.Changed);
    }

    /// <summary>A window app that declares <paramref name="handlers"/>, for tests.</summary>
    public static KazoAppInfo Window(string id, string name, params KazoAppFileHandler[] handlers) =>
        new(id, name, name, AppKind.Window, "test", null, "1.0.0", "pub.test", false, false, handlers);
}
