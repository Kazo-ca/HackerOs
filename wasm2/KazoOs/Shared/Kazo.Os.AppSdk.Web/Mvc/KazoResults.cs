using System.Text.Json.Serialization.Metadata;

namespace Kazo.Os.AppSdk.Web.Mvc;

/// <summary>
/// Results for minimal routes (<c>app.MapGet("/health", () =&gt; KazoResults.Ok("up"))</c>), like
/// ASP.NET Core's <c>Results</c>. They are the same result types controllers return.
/// </summary>
public static class KazoResults
{
    /// <summary><c>200 OK</c> with no body.</summary>
    public static IKazoActionResult Ok() => new KazoStatusCodeResult(200);

    /// <summary><c>200 OK</c> with a value (JSON, or text for a string).</summary>
    /// <param name="value">The value.</param>
    public static IKazoActionResult Ok(object? value) => new KazoObjectResult(value, 200);

    /// <summary>Text, <c>text/plain; charset=utf-8</c> unless <paramref name="contentType"/> says otherwise.</summary>
    /// <param name="content">The text.</param>
    /// <param name="contentType">Content type.</param>
    /// <param name="statusCode">Status code (200 by default).</param>
    public static IKazoActionResult Text(string content, string? contentType = null, int? statusCode = null) =>
        new KazoContentResult { Content = content, ContentType = contentType, StatusCode = statusCode };

    /// <summary>JSON with the site's options.</summary>
    /// <param name="value">The value.</param>
    /// <param name="statusCode">Status code (200 by default).</param>
    public static IKazoActionResult Json(object? value, int? statusCode = null) => new KazoJsonResult(value) { StatusCode = statusCode };

    /// <summary>JSON with explicit metadata (trim-safe).</summary>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">Metadata of its type.</param>
    /// <param name="statusCode">Status code (200 by default).</param>
    public static IKazoActionResult Json(object? value, JsonTypeInfo typeInfo, int? statusCode = null) =>
        new KazoJsonResult(value, typeInfo) { StatusCode = statusCode };

    /// <summary>A status code with no body.</summary>
    /// <param name="statusCode">The status code.</param>
    public static IKazoActionResult StatusCode(int statusCode) => new KazoStatusCodeResult(statusCode);

    /// <summary><c>204 No Content</c>.</summary>
    public static IKazoActionResult NoContent() => new KazoStatusCodeResult(204);

    /// <summary><c>404 Not Found</c>.</summary>
    public static IKazoActionResult NotFound() => new KazoStatusCodeResult(404);

    /// <summary><c>400 Bad Request</c>.</summary>
    public static IKazoActionResult BadRequest() => new KazoStatusCodeResult(400);

    /// <summary><c>201 Created</c>.</summary>
    /// <param name="uri">URL of the new resource.</param>
    /// <param name="value">The created value.</param>
    public static IKazoActionResult Created(string? uri, object? value) => new KazoCreatedResult(uri, value);

    /// <summary><c>302 Found</c>, or <c>301</c> when permanent.</summary>
    /// <param name="url">Target URL.</param>
    /// <param name="permanent">Whether the redirection is permanent.</param>
    public static IKazoActionResult Redirect(string url, bool permanent = false) => new KazoRedirectResult(url, permanent);

    /// <summary>Bytes to show or download.</summary>
    /// <param name="contents">The bytes.</param>
    /// <param name="contentType">Their type.</param>
    /// <param name="fileDownloadName">Download name, or <see langword="null"/>.</param>
    public static IKazoActionResult File(byte[] contents, string contentType, string? fileDownloadName = null) =>
        new KazoFileResult(contents, contentType) { FileDownloadName = fileDownloadName };

    /// <summary>An RFC 9457 problem.</summary>
    /// <param name="detail">Explanation.</param>
    /// <param name="statusCode">Status code (500 by default).</param>
    /// <param name="title">Summary.</param>
    public static IKazoActionResult Problem(string? detail = null, int? statusCode = null, string? title = null) =>
        new KazoProblemResult(new KazoProblemDetails { Detail = detail, Status = statusCode ?? 500, Title = title });
}
