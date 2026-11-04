using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Kazo.Os.AppSdk.Web.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.AppSdk.Web.Mvc;

/// <summary>Converts the text of a route value, query field, form field or header into <typeparamref name="T"/>.</summary>
/// <typeparam name="T">Target type.</typeparam>
/// <param name="text">The text.</param>
/// <param name="value">The value, when the text is valid.</param>
/// <returns><see langword="true"/> when the text is valid.</returns>
public delegate bool HackValueParser<T>(string text, out T value);

/// <summary>The parsers the generated binding code uses (trim-safe: no <c>TypeConverter</c>, no reflection).</summary>
public static class HackValueParsers
{
    /// <summary>Gets the parser of <see cref="string"/> (any text).</summary>
    public static HackValueParser<string> String { get; } = static (string text, out string value) =>
    {
        value = text;
        return true;
    };

    /// <summary>Gets the parser of a type with <c>TryParse</c> (<see cref="IParsable{TSelf}"/>), in the invariant culture.</summary>
    /// <typeparam name="T">An <see cref="IParsable{TSelf}"/> type: numbers, <see cref="Guid"/>, dates, <see cref="bool"/>...</typeparam>
    public static HackValueParser<T> Parsable<T>() where T : IParsable<T> => ParsableCache<T>.Parser;

    /// <summary>Gets the parser of an enum: a member name (without case) or its number.</summary>
    /// <typeparam name="T">The enum.</typeparam>
    public static HackValueParser<T> Enum<T>() where T : struct, Enum => EnumCache<T>.Parser;

    /// <summary>Gets the parser of <c>T?</c>: empty text is <see langword="null"/>.</summary>
    /// <typeparam name="T">The underlying value type.</typeparam>
    /// <param name="inner">The parser of <typeparamref name="T"/>.</param>
    public static HackValueParser<T?> Nullable<T>(HackValueParser<T> inner) where T : struct =>
        (string text, out T? value) =>
        {
            if (text.Length == 0)
            {
                value = null;
                return true;
            }

            bool parsed = inner(text, out T result);
            value = parsed ? result : null;
            return parsed;
        };

    private static class ParsableCache<T> where T : IParsable<T>
    {
        public static readonly HackValueParser<T> Parser = static (string text, out T value) =>
        {
            // An HTML checkbox posts "on"; the hidden companion field of a Razor form posts "false".
            if (typeof(T) == typeof(bool) && text.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                return T.TryParse("true", CultureInfo.InvariantCulture, out value!);
            }

            return T.TryParse(text.Trim(), CultureInfo.InvariantCulture, out value!);
        };
    }

    private static class EnumCache<T> where T : struct, Enum
    {
        public static readonly HackValueParser<T> Parser = static (string text, out T value) =>
            System.Enum.TryParse(text.Trim(), ignoreCase: true, out value)
            && (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) || System.Enum.IsDefined(value));
    }
}

/// <summary>Where <see cref="HackModelBinder"/> looks for a simple value.</summary>
[Flags]
public enum HackValueSources
{
    /// <summary>Nowhere.</summary>
    None = 0,

    /// <summary>The form.</summary>
    Form = 1,

    /// <summary>The route values.</summary>
    Route = 2,

    /// <summary>The query string.</summary>
    Query = 4,

    /// <summary>The request headers.</summary>
    Header = 8,

    /// <summary>ASP.NET's default order: form, then route, then query.</summary>
    Default = Form | Route | Query
}

/// <summary>
/// Binds action arguments from the request (ADR 0076 §4). The route generator writes, for each action,
/// calls such as <c>binder.BindValue("id", HackValueSources.Default, HackValueParsers.Parsable&lt;int&gt;(), ...)</c>;
/// conversion and validation failures go to <see cref="ModelState"/> as in ASP.NET MVC.
/// </summary>
public sealed class HackModelBinder
{
    private readonly JsonSerializerOptions _json;

    internal HackModelBinder(HackActionContext context, JsonSerializerOptions json)
    {
        Context = context;
        _json = json;
    }

    /// <summary>Gets the action context.</summary>
    public HackActionContext Context { get; }

    /// <summary>Gets the request.</summary>
    public HackHttpContext HttpContext => Context.HttpContext;

    /// <summary>Gets the model state that receives the errors.</summary>
    public HackModelStateDictionary ModelState => Context.ModelState;

    /// <summary>Gets whether a <c>[FromBody]</c> parameter met a body that is not JSON (<c>415</c>).</summary>
    public bool UnsupportedMediaType { get; private set; }

