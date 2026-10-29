using HackerOs.Platform.Core.ServerConnection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// The server connection icon of the taskbar's notification area (ADR 0134 §2). Starting it starts
/// the automatic connection of <see cref="ServerPresenceService"/> for the desktop session; disposing
/// it (logout) stops it. The OS never waits on it.
/// </summary>
public sealed partial class ServerStatusIndicator
{
    private ServerPresence _presence = ServerPresence.NotConfigured;
    private ServerPresenceService? _service;
    private SyncScheduler? _scheduler;
    private DateTimeOffset? _lastSync;
    private bool _isOpen;
    private bool _isIdentifying;
    private bool _isBusy;
    private string _login = string.Empty;
    private string _password = string.Empty;
    private string? _error;

    [Inject]
    private IServiceProvider Services { get; set; } = null!;

    [Inject]
    private IStringLocalizer<Strings> L { get; set; } = null!;

    /// <summary>Converts a time to the session's time zone (ADR 0193), or the device's when the host has none.</summary>
    private DateTimeOffset LocalTime(DateTimeOffset time) =>
        TimeZoneInfo.ConvertTime(time, Services.GetService<HackerOs.Platform.Core.Globalization.SessionTimeZoneService>()?.TimeZone ?? TimeZoneInfo.Local);

    /// <summary>Opens Settings at its server page ("Server settings" in the panel). The button is hidden without it.</summary>
    [Parameter]
    public EventCallback OnOpenSettings { get; set; }

    private bool CanIdentify => !_isBusy && !string.IsNullOrWhiteSpace(_login) && !string.IsNullOrEmpty(_password);

    private string StatusClass => _presence.Status switch
    {
        ServerPresenceStatus.Connected => "is-connected",
        ServerPresenceStatus.ConnectedAnonymous => "is-anonymous",
        ServerPresenceStatus.Connecting => "is-connecting",
        ServerPresenceStatus.Offline => "is-offline",
        _ => "is-error",
    };

    private string IconName => _presence.Status switch
    {
        ServerPresenceStatus.Connected => "cloud-check",
        ServerPresenceStatus.ConnectedAnonymous => "cloud",
        ServerPresenceStatus.Connecting => "loader-circle",
        ServerPresenceStatus.Offline => "cloud-off",
        _ => "cloud-alert",
    };

    private string StatusText => _presence.Status switch
    {
        ServerPresenceStatus.Connected => L["Server.Status.Connected"],
        ServerPresenceStatus.ConnectedAnonymous => L["Server.Status.Anonymous"],
        ServerPresenceStatus.Connecting => L["Server.Status.Connecting"],
        ServerPresenceStatus.Offline => L["Server.Status.Offline"],
        _ => L["Server.Status.Error"],
    };

    private string Tooltip => L["Server.Tooltip", _presence.ServerUrl?.ToString() ?? string.Empty, StatusText];

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        // Resolved optionally: a host composed without the server plumbing simply shows nothing.
        _service = Services.GetService<ServerPresenceService>();
        _scheduler = Services.GetService<SyncScheduler>();
        if (_service is null)
        {
            return;
        }

        _presence = _service.Current;
        _service.Changed += OnPresenceChanged;
        if (_scheduler is not null)
        {
            _scheduler.StatusChanged += OnSyncChanged;
            _lastSync = _scheduler.Status.LastPassAtUtc;
        }

        _ = StartQuietlyAsync(_service);
    }

    private static async Task StartQuietlyAsync(ServerPresenceService service)
    {
        try
        {
            await service.StartAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Logout while the first check ran.
        }
    }

    private void OnPresenceChanged()
    {
        _ = InvokeAsync(() =>
        {
            _presence = _service!.Current;
            StateHasChanged();
        });
    }

    private void OnSyncChanged(object? sender, EventArgs e)
    {
        _ = InvokeAsync(() =>
        {
            _lastSync = _scheduler!.Status.LastPassAtUtc;
            StateHasChanged();
        });
    }

    private void TogglePanel()
    {
        _isOpen = !_isOpen;
        _isIdentifying = false;
        _error = null;
    }

    private void HandleKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            _isOpen = false;
            _isIdentifying = false;
        }
    }

    private void StartIdentify()
    {
        _isIdentifying = true;
        _error = null;
    }

    private async Task IdentifyAsync(bool createAccount)
    {
        if (!CanIdentify || _service is null)
        {
            return;
        }

        _isBusy = true;
        _error = null;
        try
        {
            await _service.IdentifyAsync(_login.Trim(), _password, createAccount);
            _password = string.Empty;
            _isIdentifying = false;
        }
        catch (Exception exception) when (exception is ServerConnectionException or HttpRequestException or InvalidOperationException)
        {
            _error = L["Server.IdentifyFailed", exception.Message];
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task ReconnectAsync()
    {
        if (_service is null)
        {
            return;
        }

        _isBusy = true;
        try
        {
            await _service.ReconnectAsync();
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task OpenSettingsAsync()
    {
        _isOpen = false;
        await OnOpenSettings.InvokeAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_scheduler is not null)
        {
            _scheduler.StatusChanged -= OnSyncChanged;
        }

        if (_service is not null)
        {
            _service.Changed -= OnPresenceChanged;
            await _service.StopAsync();
        }
    }
}
