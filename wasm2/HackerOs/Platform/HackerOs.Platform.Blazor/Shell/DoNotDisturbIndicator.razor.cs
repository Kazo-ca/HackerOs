using HackerOs.Platform.Core.Notifications;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// The "Do not disturb" icon of the taskbar's notification area (ADR 0216 §5): a moon while the
/// mode is on, nothing otherwise. Hosts without <see cref="DoNotDisturbService"/> never show it.
/// </summary>
public partial class DoNotDisturbIndicator : ComponentBase, IDisposable
{
    private DoNotDisturbService? _service;

    /// <summary>Gets or sets what a click does: the desktop opens the clock panel, which holds the switch.</summary>
    [Parameter]
    public EventCallback OnClick { get; set; }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _service = Services.GetService<DoNotDisturbService>();
        if (_service is not null)
        {
            _service.Changed += OnChanged;
        }
    }

    private void OnChanged() => InvokeAsync(StateHasChanged);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_service is not null)
        {
            _service.Changed -= OnChanged;
        }
    }
}
