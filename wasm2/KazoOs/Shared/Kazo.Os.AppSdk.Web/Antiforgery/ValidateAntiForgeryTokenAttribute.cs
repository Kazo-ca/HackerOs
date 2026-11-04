using Kazo.Os.AppSdk.Web.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.AppSdk.Web.Antiforgery;

/// <summary>
/// Refuses a request whose anti-forgery token is missing or wrong, with <c>400 Bad Request</c>, before
/// the action runs (ASP.NET's <c>[ValidateAntiForgeryToken]</c>, ADR 0076 §6). Put it on the <c>POST</c>
/// actions that receive a <see cref="Views.KazoForm"/>; <c>GET</c>, <c>HEAD</c>, <c>OPTIONS</c> and
/// <c>TRACE</c> are never checked.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ValidateAntiForgeryTokenAttribute : KazoActionFilterAttribute
{
    /// <inheritdoc />
    public override void OnActionExecuting(KazoActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string method = context.HttpContext.Request.Method;
        if (method is "GET" or "HEAD" or "OPTIONS" or "TRACE" || context.HttpContext.Items.ContainsKey(typeof(IgnoreAntiforgeryTokenAttribute)))
        {
            return;
        }

        KazoAntiforgery antiforgery = context.HttpContext.RequestServices.GetRequiredService<KazoAntiforgery>();
        if (!antiforgery.IsRequestValid(context.HttpContext, out string? reason))
        {
            context.Result = new KazoAntiforgeryValidationFailedResult(reason ?? "The anti-forgery token is invalid.");
        }
    }
}

/// <summary>Skips the anti-forgery check of a controller marked <see cref="ValidateAntiForgeryTokenAttribute"/> for one action (ASP.NET's <c>[IgnoreAntiforgeryToken]</c>).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class IgnoreAntiforgeryTokenAttribute : KazoActionFilterAttribute
{
    /// <summary>Initializes the attribute; it runs first so it can mark the request.</summary>
    public IgnoreAntiforgeryTokenAttribute() => Order = int.MinValue;

    /// <inheritdoc />
    public override void OnActionExecuting(KazoActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.HttpContext.Items[typeof(IgnoreAntiforgeryTokenAttribute)] = true;
    }
}

/// <summary>The <c>400</c> an invalid anti-forgery token gets: plain text with the reason.</summary>
public sealed class KazoAntiforgeryValidationFailedResult(string reason) : KazoStatusCodeResult(400)
{
    /// <summary>Gets why the token was refused.</summary>
    public string Reason { get; } = reason;

    /// <inheritdoc />
    public override Task ExecuteResultAsync(KazoActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.HttpContext.Response.StatusCode = 400;
        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        return context.HttpContext.Response.WriteAsync($"400 Bad Request: {Reason}", context.HttpContext.RequestAborted);
    }
}
