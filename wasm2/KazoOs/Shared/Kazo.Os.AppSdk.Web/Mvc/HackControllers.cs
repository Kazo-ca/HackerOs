using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;
using Kazo.Os.AppSdk.Web.Http;
using Microsoft.AspNetCore.Components;
using Kazo.Os.AppSdk.Web.Routing;
using Kazo.Os.AppSdk.Web.Views;

namespace Kazo.Os.AppSdk.Web.Mvc;

/// <summary>The context of a controller: the action context of the request it serves.</summary>
public sealed class HackControllerContext : HackActionContext
{
    internal HackControllerContext(HackActionContext context)
        : base(context)
    {
    }
}

/// <summary>
/// Base of every ASP.Hack controller (ASP.NET's <c>ControllerBase</c>, ADR 0076 §3): the request,
/// the model state, URL helpers and the methods that build results (<see cref="Ok()"/>,
/// <see cref="NotFound()"/>, <see cref="CreatedAtAction{TValues}(string, TValues, object)"/>...). Derive from <see cref="HackController"/>
/// for pages or <see cref="HackApiController"/> for an HTTP API.
/// </summary>
/// <remarks>
/// A controller is a public, non-abstract class whose name ends with <c>Controller</c> by convention;
/// its public instance methods are actions unless marked <c>[NonAction]</c>. A new instance serves each
/// request, built with the services of the request scope by the generated code (ADR 0082).
/// </remarks>
public abstract class HackControllerBase
{
    private HackControllerContext? _context;
    private IHackUrlHelper? _url;

    /// <summary>Gets the controller context (set by the framework before the action runs).</summary>
    public HackControllerContext ControllerContext
    {
        get => _context ?? throw new InvalidOperationException("The controller context is set when the framework runs an action.");
        internal set => _context = value;
    }

    /// <summary>Gets the request.</summary>
    public HackHttpContext HttpContext => ControllerContext.HttpContext;

    /// <summary>Gets the HTTP request.</summary>
    public HackHttpRequest Request => HttpContext.Request;

    /// <summary>Gets the HTTP response.</summary>
    public HackHttpResponse Response => HttpContext.Response;

    /// <summary>Gets the route values (ASP.NET's <c>RouteData.Values</c>).</summary>
    public HackRouteValueDictionary RouteValues => Request.RouteValues;

    /// <summary>Gets the binding and validation state of the arguments.</summary>
    public HackModelStateDictionary ModelState => ControllerContext.ModelState;

    /// <summary>Gets the URL helper (links to actions and routes).</summary>
    public IHackUrlHelper Url
    {
        get => _url ??= new HackUrlHelper(ControllerContext);
        set => _url = value;
    }

    /// <summary><c>200 OK</c> with no body.</summary>
    [NonAction]
    public virtual HackStatusCodeResult Ok() => new(200);

    /// <summary><c>200 OK</c> with a value (JSON, or text for a string).</summary>
    /// <param name="value">The value.</param>
    [NonAction]
    public virtual HackObjectResult Ok(object? value) => new(value, 200);

    /// <summary><c>204 No Content</c>.</summary>
    [NonAction]
    public virtual HackStatusCodeResult NoContent() => new(204);

    /// <summary>A status code with no body.</summary>
    /// <param name="statusCode">The status code.</param>
    [NonAction]
    public virtual HackStatusCodeResult StatusCode(int statusCode) => new(statusCode);

    /// <summary>A status code with a value.</summary>
    /// <param name="statusCode">The status code.</param>
    /// <param name="value">The value.</param>
    [NonAction]
    public virtual HackObjectResult StatusCode(int statusCode, object? value) => new(value, statusCode);

    /// <summary>Text, <c>text/plain; charset=utf-8</c> unless <paramref name="contentType"/> says otherwise.</summary>
    /// <param name="content">The text.</param>
    /// <param name="contentType">Content type, e.g. <c>text/html; charset=utf-8</c>.</param>
    [NonAction]
    public virtual HackContentResult Content(string content, string? contentType = null) => new() { Content = content, ContentType = contentType };

