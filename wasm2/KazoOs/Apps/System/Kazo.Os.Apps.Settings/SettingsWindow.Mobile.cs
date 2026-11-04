using Kazo.Os.AppSdk.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.Apps.Settings;

public partial class SettingsWindow
{
    private KazoSettingsDocument? _mobilePreferences;
    private bool _mobileMouseHelpers = true;
    private bool _savingMobile;
    private string? _mobileMessage;
    private Task? _mobileWrite;

    private async Task OpenMobilePreferencesAsync()
    {
        try
        {
            _mobilePreferences = await KazoSettings.OpenSystemAsync(AppContext, "mobile", AppContext.CancellationToken);
            _mobilePreferences.Changed += MobilePreferencesChanged;
            _mobileMouseHelpers = _mobilePreferences.Get<bool>("mouseHelpers");
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            _mobileMessage = L["Mobile.HelpersError"];
        }
    }

    private void MobilePreferencesChanged(object? sender, KazoSettingsChangedEventArgs args) => _ = InvokeAsync(() =>
    {
        if (_mobilePreferences is not null && !_savingMobile) _mobileMouseHelpers = _mobilePreferences.Get<bool>("mouseHelpers");
        StateHasChanged();
    });

    private async Task ChangeMobileMouseHelpersAsync(ChangeEventArgs args)
    {
        if (_savingMobile || _mobilePreferences is null || args.Value is not bool enabled) return;
        _savingMobile = true;
        _mobileMessage = null;
        TaskCompletionSource written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _mobileWrite = written.Task;
        try
        {
            // Preserve the same target layer and permission/administrator flow as Appearance.
            await _mobilePreferences.SetAsync("mouseHelpers", enabled, _targetLayer, CancellationToken.None);
            _mobileMouseHelpers = _mobilePreferences.Get<bool>("mouseHelpers");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _mobileMessage = L["Mobile.HelpersError"];
        }
        finally { _savingMobile = false; written.TrySetResult(); _mobileWrite = null; }
    }
}
