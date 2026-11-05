using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Kazo.Os.AppSdk.Web.Antiforgery;
using Kazo.Os.AppSdk.Web.Mvc;
using Kazo.Os.AppSdk.Web.Routing;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Web.Views;

/// <summary>
/// The <c>Html</c> property of a <c>.cshtml</c> page (ASP.NET's <c>IHtmlHelper&lt;TModel&gt;</c>, ADR 0086 §3):
/// raw HTML, links, forms, fields bound to the model, validation messages and partials. It replaces the
/// tag helpers (<c>asp-action</c>, <c>asp-for</c>), which need ASP.NET MVC.
/// </summary>
/// <typeparam name="TModel">The page's model type.</typeparam>
public sealed class KazoHtmlHelper<TModel>
{
    private readonly KazoRazorPage _page;

    internal KazoHtmlHelper(KazoRazorPage page)
    {
        _page = page;
    }

    /// <summary>Gets the page context.</summary>
    public KazoViewContext ViewContext => _page.ViewContext;

    /// <summary>Gets the view data.</summary>
    public KazoViewDataDictionary ViewData => _page.ViewData;

    // ------------------------------------------------------------------------------------------
    // Raw HTML and encoding
    // ------------------------------------------------------------------------------------------

    /// <summary>Writes HTML as is: <c>@Html.Raw(note.BodyHtml)</c>. Never pass text a user typed.</summary>
    /// <param name="html">The HTML.</param>
    public KazoHtmlString Raw(string? html) => new(html);

    /// <summary>Writes the string form of a value as is.</summary>
    /// <param name="value">The value.</param>
    public KazoHtmlString Raw(object? value) => new(Convert.ToString(value, CultureInfo.CurrentCulture));

    /// <summary>HTML-encodes a value (what <c>@value</c> does).</summary>
    /// <param name="value">The value.</param>
    public string Encode(object? value) => KazoHtml.Encode(Convert.ToString(value, CultureInfo.CurrentCulture));

    // ------------------------------------------------------------------------------------------
    // Links and forms
    // ------------------------------------------------------------------------------------------

    /// <summary>A link to an action: <c>@Html.ActionLink("Notes", "Index", "Notes")</c>.</summary>
    /// <param name="linkText">The text (encoded).</param>
    /// <param name="actionName">The action; the current one when <see langword="null"/>.</param>
    /// <param name="controllerName">The controller; the current one when <see langword="null"/>.</param>
    /// <param name="htmlAttributes">Extra attributes (<c>KazoRoute.Values(new { @class = "nav" })</c>).</param>
    /// <exception cref="InvalidOperationException">No route produces the URL.</exception>
    public KazoHtmlString ActionLink(string linkText, string? actionName, string? controllerName = null, IReadOnlyDictionary<string, object?>? htmlAttributes = null) =>
        Anchor(linkText, ActionUrl(actionName, controllerName, null), htmlAttributes);

    /// <summary>A link to an action with route values: <c>@Html.ActionLink("Edit", "Edit", "Notes", new { id = note.Id })</c>.</summary>
    /// <typeparam name="TValues">Type of the route values (an anonymous object, or a dictionary).</typeparam>
    /// <param name="linkText">The text (encoded).</param>
    /// <param name="actionName">The action.</param>
    /// <param name="controllerName">The controller.</param>
    /// <param name="routeValues">The route values.</param>
    /// <param name="htmlAttributes">Extra attributes.</param>
    public KazoHtmlString ActionLink<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TValues>(
        string linkText, string? actionName, string? controllerName, TValues routeValues, IReadOnlyDictionary<string, object?>? htmlAttributes = null) =>
        Anchor(linkText, ActionUrl(actionName, controllerName, KazoRouteValueDictionary.FromObject(routeValues)), htmlAttributes);

    /// <summary>A link to a named route: <c>@Html.RouteLink("Note", "GetNote", new { id = 3 })</c>.</summary>
    /// <typeparam name="TValues">Type of the route values.</typeparam>
    /// <param name="linkText">The text (encoded).</param>
    /// <param name="routeName">The route name.</param>
    /// <param name="routeValues">The route values.</param>
    /// <param name="htmlAttributes">Extra attributes.</param>
    public KazoHtmlString RouteLink<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TValues>(
        string linkText, string routeName, TValues routeValues, IReadOnlyDictionary<string, object?>? htmlAttributes = null)
    {
        string url = ViewContext.Url.RouteUrl(routeName, routeValues)
            ?? throw new InvalidOperationException($"No route named '{routeName}' produces a URL with the given values.");
        return Anchor(linkText, url, htmlAttributes);
    }

