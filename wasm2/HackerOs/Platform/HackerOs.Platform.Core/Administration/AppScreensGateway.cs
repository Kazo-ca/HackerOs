using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Administration;
using HackerOs.Platform.Core.Execution;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.Platform.Core.Administration;

/// <summary>Trusted <see cref="IAppScreensGateway"/> (ADR 0184) over <see cref="IScreenControl"/>.</summary>
/// <remarks>
/// Every call but <see cref="IsSupported"/> requires <c>screens.manage</c>. No administrator is
/// asked: the browser already confines each call to the user's own click, and nothing leaves the
/// machine. The calls that open a window or enter full screen run at once, so the click's gesture
/// still holds when they reach the browser.
/// </remarks>
internal sealed class AppScreensGateway : IAppScreensGateway
{
    private readonly Func<IScreenControl>? _control;
    private readonly ICapabilityChecker _capabilities;
    private readonly Guid _appInstanceId;
    private readonly AppAmbientContextBinding _ambient = new();

    /// <summary>Creates the gateway for one app instance.</summary>
    /// <param name="control">Lazily resolved screens; <see langword="null"/> makes the gateway unsupported.</param>
    /// <param name="capabilities">The calling instance's capabilities.</param>
    /// <param name="appInstanceId">The calling instance, owner of its windows.</param>
    public AppScreensGateway(Func<IScreenControl>? control, ICapabilityChecker capabilities, Guid appInstanceId)
    {
        _control = control;
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _appInstanceId = appInstanceId;
    }

    /// <inheritdoc />
    public bool IsSupported => _control?.Invoke().IsAvailable == true;

    /// <summary>Binds the app whose ambient scope change callbacks enter (ADR 0043).</summary>
    internal void BindAmbientContext(HackerOs.AppSdk.IAppExecutionContext context) => _ambient.Bind(context);

    /// <inheritdoc />
    public HackScreensState GetState() => Control().GetState(_appInstanceId);

    /// <inheritdoc />
    public ValueTask<IAsyncDisposable> WatchAsync(Func<ValueTask> onChanged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        IScreenControl control = Control();
        Action handler = _ambient.Wrap(() => _ = InvokeAsync(onChanged));
        control.Changed += handler;
        return ValueTask.FromResult<IAsyncDisposable>(new Subscription(() => control.Changed -= handler));
    }

    /// <inheritdoc />
    public async ValueTask<HackScreenOpenStatus> OpenScreenAsync(CancellationToken cancellationToken = default) =>
        await Control().OpenScreenAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<int> RestoreScreensAsync(CancellationToken cancellationToken = default) =>
        await Control().RestoreScreensAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask CloseScreenAsync(int number, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 2);
        await Control().CloseScreenAsync(number).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask IdentifyAsync(CancellationToken cancellationToken = default) =>
        await Control().IdentifyAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<bool> ToggleFullScreenAsync(CancellationToken cancellationToken = default) =>
        await Control().ToggleFullScreenAsync(_appInstanceId).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<HackScreenPermission> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
        await Control().RequestPermissionAsync().ConfigureAwait(false);

    private IScreenControl Control()
    {
        _capabilities.Require(AppCapabilities.ScreensManage);
        return _control?.Invoke() ?? throw new NotSupportedException("This host has no secondary screens.");
    }

    private static async Task InvokeAsync(Func<ValueTask> onChanged)
    {
        try
        {
            await onChanged().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or NotSupportedException)
        {
            // A failed refresh keeps the app's last state, as the settings watch does.
        }
    }

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
