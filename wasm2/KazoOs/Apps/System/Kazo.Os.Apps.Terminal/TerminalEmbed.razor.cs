using Kazo.Os.AppSdk.Components;
using Kazo.Os.AppSdk.Terminal;

namespace Kazo.Os.Apps.Terminal;

/// <summary>
/// Embeddable terminal of the Terminal app (contract <see cref="KazoComponentContracts.Terminal"/>,
/// ADR 0063). It shows the same <see cref="TerminalPanel"/> as the Terminal window and hands it to
/// the host as its <see cref="IKazoTerminal"/> once mounted.
/// </summary>
public partial class TerminalEmbed
{
    private TerminalPanel? _panel;
    private bool _handedOver;

    /// <summary>Gets the host's options, or the defaults when it passed none.</summary>
    private KazoTerminalOptions TerminalOptions => Options as KazoTerminalOptions ?? new KazoTerminalOptions();

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The panel exists after the first render; the host gets it once.
        if (!_handedOver && _panel is not null)
        {
            _handedOver = true;
            await ControllerReady.InvokeAsync(_panel);
        }
    }
}