    /// <summary>
    /// Opens a form posted to an action, closed by <c>Dispose</c>: <c>@using (Html.BeginForm("Edit", "Notes", new { id = 3 })) { ... }</c>.
    /// A non-<c>GET</c> form ends with the anti-forgery hidden field (as ASP.NET's <c>BeginForm</c>).
    /// </summary>
    /// <param name="actionName">The action; the current one when <see langword="null"/>.</param>
    /// <param name="controllerName">The controller; the current one when <see langword="null"/>.</param>
    /// <param name="method">The method (<c>post</c> by default).</param>
    /// <param name="antiforgery">Whether the anti-forgery field is written (yes by default, not for <c>GET</c>).</param>
    /// <param name="htmlAttributes">Extra attributes.</param>
    /// <returns>The form, which writes <c>&lt;/form&gt;</c> when disposed.</returns>
    public KazoHtmlForm BeginForm(string? actionName = null, string? controllerName = null, string method = "post", bool antiforgery = true, IReadOnlyDictionary<string, object?>? htmlAttributes = null) =>
        OpenForm(ActionUrl(actionName, controllerName, null), method, antiforgery, htmlAttributes);

    /// <summary>Opens a form posted to an action with route values.</summary>
    /// <typeparam name="TValues">Type of the route values.</typeparam>
    /// <param name="actionName">The action.</param>
    /// <param name="controllerName">The controller.</param>
    /// <param name="routeValues">The route values.</param>
    /// <param name="method">The method (<c>post</c> by default).</param>
    /// <param name="antiforgery">Whether the anti-forgery field is written.</param>
    /// <param name="htmlAttributes">Extra attributes.</param>
    public KazoHtmlForm BeginForm<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TValues>(
        string? actionName, string? controllerName, TValues routeValues, string method = "post", bool antiforgery = true, IReadOnlyDictionary<string, object?>? htmlAttributes = null) =>
        OpenForm(ActionUrl(actionName, controllerName, KazoRouteValueDictionary.FromObject(routeValues)), method, antiforgery, htmlAttributes);

    /// <summary>The anti-forgery hidden field, for a hand-written <c>&lt;form&gt;</c>.</summary>
    public KazoHtmlString AntiForgeryToken()
    {
        KazoAntiforgeryTokenSet tokens = ViewContext.GetAntiforgeryTokens();
        return new KazoHtmlString($"<input name=\"{KazoHtml.Encode(tokens.FormFieldName)}\" type=\"hidden\" value=\"{KazoHtml.Encode(tokens.RequestToken)}\" />");
    }

    // ------------------------------------------------------------------------------------------
    // Fields
    // ------------------------------------------------------------------------------------------

    /// <summary>A text input: <c>@Html.TextBox("Title", Model.Title)</c>. After a failed post, the posted value wins.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value.</param>
    /// <param name="htmlAttributes">Extra attributes.</param>
    public KazoHtmlString TextBox(string name, object? value = null, IReadOnlyDictionary<string, object?>? htmlAttributes = null) =>
        Input("text", name, value, htmlAttributes);

    /// <summary>A hidden input.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value.</param>
    /// <param name="htmlAttributes">Extra attributes.</param>
    public KazoHtmlString Hidden(string name, object? value = null, IReadOnlyDictionary<string, object?>? htmlAttributes = null) =>
        Input("hidden", name, value, htmlAttributes);

    /// <summary>A password input (its value is never written back).</summary>
    /// <param name="name">The field name.</param>
    /// <param name="htmlAttributes">Extra attributes.</param>
    public KazoHtmlString Password(string name, IReadOnlyDictionary<string, object?>? htmlAttributes = null) =>
        Element("input", [new("type", "password"), new("name", name), new("id", IdOf(name))], htmlAttributes, content: null, selfClosing: true);