    /// <summary>Finds the raw text of <paramref name="name"/> in <paramref name="sources"/>.</summary>
    /// <param name="name">Field, route value or header name.</param>
    /// <param name="sources">Where to look, in the order form, route, query, header.</param>
    /// <param name="text">The first value found.</param>
    /// <returns><see langword="true"/> when the value is present (possibly empty).</returns>
    public bool TryGetRawValue(string name, HackValueSources sources, [NotNullWhen(true)] out string? text)
    {
        HackHttpRequest request = HttpContext.Request;
        if (sources.HasFlag(HackValueSources.Form) && request.Form.GetValues(name) is { Count: > 0 } form)
        {
            text = form[0];
            return true;
        }

        if (sources.HasFlag(HackValueSources.Route) && request.RouteValues.TryGetValue(name, out object? route) && route is not null)
        {
            text = Routing.HackRouteValueDictionary.FormatValue(route)!;
            return true;
        }

        if (sources.HasFlag(HackValueSources.Query) && request.Query.GetValues(name) is { Count: > 0 } query)
        {
            text = query[0];
            return true;
        }

        if (sources.HasFlag(HackValueSources.Header) && request.Headers[name] is string header)
        {
            text = header;
            return true;
        }

        text = null;
        return false;
    }

    /// <summary>Binds a simple value; missing gives <paramref name="defaultValue"/>, invalid text an error.</summary>
    /// <typeparam name="T">Parameter type.</typeparam>
    /// <param name="name">Field name, also the model state key.</param>
    /// <param name="sources">Where to look.</param>
    /// <param name="parser">How to convert the text.</param>
    /// <param name="isRequired">Whether a missing value is an error (a non-nullable reference type, as ASP.NET with nullable enabled).</param>
    /// <param name="defaultValue">The value when missing (the C# default value of the parameter, or <c>default</c>).</param>
    /// <returns>The value.</returns>
    public T BindValue<T>(string name, HackValueSources sources, HackValueParser<T> parser, bool isRequired, T defaultValue)
    {
        ArgumentNullException.ThrowIfNull(parser);
        if (!TryGetRawValue(name, sources, out string? text) || (text.Length == 0 && default(T) is not null && typeof(T) != typeof(string)))
        {
            if (isRequired)
            {
                ModelState.AddModelError(name, $"The {name} field is required.");
            }

            return defaultValue;
        }

        ModelState.SetAttemptedValue(name, text);
        if (parser(text, out T value))
        {
            if (isRequired && value is string { Length: 0 })
            {
                ModelState.AddModelError(name, $"The {name} field is required.");
            }

            return value;
        }

        ModelState.AddModelError(name, $"The value '{text}' is not valid for {name}.");
        return defaultValue;
    }

    /// <summary>Binds every value of a repeated field (<c>?tag=a&amp;tag=b</c>) into an array.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="name">Field name.</param>
    /// <param name="sources">Where to look (form and query carry repeated fields; a route value gives one).</param>
    /// <param name="parser">How to convert each text.</param>
    public T[] BindArray<T>(string name, HackValueSources sources, HackValueParser<T> parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        HackHttpRequest request = HttpContext.Request;
        IReadOnlyList<string> texts = sources.HasFlag(HackValueSources.Form) && request.Form.ContainsKey(name) ? request.Form.GetValues(name)
            : sources.HasFlag(HackValueSources.Query) && request.Query.ContainsKey(name) ? request.Query.GetValues(name)
            : TryGetRawValue(name, sources & (HackValueSources.Route | HackValueSources.Header), out string? single) ? [.. single.Split(',')]
            : [];
        List<T> values = [];
        foreach (string text in texts)
        {
            if (parser(text, out T value))
            {
                values.Add(value);
            }
            else
            {
                ModelState.AddModelError(name, $"The value '{text}' is not valid for {name}.");
            }
        }

        return [.. values];
    }

