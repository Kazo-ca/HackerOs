using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Kazo.Os.AppSdk.Web.Hosting;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.AppSdk.Web.Mvc;

/// <summary>
/// What an action (or a minimal route) answers (ASP.NET's <c>IActionResult</c>, ADR 0076 §5): it
/// writes the status, headers and body of the response.
/// </summary>
public interface IKazoActionResult
{
    /// <summary>Writes the response.</summary>
    /// <param name="context">The action context.</param>
    Task ExecuteResultAsync(KazoActionContext context);
}

/// <summary>
/// A result that is only a client error status: an API controller (<see cref="KazoApiController"/>)
/// turns it into <c>application/problem+json</c>, like ASP.NET's <c>IClientErrorActionResult</c>.
/// </summary>
public interface IKazoClientErrorResult : IKazoActionResult
{
    /// <summary>Gets the status code.</summary>
    int StatusCode { get; }
}

/// <summary>Answers a status code with no body (<c>NoContent</c>, <c>NotFound()</c>, <c>StatusCode(418)</c>...).</summary>
public class KazoStatusCodeResult(int statusCode) : IKazoClientErrorResult
{
    /// <inheritdoc />
    public int StatusCode { get; } = statusCode;

    /// <inheritdoc />
    public virtual Task ExecuteResultAsync(KazoActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.HttpContext.Response.StatusCode = StatusCode;
        return Task.CompletedTask;
    }
}

/// <summary>Writes nothing and keeps the response as it is (a <c>void</c> action, or one that wrote the response itself).</summary>
public sealed class KazoEmptyResult : IKazoActionResult
{
    /// <summary>The shared instance.</summary>
    public static KazoEmptyResult Instance { get; } = new();

    /// <inheritdoc />
    public Task ExecuteResultAsync(KazoActionContext context) => Task.CompletedTask;
}

/// <summary>Answers text with a content type (<c>Content("...", "text/html")</c>).</summary>
public sealed class KazoContentResult : IKazoActionResult
{
    /// <summary>Gets or sets the text.</summary>
    public string? Content { get; init; }

    /// <summary>Gets or sets the content type (<c>text/plain; charset=utf-8</c> by default).</summary>
    public string? ContentType { get; init; }

    /// <summary>Gets or sets the status code (200 by default).</summary>
    public int? StatusCode { get; init; }

    /// <inheritdoc />
    public Task ExecuteResultAsync(KazoActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        KazoHttpResponse response = context.HttpContext.Response;
        response.StatusCode = StatusCode ?? 200;
        response.ContentType = ContentType ?? "text/plain; charset=utf-8";
        return response.WriteAsync(Content ?? string.Empty, context.HttpContext.RequestAborted);
    }
}

/// <summary>
/// Answers a value: JSON (with the site's <see cref="KazoWebJsonOptions"/>), plain text for a
/// <see cref="string"/>, <c>204 No Content</c> for <see langword="null"/>, as ASP.NET's <c>ObjectResult</c>
/// with its default formatters. An action returning a plain object gets one of these.
/// </summary>
public class KazoObjectResult(object? value, int? statusCode = null) : IKazoActionResult
{
    /// <summary>Gets the value.</summary>
    public object? Value { get; } = value;

    /// <summary>Gets the status code (200 when <see langword="null"/>).</summary>
    public int? StatusCode { get; } = statusCode;

    /// <summary>Gets or sets the content type; <c>application/json; charset=utf-8</c> by default.</summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Gets or sets the type the value was declared as (an action's return type), used for JSON when the
    /// runtime type has no metadata in the site's JSON options (ASP.NET's <c>ObjectResult.DeclaredType</c>).
    /// </summary>
    public Type? DeclaredType { get; init; }