    /// <summary>A text area.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value.</param>
    /// <param name="htmlAttributes">Extra attributes.</param>
    public KazoHtmlString TextArea(string name, string? value = null, IReadOnlyDictionary<string, object?>? htmlAttributes = null) =>
        Element("textarea", [new("name", name), new("id", IdOf(name))], htmlAttributes, KazoHtml.Encode(AttemptedValue(name) ?? value), selfClosing: false);

    /// <summary>A check box (and the hidden <c>false</c> ASP.NET writes so that an unchecked box posts a value).</summary>
    /// <param name="name">The field name.</param>
    /// <param name="isChecked">Whether it is checked.</param>
    /// <param name="htmlAttributes">Extra attributes.</param>
    public KazoHtmlString CheckBox(string name, bool isChecked = false, IReadOnlyDictionary<string, object?>? htmlAttributes = null)
    {
        string? attempted = AttemptedValue(name);
        bool value = attempted is null ? isChecked : attempted.Split(',')[0].Equals("true", StringComparison.OrdinalIgnoreCase);
        List<KeyValuePair<string, object?>> attributes = [new("type", "checkbox"), new("name", name), new("id", IdOf(name)), new("value", "true")];
        if (value)
        {
            attributes.Add(new("checked", "checked"));
        }

        return new KazoHtmlString(Element("input", attributes, htmlAttributes, content: null, selfClosing: true).Value +
            $"<input name=\"{KazoHtml.Encode(name)}\" type=\"hidden\" value=\"false\" />");
    }

    /// <summary>The field name of a model expression: <c>@Html.NameFor(m => m.Address.City)</c> is <c>Address.City</c>.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="expression">A member chain from the model.</param>
    public string NameFor<TValue>(Expression<Func<TModel, TValue>> expression) => KazoModelExpressions.GetName(expression);

    /// <summary>The element id of a model expression (<c>Address_City</c>).</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="expression">A member chain from the model.</param>
    public string IdFor<TValue>(Expression<Func<TModel, TValue>> expression) => IdOf(NameFor(expression));

    /// <summary>The display name of a model expression: <c>[Display(Name = ...)]</c>, or the member name.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="expression">A member chain from the model.</param>
    public string DisplayNameFor<TValue>(Expression<Func<TModel, TValue>> expression) => KazoModelExpressions.GetDisplayName(expression);

