using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;

namespace Kazo.Os.Tests.Support;

/// <summary>
/// In-memory <see cref="IAppCatalogGateway"/> double: a fixed list of apps and the configured
/// default handlers (ADR 0121), with the user's choices recorded in <see cref="UserDefaults"/>.
/// </summary>
public sealed class FakeAppCatalogGateway : IAppCatalogGateway
{
    private readonly List<HackAppInfo> _apps = [];
    private readonly List<HackFileAssociation> _systemDefaults = [];

    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    /// <summary>The user's choices, in the order they were made (a reset removes the row).</summary>
    public List<HackFileAssociation> UserDefaults { get; } = [];

    /// <summary>The next result of <see cref="SetUserDefaultHandlerAsync"/>, when a test forces one.</summary>
    public HackDefaultHandlerStatus? NextSetResult { get; set; }

    /// <summary>Adds an installed app.</summary>
    public FakeAppCatalogGateway WithApp(HackAppInfo app)
    {
        _apps.Add(app);
        return this;
    }

    /// <summary>Adds a default of <c>/etc/hackeros/file-associations.json</c>.</summary>
    public FakeAppCatalogGateway WithSystemDefault(string target, string appId, string action = "open")
    {
        _systemDefaults.Add(new HackFileAssociation(target, action, appId, HackFileAssociationLayer.System));
        return this;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HackAppInfo>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<HackAppInfo>>([.. _apps]);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HackAppInfo>> GetHandlersAsync(
        VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default)
    {
        string extension = Path.GetExtension(path.Value).ToLowerInvariant();
        return ValueTask.FromResult<IReadOnlyList<HackAppInfo>>([.. _apps.Where(app => app.FileHandlers.Any(handler =>
            handler.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)))]);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HackFileAssociation>> GetDefaultHandlersAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<HackFileAssociation>>([.. UserDefaults, .. _systemDefaults]);

    /// <inheritdoc />
    public ValueTask<HackDefaultHandlerStatus> SetUserDefaultHandlerAsync(
        string target, string action, string? appId, CancellationToken cancellationToken = default)
    {
        if (NextSetResult is { } forced)
        {
            return ValueTask.FromResult(forced);
        }

        string normalized = target.ToLowerInvariant();
        if (!normalized.StartsWith('.') && !normalized.Contains('/'))
        {
            return ValueTask.FromResult(HackDefaultHandlerStatus.InvalidTarget);
        }

        if (appId is not null)
        {
            HackAppInfo? app = _apps.FirstOrDefault(candidate => candidate.Id == appId);
            if (app is null)
            {
                return ValueTask.FromResult(HackDefaultHandlerStatus.AppNotFound);
            }

            bool declares = app.FileHandlers.Any(handler =>
                (handler.Extensions.Contains(normalized, StringComparer.OrdinalIgnoreCase)
                    || string.Equals(handler.MediaType, normalized, StringComparison.OrdinalIgnoreCase))
                && handler.Actions.Contains(action, StringComparer.OrdinalIgnoreCase));
            if (!declares)
            {
                return ValueTask.FromResult(HackDefaultHandlerStatus.AppCannotHandle);
            }
        }

        UserDefaults.RemoveAll(row => row.Target == normalized && row.Action == action);
        if (appId is not null)
        {
            UserDefaults.Add(new HackFileAssociation(normalized, action, appId, HackFileAssociationLayer.User));
        }

        return ValueTask.FromResult(HackDefaultHandlerStatus.Changed);
    }

    /// <summary>A window app that declares <paramref name="handlers"/>, for tests.</summary>
    public static HackAppInfo Window(string id, string name, params HackAppFileHandler[] handlers) =>
        new(id, name, name, AppKind.Window, "test", null, "1.0.0", "pub.test", false, false, handlers);
}