    /// <summary>Bytes to show or download.</summary>
    /// <param name="fileContents">The bytes.</param>
    /// <param name="contentType">Their type.</param>
    /// <param name="fileDownloadName">A download name, or <see langword="null"/> to show inline.</param>
    [NonAction]
    public virtual HackFileResult File(byte[] fileContents, string contentType, string? fileDownloadName = null) =>
        new(fileContents, contentType) { FileDownloadName = fileDownloadName };

    /// <summary>The content of a stream to show or download (read whole: the simulated network sends whole bodies).</summary>
    /// <param name="fileStream">The stream; it is disposed.</param>
    /// <param name="contentType">Its type.</param>
    /// <param name="fileDownloadName">A download name, or <see langword="null"/>.</param>
    [NonAction]
    public virtual HackFileResult File(Stream fileStream, string contentType, string? fileDownloadName = null)
    {
        ArgumentNullException.ThrowIfNull(fileStream);
        using (fileStream)
        {
            using MemoryStream buffer = new();
            fileStream.CopyTo(buffer);
            return File(buffer.ToArray(), contentType, fileDownloadName);
        }
    }

    /// <summary><c>302 Found</c> to <paramref name="url"/>.</summary>
    /// <param name="url">Target URL.</param>
    [NonAction]
    public virtual HackRedirectResult Redirect(string url) => new(url);

    /// <summary><c>301 Moved Permanently</c> to <paramref name="url"/>.</summary>
    /// <param name="url">Target URL.</param>
    [NonAction]
    public virtual HackRedirectResult RedirectPermanent(string url) => new(url, permanent: true);

    /// <summary><c>302 Found</c> to a URL of this site; refuses an external URL (open redirect).</summary>
    /// <param name="localUrl">A URL starting with <c>/</c> or <c>~/</c>.</param>
    [NonAction]
    public virtual HackLocalRedirectResult LocalRedirect(string localUrl) => new(localUrl);

    /// <summary><c>302 Found</c> to an action of this controller.</summary>
    /// <param name="actionName">Action name.</param>
    [NonAction]
    public virtual HackRedirectToActionResult RedirectToAction(string actionName) => new(actionName, null, null);

    /// <summary><c>302 Found</c> to an action, with route values (<c>new { id = 5 }</c>).</summary>
    /// <typeparam name="TValues">Type of the route values (inferred).</typeparam>
    /// <param name="actionName">Action name.</param>
    /// <param name="routeValues">Route values: an anonymous object or a dictionary.</param>
    [NonAction]
    public virtual HackRedirectToActionResult RedirectToAction<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TValues>(
        string? actionName,
        TValues routeValues) =>
        new(actionName, null, HackRouteValueDictionary.FromObject(routeValues));

    /// <summary><c>302 Found</c> to an action of another controller.</summary>
    /// <param name="actionName">Action name.</param>
    /// <param name="controllerName">Controller name (<c>Home</c>, not <c>HomeController</c>).</param>
    [NonAction]
    public virtual HackRedirectToActionResult RedirectToAction(string? actionName, string? controllerName) =>
        new(actionName, controllerName, null);

    /// <summary><c>302 Found</c> to an action of another controller, with route values.</summary>
    /// <typeparam name="TValues">Type of the route values (inferred).</typeparam>
    /// <param name="actionName">Action name.</param>
    /// <param name="controllerName">Controller name.</param>
    /// <param name="routeValues">Route values.</param>
    [NonAction]
    public virtual HackRedirectToActionResult RedirectToAction<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TValues>(
        string? actionName,
        string? controllerName,
        TValues routeValues) =>
        new(actionName, controllerName, HackRouteValueDictionary.FromObject(routeValues));

