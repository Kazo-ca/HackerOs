using System.Runtime.ExceptionServices;
using Kazo.Os.AppSdk.Web.Hosting;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.AppSdk.Web.Mvc;

/// <summary>MVC settings of a site (ASP.NET's <c>MvcOptions</c>): global filters.</summary>
public sealed class KazoMvcOptions
{
    /// <summary>Gets the filters that run for every action, before the controller's and the action's.</summary>
    public IList<IKazoFilterMetadata> Filters { get; } = [];
}

/// <summary>
/// Runs one controller action (ASP.NET's <c>ControllerActionInvoker</c>): builds the controller,
/// binds and validates the arguments, runs the filters around the call and executes the result.
/// </summary>
internal static class KazoActionInvoker
{
    /// <summary>Creates the endpoints of the attribute-routed actions of <paramref name="table"/>.</summary>
    public static IEnumerable<KazoEndpoint> CreateAttributeEndpoints(KazoControllerRouteTable table)
    {
        foreach (KazoControllerActionDescriptor action in table.Actions)
        {
            foreach (KazoAttributeRouteInfo route in action.AttributeRoutes)
            {
                List<object> metadata = [action];
                if (route.Name is not null)
                {
                    metadata.Add(new KazoRouteNameMetadata(route.Name));
                }

                yield return new KazoEndpoint(
                    KazoRoutePattern.Parse(route.Template, RequiredValues(action)),
                    route.HttpMethods,
                    context => InvokeAsync(context, action),
                    $"{action.DisplayName} ({action.ControllerType.Assembly.GetName().Name})",
                    route.Order,
                    metadata);
            }
        }
    }

