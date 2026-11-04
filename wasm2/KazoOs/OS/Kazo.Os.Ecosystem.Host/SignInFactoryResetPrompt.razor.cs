using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// The sign-in screen's « Forgot password? » link and its confirmation (ADR 0226). The component
/// only asks; the host (<c>App.razor</c>) erases the machine through <see cref="SignInFactoryReset"/>
/// once <see cref="OnConfirmed"/> is raised.
/// </summary>
public sealed partial class SignInFactoryResetPrompt
{
    private ElementReference _wordInput;
    private bool _focusWordInput;

    /// <summary>Raised once the user typed the confirmation word and pressed the reset button.</summary>
    [Parameter]
    public EventCallback OnConfirmed { get; set; }

    /// <summary>Disables the link while the sign-in form is busy.</summary>
    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>Opens the confirmation at first render (tests, and a host that wants it open).</summary>
    [Parameter]
    public bool InitiallyOpen { get; set; }

    private bool IsOpen { get; set; }

    private bool IsResetting { get; set; }

    private string TypedWord { get; set; } = string.Empty;

    /// <summary>The word to type, in the sign-in screen's language: ERASE, or EFFACER in French.</summary>
    private string ConfirmationWord => L["FactoryReset.Word"];

    private bool IsConfirmed => IsConfirmationTyped(TypedWord, ConfirmationWord);

    /// <summary>
    /// Whether <paramref name="typed"/> is the confirmation word: surrounding spaces and letter case
    /// do not count, any other difference does (ADR 0226, question 3).
    /// </summary>
    /// <param name="typed">What the user typed.</param>
    /// <param name="word">The confirmation word of the current language.</param>
    /// <returns><see langword="true"/> when the reset may go ahead.</returns>
    public static bool IsConfirmationTyped(string? typed, string word) =>
        !string.IsNullOrWhiteSpace(word)
        && string.Compare(typed?.Trim(), word.Trim(), CultureInfo.CurrentCulture, CompareOptions.IgnoreCase) == 0;

    /// <inheritdoc />
    protected override void OnInitialized() => IsOpen = InitiallyOpen;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_focusWordInput)
        {
            return;
        }

        // The dialog is modal: the keyboard goes to its only field, so Enter on the sign-in form
        // behind it never signs in by accident.
        _focusWordInput = false;
        await _wordInput.FocusAsync();
    }

    private void Open()
    {
        TypedWord = string.Empty;
        IsOpen = true;
        _focusWordInput = true;
    }

    private void Close()
    {
        TypedWord = string.Empty;
        IsOpen = false;
    }

    private void OnKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape" && !IsResetting)
        {
            Close();
        }
    }

    private async Task ConfirmAsync()
    {
        if (!IsConfirmed || IsResetting)
        {
            return;
        }

        IsResetting = true;
        try
        {
            await OnConfirmed.InvokeAsync();
        }
        finally
        {
            // Reached only when no device could restart the machine: the dialog closes on the
            // sign-in screen the host shows again.
            IsResetting = false;
            Close();
        }
    }
}