    /// <summary>The value of a model expression as text (the posted value after a failed post).</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="expression">A member chain from the model.</param>
    public string ValueFor<TValue>(Expression<Func<TModel, TValue>> expression) =>
        AttemptedValue(NameFor(expression)) ?? Convert.ToString(Evaluate(expression), CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>A text input bound to the model: <c>@Html.TextBoxFor(m => m.Title)</c>.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="expression">A member chain from the model.</param>
    /// <param name="htmlAttributes">Extra attributes.</param>
    public KazoHtmlString TextBoxFor<TValue>(Expression<Func<TModel, TValue>> expression, IReadOnlyDictionary<string, object?>? htmlAttributes = null) =>
        TextBox(NameFor(expression), Evaluate(expression), htmlAttributes);

    /// <summary>A hidden input bound to the model.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="expression">A member chain from the model.</param>
    /// <param name="htmlAttributes">Extra attributes.</param>
    public KazoHtmlString HiddenFor<TValue>(Expression<Func<TModel, TValue>> expression, IReadOnlyDictionary<string, object?>? htmlAttributes = null) =>
        Hidden(NameFor(expression), Evaluate(expression), htmlAttributes);

    /// <summary>A text area bound to the model.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="expression">A member chain from the model.</param>
    /// <param name="htmlAttributes">Extra attributes.</param>
    public KazoHtmlString TextAreaFor<TValue>(Expression<Func<TModel, TValue>> expression, IReadOnlyDictionary<string, object?>? htmlAttributes = null) =>
        TextArea(NameFor(expression), Convert.ToString(Evaluate(expression), CultureInfo.CurrentCulture), htmlAttributes);

    /// <summary>A check box bound to a <see cref="bool"/> of the model.</summary>
    /// <param name="expression">A member chain from the model.</param>
    /// <param name="htmlAttributes">Extra attributes.</param>
    public KazoHtmlString CheckBoxFor(Expression<Func<TModel, bool>> expression, IReadOnlyDictionary<string, object?>? htmlAttributes = null) =>
        CheckBox(NameFor(expression), Evaluate(expression) is true, htmlAttributes);

    /// <summary>A <c>&lt;label for&gt;</c> with the display name of a model expression.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="expression">A member chain from the model.</param>
    /// <param name="labelText">The text; the display name when <see langword="null"/>.</param>
    public KazoHtmlString LabelFor<TValue>(Expression<Func<TModel, TValue>> expression, string? labelText = null) =>
        new($"<label for=\"{KazoHtml.Encode(IdFor(expression))}\">{KazoHtml.Encode(labelText ?? DisplayNameFor(expression))}</label>");

    // ------------------------------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The first error of a field, in a <c>&lt;span&gt;</c> (class <c>field-validation-error</c>, or an empty
    /// <c>field-validation-valid</c>), as <see cref="KazoValidationMessage"/> writes it.
    /// </summary>
    /// <param name="modelName">The field key (<c>Title</c>).</param>
    /// <param name="message">A message replacing the error's own.</param>
    public KazoHtmlString ValidationMessage(string modelName, string? message = null)
    {
        string? error = ViewContext.ModelState[modelName]?.Errors.FirstOrDefault();
        return new KazoHtmlString(
            $"<span class=\"{(error is null ? "field-validation-valid" : "field-validation-error")}\" data-valmsg-for=\"{KazoHtml.Encode(modelName)}\">" +
            $"{(error is null ? string.Empty : KazoHtml.Encode(message ?? error))}</span>");
    }

    /// <summary>The first error of the field of a model expression.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="expression">A member chain from the model.</param>
    /// <param name="message">A message replacing the error's own.</param>
    public KazoHtmlString ValidationMessageFor<TValue>(Expression<Func<TModel, TValue>> expression, string? message = null) =>
        ValidationMessage(NameFor(expression), message);

    /// <summary>Every error of the model state as a list; nothing when the model is valid.</summary>
    /// <param name="excludePropertyErrors">Whether only the model-level errors (empty key) are listed.</param>
    public KazoHtmlString ValidationSummary(bool excludePropertyErrors = false)
    {
        KazoModelStateDictionary modelState = ViewContext.ModelState;
        string[] errors = [.. modelState.Keys
            .Where(key => !excludePropertyErrors || key.Length == 0)
            .SelectMany(key => modelState[key]!.Errors)];
        if (errors.Length == 0)
        {
            return KazoHtmlString.Empty;
        }

        StringBuilder html = new("<div class=\"validation-summary-errors\"><ul>");
        foreach (string error in errors)
        {
            html.Append("<li>").Append(KazoHtml.Encode(error)).Append("</li>");
        }

        return new KazoHtmlString(html.Append("</ul></div>").ToString());
    }

    // ------------------------------------------------------------------------------------------
    // Partials
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Renders a partial view (<c>.cshtml</c> or <c>.razor</c>), from the controller's folder then
    /// <c>Shared/</c>, with the page's model: <c>@await Html.PartialAsync("_NoteCard")</c>.
    /// </summary>
    /// <param name="partialViewName">The view name or path.</param>
    public Task<KazoHtmlString> PartialAsync(string partialViewName) => PartialAsync(partialViewName, ViewContext.Model);

    /// <summary>Renders a partial view with its own model: <c>@await Html.PartialAsync("_NoteCard", note)</c>.</summary>
    /// <param name="partialViewName">The view name or path.</param>
    /// <param name="model">The partial's model.</param>
    public async Task<KazoHtmlString> PartialAsync(string partialViewName, object? model) =>
        new(await KazoViewEngine.RenderPartialAsync(ViewContext, partialViewName, model).ConfigureAwait(true));

    /// <summary>Writes a partial view straight to the page: <c>@{ await Html.RenderPartialAsync("_NoteCard", note); }</c>.</summary>
    /// <param name="partialViewName">The view name or path.</param>
    /// <param name="model">The partial's model; the page's model when not given.</param>
    public async Task RenderPartialAsync(string partialViewName, object? model = null)
    {
        KazoHtmlString html = await PartialAsync(partialViewName, model ?? ViewContext.Model).ConfigureAwait(true);
        html.WriteTo(_page.Output);
    }

    // ------------------------------------------------------------------------------------------

    private string ActionUrl(string? action, string? controller, KazoRouteValueDictionary? values) =>
        ViewContext.Url.Action(action, controller, values ?? [])
            ?? throw new InvalidOperationException(
                $"No route produces a URL for the action '{action ?? ViewContext.ActionDescriptor?.ActionName}' of controller '{controller ?? ViewContext.ActionDescriptor?.ControllerName}'.");

    private static KazoHtmlString Anchor(string text, string href, IReadOnlyDictionary<string, object?>? htmlAttributes) =>
        Element("a", [new("href", href)], htmlAttributes, KazoHtml.Encode(text), selfClosing: false);

    private KazoHtmlForm OpenForm(string action, string method, bool antiforgery, IReadOnlyDictionary<string, object?>? htmlAttributes)
    {
        string start = Element("form", [new("action", action), new("method", method)], htmlAttributes, content: null, selfClosing: false).Value;
        _page.Output.Write(start[..^"</form>".Length]);
        string end = antiforgery && !string.Equals(method, "get", StringComparison.OrdinalIgnoreCase)
            ? AntiForgeryToken().Value + "</form>"
            : "</form>";
        return new KazoHtmlForm(_page, end);
    }

    private KazoHtmlString Input(string type, string name, object? value, IReadOnlyDictionary<string, object?>? htmlAttributes)
    {
        List<KeyValuePair<string, object?>> attributes = [new("type", type), new("name", name), new("id", IdOf(name))];
        string? text = AttemptedValue(name) ?? Convert.ToString(value, CultureInfo.CurrentCulture);
        attributes.Add(new("value", text ?? string.Empty));
        if (ViewContext.ModelState[name]?.Errors.Count > 0)
        {
            attributes.Add(new("class", "input-validation-error"));
        }

        return Element("input", attributes, htmlAttributes, content: null, selfClosing: true);
    }

    /// <summary>The value the user posted for a field that failed validation, which a re-displayed form shows again.</summary>
    private string? AttemptedValue(string name) =>
        ViewContext.ModelState[name] is { Errors.Count: > 0 } && ViewContext.HttpContext.Request.HasFormContentType
            ? ViewContext.HttpContext.Request.Form[name]
            : null;

    private static string IdOf(string name) => name.Replace('.', '_').Replace('[', '_').Replace(']', '_');

    private object? Evaluate<TValue>(Expression<Func<TModel, TValue>> expression)
    {
        try
        {
            return _page.ViewContext.Model is TModel model ? expression.Compile(preferInterpretation: true)(model) : null;
        }
        catch (NullReferenceException)
        {
            // m => m.Address.City with no Address: an empty field, as in ASP.NET.
            return null;
        }
    }

    private static KazoHtmlString Element(
        string tag,
        IEnumerable<KeyValuePair<string, object?>> attributes,
        IReadOnlyDictionary<string, object?>? htmlAttributes,
        string? content,
        bool selfClosing)
    {
        // Extra attributes override the generated ones, except class, which adds to it (input-validation-error stays).
        Dictionary<string, string> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, object?> attribute in attributes)
        {
            merged[attribute.Key] = Convert.ToString(attribute.Value, CultureInfo.CurrentCulture) ?? string.Empty;
        }

        foreach (KeyValuePair<string, object?> attribute in htmlAttributes ?? new Dictionary<string, object?>())
        {
            string key = attribute.Key.Replace('_', '-');
            string value = Convert.ToString(attribute.Value, CultureInfo.CurrentCulture) ?? string.Empty;
            merged[key] = key.Equals("class", StringComparison.OrdinalIgnoreCase) && merged.TryGetValue(key, out string? existing)
                ? $"{existing} {value}"
                : value;
        }

        StringBuilder html = new($"<{tag}");
        foreach (KeyValuePair<string, string> attribute in merged)
        {
            html.Append(' ').Append(attribute.Key).Append("=\"").Append(KazoHtml.Encode(attribute.Value)).Append('"');
        }

        html.Append(selfClosing ? " />" : $">{content}</{tag}>");
        return new KazoHtmlString(html.ToString());
    }
}

