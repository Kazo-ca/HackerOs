using System.Globalization;
using HackerOs.AppSdk.WhatsNew;
using HackerOs.Platform.Core.Execution;

namespace HackerOs.Platform.Core.WhatsNew;

/// <summary>
/// <see cref="IAppWhatsNewGateway"/> over <see cref="WhatsNewService"/> (ADR 0186): the change list of
/// the session user. No capability: every app may read it and mark it seen.
/// </summary>
internal sealed class AppWhatsNewGateway : IAppWhatsNewGateway
{
    private readonly Func<WhatsNewService> _service;
    private readonly AppAmbientContextBinding _ambient = new();

    public AppWhatsNewGateway(Func<WhatsNewService> service) =>
        _service = service ?? throw new ArgumentNullException(nameof(service));

    public bool IsSupported => true;

    internal void BindAmbientContext(HackerOs.AppSdk.IAppExecutionContext context) => _ambient.Bind(context);

    public ValueTask<HackChangeList> GetAsync(CultureInfo culture, CancellationToken cancellationToken = default) =>
        _service().GetAsync(culture, cancellationToken);

    public ValueTask MarkSeenAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default) =>
        _service().MarkSeenAsync(ids, cancellationToken);

    public ValueTask<IAsyncDisposable> WatchAsync(Func<ValueTask> onChanged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        WhatsNewService service = _service();
        Action handler = _ambient.Wrap(() => _ = InvokeAsync(onChanged));
        service.Changed += handler;
        return ValueTask.FromResult<IAsyncDisposable>(new Subscription(() => service.Changed -= handler));
    }

    private static async Task InvokeAsync(Func<ValueTask> onChanged)
    {
        try
        {
            await onChanged().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or NotSupportedException)
        {
            // A failed refresh keeps the app's last state, as the screens watch does.
        }
    }

    private sealed class Subscription(Action unsubscribe) : IAsyncDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
