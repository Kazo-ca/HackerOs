using Microsoft.CodeAnalysis;

namespace Kazo.Os.Tools.WebRouteGenerator;

/// <summary>
/// Build errors of ASP.Kazo sites (ADR 0076 §3, ADR 0082). Every one is an error: a route that the
/// build cannot prove unambiguous or bindable never reaches the browser.
/// </summary>
internal static class WebDiagnostics
{
    private const string Category = "Kazo.Os.Web";

    /// <summary>Two attribute routes match the same URLs for a common method.</summary>
    public static readonly DiagnosticDescriptor RouteConflict = new(
        "KOSWEB0001",
        "Conflicting routes",
        "The route '{0}' of {1} conflicts with {2}: both match the same URLs for {3}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>A route template is malformed (syntax, unknown constraint, complex segment...).</summary>
    public static readonly DiagnosticDescriptor InvalidTemplate = new(
        "KOSWEB0002",
        "Invalid route template",
        "Invalid route template on {0}: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>One route name is used for different templates.</summary>
    public static readonly DiagnosticDescriptor DuplicateRouteName = new(
        "KOSWEB0003",
        "Duplicate route name",
        "The route name '{0}' is used by {1} ('{2}') and {3} ('{4}'); route names must be unique",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>An action parameter cannot be bound from a request.</summary>
    public static readonly DiagnosticDescriptor UnbindableParameter = new(
        "KOSWEB0004",
        "Parameter cannot be bound",
        "The parameter '{0}' of {1} cannot be bound: {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>A controller has no public constructor the generated code can call.</summary>
    public static readonly DiagnosticDescriptor ControllerNotCreatable = new(
        "KOSWEB0005",
        "Controller cannot be created",
        "The controller {0} cannot be created: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>A web app definition without entry point is not partial (or is nested).</summary>
    public static readonly DiagnosticDescriptor DefinitionNotPartial = new(
        "KOSWEB0006",
        "Web app definition needs an entry point",
        "The web app definition {0} must be a top-level partial class so the build can supply its entry point, or override EntryPoint itself",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>An action uses an attribute the generator cannot honour.</summary>
    public static readonly DiagnosticDescriptor UnsupportedAction = new(
        "KOSWEB0007",
        "Unsupported action declaration",
        "The action {0} cannot be routed: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>A <c>.razor</c> and a <c>.cshtml</c> view have the same path under <c>Views/</c> (ADR 0086 §4).</summary>
    public static readonly DiagnosticDescriptor DuplicateView = new(
        "KOSWEB0008",
        "Duplicate view",
        "The view 'Views/{0}' exists as both .razor and .cshtml; View() could not tell them apart: rename or remove one",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