/// <summary>A form opened by <c>Html.BeginForm</c>: disposing it writes the anti-forgery field and <c>&lt;/form&gt;</c>.</summary>
public sealed class KazoHtmlForm : IDisposable
{
    private readonly KazoRazorPage _page;
    private readonly string _end;
    private bool _disposed;

    internal KazoHtmlForm(KazoRazorPage page, string end)
    {
        _page = page;
        _end = end;
    }

    /// <summary>Closes the form.</summary>
    public void EndForm() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _page.Output.Write(_end);
        }
    }
}

/// <summary>The <c>Json</c> property of a <c>.cshtml</c> page: a value as JSON for a script (<c>var notes = @Json.Serialize(Model)</c>).</summary>
public sealed class KazoJsonHelper
{
    private readonly KazoRazorPage _page;

    internal KazoJsonHelper(KazoRazorPage page)
    {
        _page = page;
    }

    /// <summary>Serializes a value with the site's JSON options (camelCase); <c>&lt;</c> is escaped, so the JSON is safe in a <c>&lt;script&gt;</c>.</summary>
    /// <param name="value">The value.</param>
    public KazoHtmlString Serialize(object? value)
    {
        using MemoryStream stream = new();
        KazoJsonWriter.Write(_page.Context, stream, value);
        return new KazoHtmlString(Encoding.UTF8.GetString(stream.ToArray()));
    }
}

