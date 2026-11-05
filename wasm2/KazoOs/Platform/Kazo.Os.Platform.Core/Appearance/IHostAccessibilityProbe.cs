namespace Kazo.Os.Platform.Core.Appearance;

/// <summary>
/// Reads the host's own accessibility settings (ADR 0105 section 1), which an <c>auto</c> value of
/// <c>accessibility.config</c> follows. The browser host answers from media queries; a host with no
/// browser (tests, prerendering) answers <see cref="HostAccessibilitySignals.None"/>.
/// </summary>
public interface IHostAccessibilityProbe
{
    /// <summary>Gets the last signals read; <see cref="HostAccessibilitySignals.None"/> until <see cref="InitializeAsync"/> ran.</summary>
    HostAccessibilitySignals Current { get; }

    /// <summary>Raised when the host changed one of its settings.</summary>
    event Action? Changed;

    /// <summary>Reads the signals and starts watching them. Never throws for a host without a browser.</summary>
    /// <param name="cancellationToken">Cancels the first read.</param>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <summary>A host that never asks for anything: tests, prerendering, and hosts with no browser.</summary>
public sealed class NoHostAccessibilityProbe : IHostAccessibilityProbe
{
    /// <inheritdoc />
    public HostAccessibilitySignals Current => HostAccessibilitySignals.None;

    /// <inheritdoc />
    public event Action? Changed
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
