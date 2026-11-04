using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Routing;

namespace Kazo.Os.AppSdk.Web.Mvc;

/// <summary>
/// The context a result executes in (ASP.NET's <c>ActionContext</c>): the request, its route values,
/// the action (none for a minimal route) and the model state.
/// </summary>
public class HackActionContext
{
    /// <summary>Initializes a context.</summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="actionDescriptor">The controller action, or <see langword="null"/> for a minimal route.</param>
    /// <param name="modelState">The model state; a new one when <see langword="null"/>.</param>
    public HackActionContext(HackHttpContext httpContext, HackControllerActionDescriptor? actionDescriptor = null, HackModelStateDictionary? modelState = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        HttpContext = httpContext;
        ActionDescriptor = actionDescriptor;
        ModelState = modelState ?? new HackModelStateDictionary();
    }

    /// <summary>Initializes a copy of <paramref name="other"/> (filter contexts).</summary>
    /// <param name="other">The context to copy.</param>
    protected HackActionContext(HackActionContext other)
        : this(other?.HttpContext!, other!.ActionDescriptor, other.ModelState)
    {
    }

    /// <summary>Gets the request.</summary>
    public HackHttpContext HttpContext { get; }

    /// <summary>Gets the route values of the request.</summary>
    public HackRouteValueDictionary RouteValues => HttpContext.Request.RouteValues;

    /// <summary>Gets the controller action, or <see langword="null"/> for a minimal route.</summary>
    public HackControllerActionDescriptor? ActionDescriptor { get; }

    /// <summary>Gets the model state.</summary>
    public HackModelStateDictionary ModelState { get; }
}