    /// <inheritdoc />
    public virtual Task ExecuteResultAsync(KazoActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        KazoHttpResponse response = context.HttpContext.Response;
        int status = StatusCode ?? 200;
        switch (Value)
        {
            case null when status == 200:
                response.StatusCode = 204;
                return Task.CompletedTask;
            case null:
                response.StatusCode = status;
                return Task.CompletedTask;
            case string text when ContentType is null:
                response.StatusCode = status;
                response.ContentType = "text/plain; charset=utf-8";
                return response.WriteAsync(text, context.HttpContext.RequestAborted);
            default:
                response.StatusCode = status;
                response.ContentType = ContentType ?? "application/json; charset=utf-8";
                KazoJsonWriter.Write(context.HttpContext, response.Body, Value, DeclaredType);
                return Task.CompletedTask;
        }
    }
}

/// <summary>Answers JSON, always (even for a string or <see langword="null"/>), like ASP.NET's <c>JsonResult</c>.</summary>
public sealed class KazoJsonResult : IKazoActionResult
{
    private readonly JsonTypeInfo? _typeInfo;

    /// <summary>Serializes <paramref name="value"/> with the site's JSON options.</summary>
    /// <param name="value">The value.</param>
    public KazoJsonResult(object? value) => Value = value;

    /// <summary>Serializes <paramref name="value"/> with explicit metadata (trim-safe without registering a context).</summary>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">Metadata of the value's type.</param>
    public KazoJsonResult(object? value, JsonTypeInfo typeInfo)
    {
        Value = value;
        _typeInfo = typeInfo;
    }

    /// <summary>Gets the value.</summary>
    public object? Value { get; }

    /// <summary>Gets or sets the status code (200 by default).</summary>
    public int? StatusCode { get; init; }

    /// <inheritdoc />
    public Task ExecuteResultAsync(KazoActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        KazoHttpResponse response = context.HttpContext.Response;
        response.StatusCode = StatusCode ?? 200;
        response.ContentType = "application/json; charset=utf-8";
        if (_typeInfo is not null)
        {
            JsonSerializer.Serialize(response.Body, Value, _typeInfo);
        }
        else
        {
            KazoJsonWriter.Write(context.HttpContext, response.Body, Value);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Answers bytes, optionally as a download (<c>Content-Disposition: attachment</c>).</summary>
public sealed class KazoFileResult : IKazoActionResult
{
    /// <summary>Initializes a file result.</summary>
    /// <param name="contents">The bytes.</param>
    /// <param name="contentType">Their type.</param>
    public KazoFileResult(byte[] contents, string contentType)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        Contents = contents;
        ContentType = contentType;
    }

    /// <summary>Gets the bytes.</summary>
    public byte[] Contents { get; }

    /// <summary>Gets the content type.</summary>
    public string ContentType { get; }

    /// <summary>Gets or sets the download name; the browser saves the file instead of showing it.</summary>
    public string? FileDownloadName { get; init; }

    /// <inheritdoc />
    public Task ExecuteResultAsync(KazoActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        KazoHttpResponse response = context.HttpContext.Response;
        response.StatusCode = 200;
        response.ContentType = ContentType;
        if (!string.IsNullOrEmpty(FileDownloadName))
        {
            string ascii = new([.. FileDownloadName.Select(c => c is < ' ' or > '~' or '"' or '\\' ? '_' : c)]);
            response.Headers["Content-Disposition"] =
                $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(FileDownloadName)}";
        }

        response.Body.Write(Contents);
        return Task.CompletedTask;
    }
}

/// <summary>Redirects to a URL: <c>302</c>, <c>301</c> when permanent, <c>307</c>/<c>308</c> when the method is kept.</summary>
public class KazoRedirectResult : IKazoActionResult
{
    /// <summary>Initializes a redirection.</summary>
    /// <param name="url">Target URL; <c>~/</c> means the site root.</param>
    /// <param name="permanent">Whether it is permanent.</param>
    /// <param name="preserveMethod">Whether the client must repeat the same method and body.</param>
    public KazoRedirectResult(string url, bool permanent = false, bool preserveMethod = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        Url = url;
        Permanent = permanent;
        PreserveMethod = preserveMethod;
    }

