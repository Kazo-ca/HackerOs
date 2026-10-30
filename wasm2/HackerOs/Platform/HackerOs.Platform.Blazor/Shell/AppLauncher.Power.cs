using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// The Power button of the Start menu (ADR 0195): « Se déconnecter », « Changer d'utilisateur »
/// (ADR 0201, never for the guest) and « Redémarrer ». The desktop shell carries out the request;
/// the menu only closes the launcher and raises it.
/// </summary>
public partial class AppLauncher
{
    private bool _isPowerMenuOpen;
    private bool _focusPowerMenu;
    private ElementReference _powerTriggerRef;
    private ElementReference _signOutItemRef;
    private ElementReference _restartItemRef;
    private ElementReference _switchUserItemRef;

    /// <summary>
    /// Raised by « Se déconnecter » (ADR 0195): the desktop shell confirms and ends the session.
    /// No Power button is shown when neither this nor <see cref="OnRestart"/> is supplied.
    /// </summary>
    [Parameter]
    public EventCallback OnSignOut { get; set; }

    /// <summary>
    /// Raised by « Redémarrer » (ADR 0195): the desktop shell ends the session as for a sign-out,
    /// then the host reloads the OS past its caches.
    /// </summary>
    [Parameter]
    public EventCallback OnRestart { get; set; }

    /// <summary>
    /// Raised by « Changer d'utilisateur » (ADR 0201): the host moves the session to the background
    /// and opens the sign-in screen. The item stays greyed out when it is not supplied.
    /// </summary>
    [Parameter]
    public EventCallback OnSwitchUser { get; set; }

    private bool HasPowerMenu => OnSignOut.HasDelegate || OnRestart.HasDelegate;

    private bool IsGuest => _principal?.LoginName.Value == HackerOs.Platform.Core.Sessions.GuestAccountService.LoginName;

    /// <summary>The guest never switches user (ADR 0195 §4, ADR 0201 §4); the others do when the host supports it.</summary>
    private bool CanSwitchUser => OnSwitchUser.HasDelegate && !IsGuest;

    private string SwitchUserHint => IsGuest
        ? L["Launcher.Power.SwitchUser.Guest"]
        : L["Launcher.Power.SwitchUser.Unavailable"];

    private void TogglePowerMenu()
    {
        _isPowerMenuOpen = !_isPowerMenuOpen;
        _focusPowerMenu = _isPowerMenuOpen;
    }

    private async Task SignOutAsync()
    {
        await CloseAsync();
        await OnSignOut.InvokeAsync();
    }

    private async Task SwitchUserAsync()
    {
        await CloseAsync();
        await OnSwitchUser.InvokeAsync();
    }

    private async Task RestartAsync()
    {
        await CloseAsync();
        await OnRestart.InvokeAsync();
    }

    /// <summary>Closes the Power menu and gives the focus back to its button.</summary>
    private async Task ClosePowerMenuAsync()
    {
        _isPowerMenuOpen = false;
        await TryFocusAsync(_powerTriggerRef);
    }

    private async Task HandlePowerKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            // The first Escape closes the Power menu, the next one the Start menu.
            await (_isPowerMenuOpen ? ClosePowerMenuAsync() : CloseAsync());
            return;
        }

        if (!_isPowerMenuOpen || args.Key is not ("ArrowDown" or "ArrowUp" or "Home" or "End"))
        {
            return;
        }

        // Only the enabled items take the focus; a greyed « Changer d'utilisateur » is skipped.
        List<ElementReference> items = [];
        if (OnSignOut.HasDelegate)
        {
            items.Add(_signOutItemRef);
        }

        if (CanSwitchUser)
        {
            items.Add(_switchUserItemRef);
        }

        if (OnRestart.HasDelegate)
        {
            items.Add(_restartItemRef);
        }

        _powerItemIndex = args.Key switch
        {
            "Home" => 0,
            "End" => items.Count - 1,
            "ArrowDown" => (_powerItemIndex + 1) % items.Count,
            _ => (_powerItemIndex - 1 + items.Count) % items.Count,
        };
        await TryFocusAsync(items[_powerItemIndex]);
    }

    private int _powerItemIndex;

    private async Task FocusPowerMenuIfRequestedAsync()
    {
        if (!_focusPowerMenu)
        {
            return;
        }

        _focusPowerMenu = false;
        _powerItemIndex = 0;
        await TryFocusAsync(OnSignOut.HasDelegate ? _signOutItemRef : CanSwitchUser ? _switchUserItemRef : _restartItemRef);
    }

    private static async Task TryFocusAsync(ElementReference element)
    {
        try
        {
            await element.FocusAsync();
        }
        catch (InvalidOperationException)
        {
            // Headless component renderers do not provide a browser focus implementation.
        }
    }
}