    /// <summary><c>201 Created</c> with <c>Location</c>.</summary>
    /// <param name="uri">The new resource's URL.</param>
    /// <param name="value">The created value.</param>
    [NonAction]
    public virtual HackCreatedResult Created(string? uri, object? value) => new(uri, value);

    /// <summary><c>201 Created</c> with the URL of an action as <c>Location</c>.</summary>
    /// <typeparam name="TValues">Type of the route values (inferred).</typeparam>
    /// <param name="actionName">The action that reads the resource.</param>
    /// <param name="routeValues">Its route values (<c>new { id = note.Id }</c>).</param>
    /// <param name="value">The created value.</param>
    [NonAction]
    public virtual HackCreatedAtActionResult CreatedAtAction<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TValues>(
        string? actionName,
        TValues routeValues,
        object? value) =>
        new(actionName, null, HackRouteValueDictionary.FromObject(routeValues), value);

    /// <summary><c>201 Created</c> with the URL of another controller's action as <c>Location</c>.</summary>
    /// <typeparam name="TValues">Type of the route values (inferred).</typeparam>
    /// <param name="actionName">The action that reads the resource.</param>
    /// <param name="controllerName">Its controller.</param>
    /// <param name="routeValues">Its route values.</param>
    /// <param name="value">The created value.</param>
    [NonAction]
    public virtual HackCreatedAtActionResult CreatedAtAction<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TValues>(
        string? actionName,
        string? controllerName,
        TValues routeValues,
        object? value) =>
        new(actionName, controllerName, HackRouteValueDictionary.FromObject(routeValues), value);

    /// <summary><c>404 Not Found</c>.</summary>
    [NonAction]
    public virtual HackStatusCodeResult NotFound() => new(404);

    /// <summary><c>404 Not Found</c> with a value.</summary>
    /// <param name="value">The value.</param>
    [NonAction]
    public virtual HackObjectResult NotFound(object? value) => new(value, 404);

    /// <summary><c>400 Bad Request</c>.</summary>
    [NonAction]
    public virtual HackStatusCodeResult BadRequest() => new(400);

    /// <summary><c>400 Bad Request</c> with a value.</summary>
    /// <param name="error">The value.</param>
    [NonAction]
    public virtual HackObjectResult BadRequest(object? error) => new(error, 400);

    /// <summary><c>400 Bad Request</c> with the errors of <paramref name="modelState"/> (<c>{ "Title": ["..."] }</c>).</summary>
    /// <param name="modelState">The model state.</param>
    [NonAction]
    public virtual HackJsonResult BadRequest(HackModelStateDictionary modelState)
    {
        ArgumentNullException.ThrowIfNull(modelState);
        return new HackJsonResult(modelState.ToErrorDictionary(), HackWebJsonContextAccess.ErrorDictionary) { StatusCode = 400 };
    }

    /// <summary><c>401 Unauthorized</c>.</summary>
    [NonAction]
    public virtual HackStatusCodeResult Unauthorized() => new(401);

    /// <summary><c>403 Forbidden</c> (no authentication schemes in v1: the request is simply refused).</summary>
    [NonAction]
    public virtual HackStatusCodeResult Forbid() => new(403);

    /// <summary><c>409 Conflict</c>.</summary>
    [NonAction]
    public virtual HackStatusCodeResult Conflict() => new(409);

    /// <summary><c>409 Conflict</c> with a value.</summary>
    /// <param name="error">The value.</param>
    [NonAction]
    public virtual HackObjectResult Conflict(object? error) => new(error, 409);

    /// <summary>An RFC 9457 problem (<c>application/problem+json</c>); 500 by default.</summary>
    /// <param name="detail">Explanation of this occurrence.</param>
    /// <param name="instance">URI of this occurrence.</param>
    /// <param name="statusCode">Status code.</param>
    /// <param name="title">Summary of the problem type.</param>
    /// <param name="type">URI of the problem type.</param>
    [NonAction]
    public virtual HackProblemResult Problem(string? detail = null, string? instance = null, int? statusCode = null, string? title = null, string? type = null) =>
        new(new HackProblemDetails { Detail = detail, Instance = instance, Status = statusCode ?? 500, Title = title, Type = type });