    /// <summary>Gets the target URL.</summary>
    public string Url { get; }

    /// <summary>Gets whether the redirection is permanent.</summary>
    public bool Permanent { get; }

    /// <summary>Gets whether the method is kept.</summary>
    public bool PreserveMethod { get; }

    /// <inheritdoc />
    public virtual Task ExecuteResultAsync(KazoActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        WriteRedirect(context, ResolveUrl(context));
        return Task.CompletedTask;
    }

    /// <summary>Gets the URL to send in <c>Location</c>.</summary>
    /// <param name="context">The action context.</param>
    protected virtual string ResolveUrl(KazoActionContext context) => Url.StartsWith("~/", StringComparison.Ordinal) ? Url[1..] : Url;

    private protected void WriteRedirect(KazoActionContext context, string location)
    {
        KazoHttpResponse response = context.HttpContext.Response;
        response.StatusCode = (Permanent, PreserveMethod) switch
        {
            (true, true) => 308,
            (true, false) => 301,
            (false, true) => 307,
            _ => 302
        };
        response.Headers["Location"] = location;
    }
}

/// <summary>Redirects to a URL of this site only; an absolute or protocol-relative URL is refused (open redirect).</summary>
public sealed class KazoLocalRedirectResult(string localUrl, bool permanent = false, bool preserveMethod = false)
    : KazoRedirectResult(localUrl, permanent, preserveMethod)
{
    /// <inheritdoc />
    protected override string ResolveUrl(KazoActionContext context) =>
        KazoUrlHelper.IsLocalUrl(Url)
            ? base.ResolveUrl(context)
            : throw new InvalidOperationException($"The supplied URL '{Url}' is not local. A local URL starts with '/' or '~/'.");
}

/// <summary>Redirects to the URL of an action, generated from the routes (<c>RedirectToAction("Index")</c>).</summary>
public sealed class KazoRedirectToActionResult(string? actionName, string? controllerName, KazoRouteValueDictionary? routeValues, bool permanent = false)
    : KazoRedirectResult("~/", permanent)
{
    /// <summary>Gets the action name (the current one when <see langword="null"/>).</summary>
    public string? ActionName { get; } = actionName;

    /// <summary>Gets the controller name (the current one when <see langword="null"/>).</summary>
    public string? ControllerName { get; } = controllerName;

    /// <summary>Gets the route values.</summary>
    public KazoRouteValueDictionary RouteValues { get; } = routeValues ?? [];

    /// <inheritdoc />
    protected override string ResolveUrl(KazoActionContext context) =>
        new KazoUrlHelper(context).Action(ActionName, ControllerName, RouteValues)
        ?? throw new InvalidOperationException(
            $"No route matches the supplied values (action '{ActionName ?? context.RouteValues.GetString("action")}', controller '{ControllerName ?? context.RouteValues.GetString("controller")}').");
}

/// <summary><c>201 Created</c> with <c>Location</c> and the created value.</summary>
public sealed class KazoCreatedResult(string? location, object? value) : KazoObjectResult(value, 201)
{
    /// <summary>Gets the URL of the new resource.</summary>
    public string? Location { get; } = location;

    /// <inheritdoc />
    public override Task ExecuteResultAsync(KazoActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Location is not null)
        {
            context.HttpContext.Response.Headers["Location"] = Location;
        }

        return base.ExecuteResultAsync(context);
    }
}

/// <summary><c>201 Created</c> with the absolute URL of an action as <c>Location</c> (<c>CreatedAtAction("Get", new { id }, note)</c>).</summary>
public sealed class KazoCreatedAtActionResult(string? actionName, string? controllerName, KazoRouteValueDictionary? routeValues, object? value)
    : KazoObjectResult(value, 201)
{
    /// <summary>Gets the action name.</summary>
    public string? ActionName { get; } = actionName;

    /// <summary>Gets the controller name.</summary>
    public string? ControllerName { get; } = controllerName;

    /// <summary>Gets the route values.</summary>
    public KazoRouteValueDictionary RouteValues { get; } = routeValues ?? [];

    /// <inheritdoc />
    public override Task ExecuteResultAsync(KazoActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        KazoHttpRequest request = context.HttpContext.Request;
        string path = new KazoUrlHelper(context).Action(ActionName, ControllerName, RouteValues)
            ?? throw new InvalidOperationException($"No route matches the supplied values (action '{ActionName}').");
        context.HttpContext.Response.Headers["Location"] = $"{request.Scheme}://{request.Host}{path}";
        return base.ExecuteResultAsync(context);
    }
}

