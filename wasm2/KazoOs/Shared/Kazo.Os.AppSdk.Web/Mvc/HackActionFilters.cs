namespace Kazo.Os.AppSdk.Web.Mvc;

/// <summary>Marks a filter (ASP.NET's <c>IFilterMetadata</c>): an attribute on a controller or action, or a global filter.</summary>
public interface IHackFilterMetadata;

/// <summary>A filter with an order: lower runs first (outside); filters without one have order 0.</summary>
public interface IHackOrderedFilter : IHackFilterMetadata
{
    /// <summary>Gets the order.</summary>
    int Order { get; }
}

/// <summary>A synchronous action filter (ASP.NET's <c>IActionFilter</c>, ADR 0076 §6): code before and after the action.</summary>
public interface IHackActionFilter : IHackFilterMetadata
{
    /// <summary>Runs before the action; setting <see cref="HackActionExecutingContext.Result"/> skips it.</summary>
    /// <param name="context">The arguments, the controller and the model state.</param>
    void OnActionExecuting(HackActionExecutingContext context);

    /// <summary>Runs after the action, or after it threw.</summary>
    /// <param name="context">The result or the exception.</param>
    void OnActionExecuted(HackActionExecutedContext context);
}

/// <summary>Continues the filter pipeline: the next filters, then the action.</summary>
/// <returns>The context after the action.</returns>
public delegate Task<HackActionExecutedContext> HackActionExecutionDelegate();

/// <summary>An asynchronous action filter (ASP.NET's <c>IAsyncActionFilter</c>).</summary>
public interface IHackAsyncActionFilter : IHackFilterMetadata
{
    /// <summary>Runs around the action: call <paramref name="next"/> to run it, or set a result to skip it.</summary>
    /// <param name="context">The arguments, the controller and the model state.</param>
    /// <param name="next">Runs the rest of the pipeline.</param>
    Task OnActionExecutionAsync(HackActionExecutingContext context, HackActionExecutionDelegate next);
}

/// <summary>
/// Base of action filter attributes (ASP.NET's <c>ActionFilterAttribute</c>): override the synchronous
/// or the asynchronous methods. The build writes the attribute's construction into the route table.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public abstract class HackActionFilterAttribute : Attribute, IHackActionFilter, IHackAsyncActionFilter, IHackOrderedFilter
{
    /// <inheritdoc />
    public int Order { get; set; }

    /// <inheritdoc />
    public virtual void OnActionExecuting(HackActionExecutingContext context)
    {
    }

    /// <inheritdoc />
    public virtual void OnActionExecuted(HackActionExecutedContext context)
    {
    }

    /// <inheritdoc />
    public virtual async Task OnActionExecutionAsync(HackActionExecutingContext context, HackActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        OnActionExecuting(context);
        if (context.Result is null)
        {
            OnActionExecuted(await next().ConfigureAwait(false));
        }
    }
}

/// <summary>The context before an action runs.</summary>
public sealed class HackActionExecutingContext : HackActionContext
{
    internal HackActionExecutingContext(HackActionContext context, HackControllerBase controller, IDictionary<string, object?> arguments)
        : base(context)
    {
        Controller = controller;
        ActionArguments = arguments;
    }

    /// <summary>Gets the controller instance.</summary>
    public HackControllerBase Controller { get; }

    /// <summary>Gets the bound arguments by parameter name; a filter may replace them.</summary>
    public IDictionary<string, object?> ActionArguments { get; }

    /// <summary>Gets or sets a result that skips the action (and the filters after this one).</summary>
    public IHackActionResult? Result { get; set; }
}

/// <summary>The context after an action ran (or threw, or was skipped).</summary>
public sealed class HackActionExecutedContext : HackActionContext
{
    internal HackActionExecutedContext(HackActionContext context, HackControllerBase controller)
        : base(context) => Controller = controller;

    /// <summary>Gets the controller instance.</summary>
    public HackControllerBase Controller { get; }

    /// <summary>Gets or sets the result that will be executed.</summary>
    public IHackActionResult? Result { get; set; }

    /// <summary>Gets whether a filter skipped the action by setting a result.</summary>
    public bool Canceled { get; internal set; }

    /// <summary>Gets or sets the exception the action (or a later filter) threw.</summary>
    public Exception? Exception { get; set; }

    /// <summary>Gets or sets whether a filter handled <see cref="Exception"/> (then <see cref="Result"/> is sent).</summary>
    public bool ExceptionHandled { get; set; }
}