    /// <summary><c>400</c> with the errors of <see cref="ModelState"/> as a validation problem.</summary>
    [NonAction]
    public virtual HackProblemResult ValidationProblem() => ValidationProblem(ModelState);

    /// <summary><c>400</c> with the errors of <paramref name="modelState"/> as a validation problem.</summary>
    /// <param name="modelState">The model state.</param>
    [NonAction]
    public virtual HackProblemResult ValidationProblem(HackModelStateDictionary modelState) =>
        new(new HackValidationProblemDetails(modelState) { Status = 400 });

    /// <summary>
    /// Validates <paramref name="model"/> with its <c>DataAnnotations</c> attributes and
    /// <see cref="IValidatableObject"/>, adds the errors to <see cref="ModelState"/> and returns whether
    /// the model state is valid, like ASP.NET's <c>ControllerBase.TryValidateModel</c>. Useful when an
    /// action builds or changes a model after binding (a <c>PATCH</c> merged into the stored entity).
    /// </summary>
    /// <typeparam name="TModel">The model type; its public properties are kept by the trimmer.</typeparam>
    /// <param name="model">The model to validate.</param>
    /// <param name="prefix">Prefix of the model state keys (<c>note</c> gives <c>note.Title</c>); none by default.</param>
    [NonAction]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "TModel is annotated with PublicProperties: the trimmer keeps the properties and their validation attributes that Validator reads.")]
    public virtual bool TryValidateModel<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TModel>(TModel model, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        List<ValidationResult> results = [];
        Validator.TryValidateObject(model, new ValidationContext(model, HttpContext.RequestServices, null), results, validateAllProperties: true);
        foreach (ValidationResult result in results)
        {
            string message = result.ErrorMessage ?? "The value is invalid.";
            string[] members = [.. result.MemberNames];
            foreach (string member in members.Length == 0 ? [string.Empty] : members)
            {
                string key = string.IsNullOrEmpty(prefix) ? member : member.Length == 0 ? prefix : $"{prefix}.{member}";
                ModelState.AddModelError(key, message);
            }
        }

        return ModelState.IsValid;
    }
}

/// <summary>
/// A controller of pages (ASP.NET's <c>Controller</c>): <see cref="View()"/>, <see cref="PartialView()"/>,
/// <see cref="ViewData"/>, <see cref="Json(object?)"/>, and filter hooks <see cref="OnActionExecuting"/> /
/// <see cref="OnActionExecuted"/>. Complex parameters are bound from the form (and route, query).
/// </summary>
public abstract class HackController : HackControllerBase, IHackActionFilter
{
    private HackViewDataDictionary? _viewData;

    /// <summary>Gets the values handed to the view (<c>ViewData["Title"] = "Notes"</c>); the view and its layout read them.</summary>
    public HackViewDataDictionary ViewData => _viewData ??= new HackViewDataDictionary();

    /// <summary>Renders the view named after the action (<c>Views/&lt;Controller&gt;/&lt;Action&gt;.razor</c>), without model.</summary>
    [NonAction]
    public virtual HackViewResult View() => View(viewName: null, model: null);

    /// <summary>Renders the view named after the action with <paramref name="model"/>.</summary>
    /// <param name="model">The model (<c>@inherits HackView&lt;TModel&gt;</c> in the view).</param>
    [NonAction]
    public virtual HackViewResult View(object? model) => View(viewName: null, model);

    /// <summary>Renders a named view (<c>View("Edit")</c>, searched in the controller's folder then <c>Shared/</c>) or a path (<c>~/Views/Notes/Edit.razor</c>).</summary>
    /// <param name="viewName">The view name or path.</param>
    [NonAction]
    public virtual HackViewResult View(string? viewName) => View(viewName, model: null);