    /// <summary>
    /// Creates one endpoint per conventionally routed action for <paramref name="template"/>, as
    /// ASP.NET does: each carries the action's controller and action names as required values.
    /// </summary>
    /// <param name="table">The actions.</param>
    /// <param name="name">Route name.</param>
    /// <param name="template">Conventional template, e.g. <c>{controller=Home}/{action=Index}/{id?}</c>.</param>
    /// <param name="defaults">Controller and action when the template has no such parameter.</param>
    /// <param name="order">Order of the conventional route (after attribute routes).</param>
    public static IEnumerable<KazoEndpoint> CreateConventionalEndpoints(
        KazoControllerRouteTable table,
        string name,
        string template,
        KazoRouteValueDictionary defaults,
        int order)
    {
        KazoRouteTemplate parsed = KazoRouteTemplate.Parse(template);
        bool hasController = parsed.Segments.Any(segment => segment.Parameter?.Name.Equals("controller", StringComparison.OrdinalIgnoreCase) == true);
        bool hasAction = parsed.Segments.Any(segment => segment.Parameter?.Name.Equals("action", StringComparison.OrdinalIgnoreCase) == true);
        if ((!hasController && defaults.GetString("controller") is null) || (!hasAction && defaults.GetString("action") is null))
        {
            throw new FormatException($"The conventional route '{name}' ('{template}') must give a controller and an action (a parameter or a default).");
        }

        foreach (KazoControllerActionDescriptor action in table.Actions.Where(action => action.AttributeRoutes.Count == 0))
        {
            if ((!hasController && !action.ControllerName.Equals(defaults.GetString("controller"), StringComparison.OrdinalIgnoreCase))
                || (!hasAction && !action.ActionName.Equals(defaults.GetString("action"), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Url.RouteUrl(name, values) tries every endpoint of the route: each action carries the name.
            List<object> metadata = [action, new KazoRouteNameMetadata(name)];
            yield return new KazoEndpoint(
                new KazoRoutePattern(parsed, RequiredValues(action)),
                action.HttpMethods,
                context => InvokeAsync(context, action),
                $"{action.DisplayName} ({action.ControllerType.Assembly.GetName().Name}) via {name}",
                order,
                metadata);
        }
    }

    /// <summary>Runs <paramref name="action"/> for <paramref name="httpContext"/>.</summary>
    public static async Task InvokeAsync(KazoHttpContext httpContext, KazoControllerActionDescriptor action)
    {
        IServiceProvider services = httpContext.RequestServices;
        KazoMvcOptions options = services.GetService<KazoMvcOptions>() ?? new KazoMvcOptions();
        KazoWebJsonOptions json = services.GetService<KazoWebJsonOptions>() ?? new KazoWebJsonOptions();
        KazoActionContext actionContext = new(httpContext, action);
        KazoControllerBase controller = action.CreateController(services);
        controller.ControllerContext = new KazoControllerContext(actionContext);
        try
        {
            KazoModelBinder binder = new(actionContext, json.SerializerOptions);
            object?[] arguments = action.BindArguments(binder);
            IKazoActionResult result;
            if (binder.UnsupportedMediaType)
            {
                result = new KazoStatusCodeResult(415);
            }
            else if (action.IsApiController && !actionContext.ModelState.IsValid)
            {
                // [ApiController]'s automatic 400: the action does not run.
                result = new KazoProblemResult(new KazoValidationProblemDetails(actionContext.ModelState) { Status = 400 });
            }
            else
            {
                result = await RunFiltersAsync(actionContext, controller, action, arguments, options).ConfigureAwait(false);
            }

            if (action.IsApiController && result is IKazoClientErrorResult { StatusCode: >= 400 } clientError)
            {
                result = new KazoProblemResult(KazoProblemDefaults.ForStatus(clientError.StatusCode));
            }

            await result.ExecuteResultAsync(actionContext).ConfigureAwait(false);
        }
        finally
        {
            switch (controller)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    /// <summary>Converts what an action returned into a result.</summary>
    internal static IKazoActionResult ToResult(object? returned, Type? declaredType = null) => returned switch
    {
        IKazoActionResult result => result,
        _ => new KazoObjectResult(returned) { DeclaredType = declaredType }
    };

    private static async Task<IKazoActionResult> RunFiltersAsync(
        KazoActionContext actionContext,
        KazoControllerBase controller,
        KazoControllerActionDescriptor action,
        object?[] arguments,
        KazoMvcOptions options)
    {
        // Order: global, controller, action (stable within an order); the controller itself runs first, as in ASP.NET.
        List<IKazoFilterMetadata> filters = [.. options.Filters
            .Concat(action.Filters)
            .Select((filter, index) => (filter, index))
            .OrderBy(item => item.filter is IKazoOrderedFilter ordered ? ordered.Order : 0)
            .ThenBy(item => item.index)
            .Select(item => item.filter)];
        if (controller is IKazoActionFilter controllerFilter)
        {
            filters.Insert(0, controllerFilter);
        }

        Dictionary<string, object?> argumentMap = new(StringComparer.Ordinal);
        for (int index = 0; index < action.Parameters.Count && index < arguments.Length; index++)
        {
            argumentMap[action.Parameters[index].Name] = arguments[index];
        }

        KazoActionExecutingContext executing = new(actionContext, controller, argumentMap);
        KazoActionExecutedContext executed = await RunFilterAsync(0).ConfigureAwait(false);
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            ExceptionDispatchInfo.Capture(executed.Exception).Throw();
        }

        return executed.Result ?? KazoEmptyResult.Instance;

        async Task<KazoActionExecutedContext> RunFilterAsync(int index)
        {
            if (index == filters.Count)
            {
                KazoActionExecutedContext afterAction = new(actionContext, controller);
                try
                {
                    object?[] finalArguments = [.. action.Parameters.Select((parameter, position) =>
                        argumentMap.TryGetValue(parameter.Name, out object? value) ? value : arguments[position])];
                    afterAction.Result = ToResult(await action.Invoke(controller, finalArguments).ConfigureAwait(false), action.DeclaredResultType);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    afterAction.Exception = exception;
                }

                return afterAction;
            }

            switch (filters[index])
            {
                case IKazoAsyncActionFilter asyncFilter:
                {
                    KazoActionExecutedContext? inner = null;
                    await asyncFilter.OnActionExecutionAsync(executing, async () => inner = await RunFilterAsync(index + 1).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    return inner ?? Canceled();
                }

                case IKazoActionFilter filter:
                {
                    filter.OnActionExecuting(executing);
                    if (executing.Result is not null)
                    {
                        return Canceled();
                    }

                    KazoActionExecutedContext inner = await RunFilterAsync(index + 1).ConfigureAwait(false);
                    filter.OnActionExecuted(inner);
                    return inner;
                }

                default:
                    return await RunFilterAsync(index + 1).ConfigureAwait(false);
            }
        }

        KazoActionExecutedContext Canceled() => new(actionContext, controller) { Result = executing.Result, Canceled = true };
    }

    private static Dictionary<string, string> RequiredValues(KazoControllerActionDescriptor action) =>
        new(StringComparer.OrdinalIgnoreCase) { ["controller"] = action.ControllerName, ["action"] = action.ActionName };
}