    /// <summary>Reads one property of a complex model; a missing field leaves the property as it is.</summary>
    /// <typeparam name="T">Property type.</typeparam>
    /// <param name="name">Field name (the property name), also the model state key.</param>
    /// <param name="sources">Where to look.</param>
    /// <param name="parser">How to convert the text.</param>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when a valid value was found.</returns>
    public bool TryBindProperty<T>(string name, HackValueSources sources, HackValueParser<T> parser, out T value)
    {
        ArgumentNullException.ThrowIfNull(parser);
        value = default!;
        if (!TryGetRawValue(name, sources, out string? text))
        {
            return false;
        }

        ModelState.SetAttemptedValue(name, text);
        if (text.Length == 0 && default(T) is not null)
        {
            // An empty field for a value type: leave the default and let [Required] speak.
            return false;
        }

        if (parser(text, out value))
        {
            return true;
        }

        ModelState.AddModelError(name, $"The value '{text}' is not valid for {name}.");
        return false;
    }

    /// <summary>Reads the JSON body (<c>[FromBody]</c>) with the site's JSON options.</summary>
    /// <typeparam name="T">Parameter type.</typeparam>
    /// <param name="name">Parameter name.</param>
    /// <param name="isRequired">Whether an empty body is an error (a non-nullable parameter).</param>
    /// <returns>The value, or <c>default</c> on error.</returns>
    public T? BindBody<T>(string name, bool isRequired)
    {
        HackHttpRequest request = HttpContext.Request;
        if (request.ContentLength == 0)
        {
            if (isRequired)
            {
                ModelState.AddModelError(string.Empty, "A non-empty request body is required.");
            }

            return default;
        }

        if (!request.HasJsonContentType)
        {
            UnsupportedMediaType = true;
            return default;
        }

        try
        {
            JsonTypeInfo<T> typeInfo = (JsonTypeInfo<T>)_json.GetTypeInfo(typeof(T));
            T? value = JsonSerializer.Deserialize(request.BodyBytes.Span, typeInfo);
            if (value is null && isRequired)
            {
                ModelState.AddModelError(string.Empty, "A non-empty request body is required.");
            }

            return value;
        }
        catch (JsonException exception)
        {
            // ASP.NET: the JSON path is the key ("$.title"), the message says what was expected.
            ModelState.AddModelError(exception.Path ?? "$", $"The JSON value could not be converted to {typeof(T).Name}. {exception.Message}");
            ModelState.AddModelError(name, $"The {name} field is required.");
            return default;
        }
    }

    /// <summary>Takes a parameter from the request's services.</summary>
    /// <typeparam name="T">Service type.</typeparam>
    public T BindService<T>() where T : notnull => HttpContext.RequestServices.GetRequiredService<T>();

    /// <summary>
    /// Validates a model with its <c>DataAnnotations</c> attributes and <see cref="IValidatableObject"/>
    /// (top level, like <c>Validator.TryValidateObject</c>); errors are keyed by property name.
    /// </summary>
    /// <typeparam name="T">The model type; its public properties are kept by the trimmer.</typeparam>
    /// <param name="value">The model, or <see langword="null"/> (nothing to validate).</param>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "T is annotated with PublicProperties: the trimmer keeps the properties and their validation attributes that Validator reads.")]
    public void Validate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T? value)
    {
        if (value is null)
        {
            return;
        }

        ValidationContext validation = new(value, HttpContext.RequestServices, null);
        List<ValidationResult> results = [];
        Validator.TryValidateObject(value, validation, results, validateAllProperties: true);
        AddResults(results, string.Empty);
    }

    /// <summary>Validates a parameter's own attributes (<c>[Range(1, 10)] int page</c>).</summary>
    /// <param name="name">Parameter name, also the model state key.</param>
    /// <param name="value">The bound value.</param>
    /// <param name="attributes">The attributes, as the generator rebuilt them.</param>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "TryValidateValue only runs the attributes given; the context's instance is the value itself and its members are not read.")]
    public void ValidateValue(string name, object? value, params ValidationAttribute[] attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (attributes.Length == 0 || ModelState[name]?.Errors.Count > 0)
        {
            // A value that did not convert already has its error; ASP.NET does not validate it again.
            return;
        }

        ValidationContext validation = new(value ?? name, HttpContext.RequestServices, null) { MemberName = name, DisplayName = name };
        List<ValidationResult> results = [];
        Validator.TryValidateValue(value!, validation, results, attributes);
        AddResults(results, name);
    }

    private void AddResults(List<ValidationResult> results, string fallbackKey)
    {
        foreach (ValidationResult result in results)
        {
            string message = result.ErrorMessage ?? "The value is invalid.";
            string[] members = [.. result.MemberNames];
            if (members.Length == 0)
            {
                ModelState.AddModelError(fallbackKey, message);
                continue;
            }

            foreach (string member in members)
            {
                ModelState.AddModelError(member, message);
            }
        }
    }
}