    /// <summary>Renders a named view with <paramref name="model"/>.</summary>
    /// <param name="viewName">The view name or path; the action name when <see langword="null"/>.</param>
    /// <param name="model">The model.</param>
    [NonAction]
    public virtual HackViewResult View(string? viewName, object? model) =>
        new() { ViewName = viewName, Model = model, ViewData = ViewData };

    /// <summary>Renders a view by type (<c>View&lt;NoteListView&gt;(model)</c>), without lookup.</summary>
    /// <typeparam name="TView">The view component.</typeparam>
    /// <param name="model">The model.</param>
    [NonAction]
    public virtual HackViewResult View<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TView>(object? model = null)
        where TView : IComponent =>
        new() { ViewType = typeof(TView), Model = model, ViewData = ViewData };

    /// <summary>Renders the view named after the action without its layout (an HTML fragment for a script).</summary>
    [NonAction]
    public virtual HackViewResult PartialView() => PartialView(viewName: null, model: null);

    /// <summary>Renders the view named after the action without layout, with <paramref name="model"/>.</summary>
    /// <param name="model">The model.</param>
    [NonAction]
    public virtual HackViewResult PartialView(object? model) => PartialView(viewName: null, model);

    /// <summary>Renders a named view without layout.</summary>
    /// <param name="viewName">The view name or path.</param>
    [NonAction]
    public virtual HackViewResult PartialView(string? viewName) => PartialView(viewName, model: null);

    /// <summary>Renders a named view without layout, with <paramref name="model"/>.</summary>
    /// <param name="viewName">The view name or path; the action name when <see langword="null"/>.</param>
    /// <param name="model">The model.</param>
    [NonAction]
    public virtual HackViewResult PartialView(string? viewName, object? model) =>
        new() { ViewName = viewName, Model = model, ViewData = ViewData, IsPartial = true };

    /// <summary>Renders a view by type without layout.</summary>
    /// <typeparam name="TView">The view component.</typeparam>
    /// <param name="model">The model.</param>
    [NonAction]
    public virtual HackViewResult PartialView<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TView>(object? model = null)
        where TView : IComponent =>
        new() { ViewType = typeof(TView), Model = model, ViewData = ViewData, IsPartial = true };

    /// <summary>JSON, even for a string.</summary>
    /// <param name="data">The value.</param>
    [NonAction]
    public virtual HackJsonResult Json(object? data) => new(data);

    /// <summary>JSON with explicit metadata (trim-safe).</summary>
    /// <param name="data">The value.</param>
    /// <param name="typeInfo">Metadata of its type.</param>
    [NonAction]
    public virtual HackJsonResult Json(object? data, JsonTypeInfo typeInfo) => new(data, typeInfo);

    /// <summary>Runs before every action of the controller (after the global and attribute filters' "before").</summary>
    /// <param name="context">The arguments; set a result to skip the action.</param>
    [NonAction]
    public virtual void OnActionExecuting(HackActionExecutingContext context)
    {
    }

    /// <summary>Runs after every action of the controller.</summary>
    /// <param name="context">The result or the exception.</param>
    [NonAction]
    public virtual void OnActionExecuted(HackActionExecutedContext context)
    {
    }
}

/// <summary>
/// A controller of an HTTP API (ASP.NET's <c>ControllerBase</c> with <c>[ApiController]</c>):
/// complex parameters come from the JSON body, an invalid model answers <c>400</c> with a validation
/// problem before the action runs, and client errors without body become <c>application/problem+json</c>.
/// </summary>
public abstract class HackApiController : HackControllerBase;

/// <summary>Access to the framework's own JSON metadata from public members.</summary>
internal static class HackWebJsonContextAccess
{
    /// <summary>Metadata of the model-state error dictionary.</summary>
    public static JsonTypeInfo ErrorDictionary => Hosting.HackWebJsonContext.Default.DictionaryStringStringArray;
}
