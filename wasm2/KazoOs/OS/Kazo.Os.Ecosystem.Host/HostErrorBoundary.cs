using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Kazo.Os.Ecosystem;

/// <summary>Converts unhandled Blazor component failures into correlated host diagnostics.</summary>
public sealed class HostErrorBoundary : ErrorBoundary
{
    /// <summary>Gets the correlation ID assigned to the most recent unhandled failure.</summary>
    public Guid? CorrelationId { get; private set; }

    [Inject]
    private HostExceptionReporter Reporter { get; set; } = null!;

    /// <inheritdoc />
    protected override async Task OnErrorAsync(Exception exception)
    {
        CorrelationId = await Reporter.ReportAsync(exception, "component-render");
        Console.Error.WriteLine($"[HostErrorBoundary] Unhandled failure (CorrelationId: {CorrelationId}):\n{exception}");
    }
}