/// <summary>An RFC 9457 problem (<c>application/problem+json</c>), like ASP.NET's <c>ProblemDetails</c>.</summary>
public class KazoProblemDetails
{
    /// <summary>Gets or sets a URI that identifies the problem type.</summary>
    [System.Text.Json.Serialization.JsonPropertyOrder(-5)]
    public string? Type { get; set; }

    /// <summary>Gets or sets a short summary of the problem type.</summary>
    [System.Text.Json.Serialization.JsonPropertyOrder(-4)]
    public string? Title { get; set; }

    /// <summary>Gets or sets the status code.</summary>
    [System.Text.Json.Serialization.JsonPropertyOrder(-3)]
    public int? Status { get; set; }

    /// <summary>Gets or sets an explanation of this occurrence.</summary>
    [System.Text.Json.Serialization.JsonPropertyOrder(-2)]
    public string? Detail { get; set; }

    /// <summary>Gets or sets a URI of this occurrence.</summary>
    [System.Text.Json.Serialization.JsonPropertyOrder(-1)]
    public string? Instance { get; set; }

    /// <summary>Gets extra members (<c>traceId</c>...). Values must be strings, numbers or booleans.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public IDictionary<string, object?> Extensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>A problem listing the invalid fields (<c>errors</c>), like ASP.NET's <c>ValidationProblemDetails</c>.</summary>
public sealed class KazoValidationProblemDetails : KazoProblemDetails
{
    /// <summary>Initializes an empty validation problem.</summary>
    public KazoValidationProblemDetails()
    {
        Title = "One or more validation errors occurred.";
    }

    /// <summary>Initializes a validation problem from a model state.</summary>
    /// <param name="modelState">The model state.</param>
    public KazoValidationProblemDetails(KazoModelStateDictionary modelState)
        : this()
    {
        ArgumentNullException.ThrowIfNull(modelState);
        foreach (KeyValuePair<string, string[]> error in modelState.ToErrorDictionary())
        {
            Errors[error.Key] = error.Value;
        }
    }

    /// <summary>Gets the errors per field.</summary>
    public IDictionary<string, string[]> Errors { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal);
}

/// <summary>Answers a <see cref="KazoProblemDetails"/> as <c>application/problem+json</c>, with <c>traceId</c>.</summary>
public sealed class KazoProblemResult(KazoProblemDetails problem) : IKazoActionResult
{
    /// <summary>Gets the problem.</summary>
    public KazoProblemDetails Problem { get; } = problem ?? throw new ArgumentNullException(nameof(problem));

