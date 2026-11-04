using System.Diagnostics.CodeAnalysis;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.AppSdk.Web.Views;

/// <summary>
/// Renders a view as the response (ASP.NET's <c>ViewResult</c> and <c>PartialViewResult</c>, ADR 0083 §4):
/// <c>text/html; charset=utf-8</c>, status 200 unless set. The view is named (<see cref="ViewName"/>,
/// looked up by the engine) or typed (<see cref="ViewType"/>, <c>View&lt;NoteListView&gt;(model)</c>).
/// </summary>
public sealed class KazoViewResult : IKazoActionResult
{
    /// <summary>Gets or sets the view name or path; the action name when <see langword="null"/> and <see cref="ViewType"/> is not set.</summary>
    public string? ViewName { get; init; }

    /// <summary>Gets or sets the view component; wins over <see cref="ViewName"/>.</summary>
    [DynamicallyAccessedMembers(KazoViewTypes.Component)]
    public Type? ViewType { get; init; }

    /// <summary>Gets or sets the model.</summary>
    public object? Model { get; init; }

    /// <summary>Gets or sets the view data; the controller's when <see langword="null"/>.</summary>
    public KazoViewDataDictionary? ViewData { get; init; }

    /// <summary>Gets or sets whether the view renders without layout (<c>PartialView()</c>).</summary>
    public bool IsPartial { get; init; }

    /// <summary>Gets or sets the status code (200 by default; <c>400</c> for a form shown again with errors, for instance).</summary>
    public int? StatusCode { get; init; }

    /// <summary>Gets or sets the content type (<c>text/html; charset=utf-8</c> by default).</summary>
    public string? ContentType { get; init; }

    /// <inheritdoc />
    public async Task ExecuteResultAsync(KazoActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        KazoViewEngine engine = context.HttpContext.RequestServices.GetRequiredService<KazoViewEngine>();
        Type viewType = ViewType ?? engine.GetView(
            context,
            ViewName ?? context.ActionDescriptor?.ActionName ?? context.RouteValues.GetString("action")
                ?? throw new InvalidOperationException("View() needs a view name outside a controller action.")).ViewType;
        string html = await engine.RenderAsync(context, viewType, Model, ViewData ?? new KazoViewDataDictionary(), IsPartial).ConfigureAwait(false);
        KazoHttpResponse response = context.HttpContext.Response;
        response.StatusCode = StatusCode ?? 200;
        response.ContentType = ContentType ?? "text/html; charset=utf-8";
        await response.WriteAsync(html, context.HttpContext.RequestAborted).ConfigureAwait(false);
    }
}
