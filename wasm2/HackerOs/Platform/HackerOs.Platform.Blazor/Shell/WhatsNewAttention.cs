using HackerOs.Platform.Core.WhatsNew;
using Microsoft.Extensions.DependencyInjection;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// What the desktop and mobile shells show of the change list (ADR 0186 §4.5): a dot on the Start
/// button (the Home button on a phone) while the session user has unseen changes, and the sign-in
/// notification, posted once per account by <see cref="WhatsNewService"/>. Does nothing on a host
/// that serves no change list.
/// </summary>
internal sealed class WhatsNewAttention : IDisposable
{
    private readonly WhatsNewService? _service;
    private readonly Action<bool> _apply;

    /// <summary>Creates the binding.</summary>
    /// <param name="services">The shell's services; <see cref="WhatsNewService"/> may be missing.</param>
    /// <param name="apply">Shows or hides the dot; may be called off the renderer's context.</param>
    public WhatsNewAttention(IServiceProvider services, Action<bool> apply)
    {
        ArgumentNullException.ThrowIfNull(services);
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _service = services.GetService<WhatsNewService>();
        if (_service is not null)
        {
            _service.Changed += OnChanged;
        }
    }

    /// <summary>Posts the sign-in notification when there are unseen changes, then shows the dot.</summary>
    public async Task StartAsync()
    {
        if (_service is null)
        {
            return;
        }

        try
        {
            await _service.NotifyNewChangesAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // No notification: the dot and the Settings page still say it.
        }

        await RefreshAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_service is not null)
        {
            _service.Changed -= OnChanged;
        }
    }

    private void OnChanged() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            _apply(await _service!.GetNewCountAsync() > 0);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or OperationCanceledException)
        {
            // Keep the last state.
        }
    }
}