    /// <inheritdoc />
    public Task ExecuteResultAsync(KazoActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        KazoProblemDefaults.Apply(Problem, context.HttpContext);
        KazoHttpResponse response = context.HttpContext.Response;
        response.StatusCode = Problem.Status ?? 500;
        response.ContentType = "application/problem+json; charset=utf-8";
        if (Problem is KazoValidationProblemDetails validation)
        {
            JsonSerializer.Serialize(response.Body, validation, KazoWebJsonContext.Default.KazoValidationProblemDetails);
        }
        else
        {
            JsonSerializer.Serialize(response.Body, Problem, KazoWebJsonContext.Default.KazoProblemDetails);
        }

        return Task.CompletedTask;
    }
}

/// <summary>The type URIs and titles ASP.NET Core gives client and server errors.</summary>
internal static class KazoProblemDefaults
{
    private static readonly Dictionary<int, (string Type, string Title)> Defaults = new()
    {
        [400] = ("https://tools.ietf.org/html/rfc9110#section-15.5.1", "Bad Request"),
        [401] = ("https://tools.ietf.org/html/rfc9110#section-15.5.2", "Unauthorized"),
        [403] = ("https://tools.ietf.org/html/rfc9110#section-15.5.4", "Forbidden"),
        [404] = ("https://tools.ietf.org/html/rfc9110#section-15.5.5", "Not Found"),
        [405] = ("https://tools.ietf.org/html/rfc9110#section-15.5.6", "Method Not Allowed"),
        [406] = ("https://tools.ietf.org/html/rfc9110#section-15.5.7", "Not Acceptable"),
        [408] = ("https://tools.ietf.org/html/rfc9110#section-15.5.9", "Request Timeout"),
        [409] = ("https://tools.ietf.org/html/rfc9110#section-15.5.10", "Conflict"),
        [412] = ("https://tools.ietf.org/html/rfc9110#section-15.5.13", "Precondition Failed"),
        [415] = ("https://tools.ietf.org/html/rfc9110#section-15.5.16", "Unsupported Media Type"),
        [422] = ("https://tools.ietf.org/html/rfc4918#section-11.2", "Unprocessable Entity"),
        [500] = ("https://tools.ietf.org/html/rfc9110#section-15.6.1", "An error occurred while processing your request.")
    };

    /// <summary>Fills a problem's missing type and title from its status, and adds <c>traceId</c>.</summary>
    public static void Apply(KazoProblemDetails problem, KazoHttpContext context)
    {
        problem.Status ??= 500;
        if (Defaults.TryGetValue(problem.Status.Value, out (string Type, string Title) defaults))
        {
            problem.Type ??= defaults.Type;
            problem.Title ??= defaults.Title;
        }

        problem.Extensions.TryAdd("traceId", context.TraceIdentifier);
    }

    /// <summary>Builds the problem of a bare client error status.</summary>
    public static KazoProblemDetails ForStatus(int statusCode) => new() { Status = statusCode };
}

/// <summary>Serializes values with the site's JSON options (<see cref="KazoWebJsonOptions"/>).</summary>
internal static class KazoJsonWriter
{
    /// <summary>Writes <paramref name="value"/> as JSON to <paramref name="body"/>.</summary>
    /// <param name="context">The request (its services hold the site's options).</param>
    /// <param name="body">Where to write.</param>
    /// <param name="value">The value.</param>
    /// <param name="declaredType">The declared type, used when the runtime type has no JSON metadata.</param>
    public static void Write(KazoHttpContext context, Stream body, object? value, Type? declaredType = null)
    {
        JsonSerializerOptions options = context.RequestServices.GetService<KazoWebJsonOptions>()?.SerializerOptions ?? KazoWebJsonOptions.DefaultOptions;
        Type runtimeType = value?.GetType() ?? declaredType ?? typeof(object);
        JsonTypeInfo typeInfo = declaredType is not null && runtimeType != declaredType && !options.TryGetTypeInfo(runtimeType, out _)
            ? options.GetTypeInfo(declaredType)
            : options.GetTypeInfo(runtimeType);
        JsonSerializer.Serialize(body, value, typeInfo);
    }

    /// <summary>Formats a status as <c>404 Not Found</c> (for pages).</summary>
    public static string StatusText(int statusCode) => string.Create(CultureInfo.InvariantCulture, $"{statusCode} {ReasonPhrases.Get(statusCode)}");
}

/// <summary>Standard reason phrases.</summary>
internal static class ReasonPhrases
{
    /// <summary>Gets the reason phrase of <paramref name="statusCode"/>.</summary>
    public static string Get(int statusCode) => statusCode switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        304 => "Not Modified",
        307 => "Temporary Redirect",
        308 => "Permanent Redirect",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        415 => "Unsupported Media Type",
        500 => "Internal Server Error",
        _ => "Status"
    };
}