/// <summary>
/// The <c>Component</c> property of a <c>.cshtml</c> page (ADR 0086 §6): renders a Razor component in the
/// page, with the page's context cascaded, so the <c>.razor</c> helpers of ADR 0083 work in a <c>.cshtml</c>:
/// <c>@await Component.InvokeAsync&lt;KazoLink&gt;(new Dictionary&lt;string, object?&gt; { ["Action"] = "Index" })</c>.
/// </summary>
public sealed class KazoViewComponentHelper
{
    private readonly KazoRazorPage _page;

    internal KazoViewComponentHelper(KazoRazorPage page)
    {
        _page = page;
    }

    /// <summary>Renders a component to HTML.</summary>
    /// <typeparam name="TComponent">The component.</typeparam>
    /// <param name="parameters">Its parameters by name.</param>
    public async Task<KazoHtmlString> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(IReadOnlyDictionary<string, object?>? parameters = null)
        where TComponent : IComponent =>
        new(await KazoViewEngine.RenderComponentAsync(_page.ViewContext, typeof(TComponent), parameters).ConfigureAwait(true));
}

/// <summary>The <c>ModelExpressionProvider</c> property of a <c>.cshtml</c> page: the field name of a model expression.</summary>
/// <typeparam name="TModel">The page's model type.</typeparam>
public sealed class KazoModelExpressionProvider<TModel>
{
    /// <summary>Gets the field name of a member chain (<c>Address.City</c>).</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="expression">A member chain from the model.</param>
    public string GetExpressionText<TValue>(Expression<Func<TModel, TValue>> expression) => KazoModelExpressions.GetName(expression);
}

/// <summary>Reads member chains (<c>m =&gt; m.Address.City</c>) as ASP.NET's <c>ExpressionHelper</c> does.</summary>
internal static class KazoModelExpressions
{
    public static string GetName(LambdaExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        Stack<string> names = new();
        Expression? current = expression.Body;
        if (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            current = convert.Operand;
        }

        while (current is MemberExpression member)
        {
            names.Push(member.Member.Name);
            current = member.Expression;
        }

        return current is ParameterExpression
            ? string.Join('.', names)
            : throw new InvalidOperationException($"Templates can be used only with field access, property access, single-dimension array index, or single-parameter custom indexer expressions: '{expression}'.");
    }

    public static string GetDisplayName(LambdaExpression expression)
    {
        string name = GetName(expression);
        Expression body = expression.Body is UnaryExpression unary ? unary.Operand : expression.Body;
        MemberInfo? member = (body as MemberExpression)?.Member;
        DisplayAttribute? display = member?.GetCustomAttribute<DisplayAttribute>();
        return display?.GetName() ?? member?.Name ?? name;
    }
}
