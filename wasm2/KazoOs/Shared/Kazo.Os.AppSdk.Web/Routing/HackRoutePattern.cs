using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kazo.Os.AppSdk.Web.Routing;

/// <summary>
/// A <see cref="HackRouteTemplate"/> ready to match request paths and to generate links, with the
/// "required values" a conventional route carries for one action (<c>controller=Notes, action=Edit</c>),
/// as ASP.NET Core's <c>RoutePattern</c>.
/// </summary>
public sealed class HackRoutePattern
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private readonly Dictionary<HackRouteConstraint, Regex> _regexes = [];

    /// <summary>Initializes a pattern.</summary>
    /// <param name="template">The parsed template.</param>
    /// <param name="requiredValues">Values the route must produce (and that links must match), e.g. controller and action.</param>
    public HackRoutePattern(HackRouteTemplate template, IReadOnlyDictionary<string, string>? requiredValues = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        Template = template;
        RequiredValues = requiredValues is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(requiredValues, StringComparer.OrdinalIgnoreCase);
        Precedence = template.GetPrecedence();
        foreach (HackRouteSegment segment in template.Segments)
        {
            foreach (HackRouteConstraint constraint in segment.Parameter?.Constraints ?? [])
            {
                if (constraint.Name == "regex")
                {
                    _regexes[constraint] = new Regex(constraint.Argument!, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);
                }
            }
        }
    }

    /// <summary>Parses <paramref name="template"/> into a pattern.</summary>
    /// <param name="template">Template text.</param>
    /// <param name="requiredValues">Optional required values.</param>
    /// <exception cref="FormatException">The template is malformed.</exception>
    public static HackRoutePattern Parse(string template, IReadOnlyDictionary<string, string>? requiredValues = null) =>
        new(HackRouteTemplate.Parse(template), requiredValues);

    /// <summary>Gets the template.</summary>
    public HackRouteTemplate Template { get; }

    /// <summary>Gets the required values.</summary>
    public IReadOnlyDictionary<string, string> RequiredValues { get; }

    /// <summary>Gets the precedence of each segment (see <see cref="HackRouteTemplate.GetPrecedence"/>).</summary>
    public IReadOnlyList<int> Precedence { get; }

    /// <summary>Gets the template text, with a leading <c>/</c>.</summary>
    public string RawText => "/" + Template.Text;

    /// <summary>Splits a decoded request path into segments (<c>/notes/5/</c> gives <c>notes</c>, <c>5</c>).</summary>
    /// <param name="path">Decoded path.</param>
    public static string[] SplitPath(string path)
    {
        string trimmed = (path ?? string.Empty).Trim('/');
        return trimmed.Length == 0 ? [] : trimmed.Split('/');
    }

    /// <summary>Matches path segments; on success, returns the route values (parameters, defaults, required values).</summary>
    /// <param name="segments">Segments from <see cref="SplitPath"/>.</param>
    /// <param name="values">The route values.</param>
    /// <returns><see langword="true"/> when the path matches.</returns>
    public bool TryMatch(IReadOnlyList<string> segments, out HackRouteValueDictionary values)
    {
        values = [];
        int position = 0;
        foreach (HackRouteSegment segment in Template.Segments)
        {
            if (segment.Literal is string literal)
            {
                if (position >= segments.Count || !segments[position].Equals(literal, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                position++;
                continue;
            }

            HackRouteParameter parameter = segment.Parameter!;
            if (parameter.IsCatchAll)
            {
                string rest = position < segments.Count ? string.Join('/', segments.Skip(position)) : string.Empty;
                position = segments.Count;
                if (rest.Length > 0)
                {
                    if (!SatisfiesConstraints(parameter, rest))
                    {
                        return false;
                    }

                    values[parameter.Name] = rest;
                }

                continue;
            }

            if (position < segments.Count)
            {
                string value = segments[position];
                if (value.Length == 0 || !SatisfiesConstraints(parameter, value))
                {
                    return false;
                }

                values[parameter.Name] = value;
                position++;
            }
            else if (parameter.DefaultValue is string defaultValue)
            {
                values[parameter.Name] = defaultValue;
            }
            else if (!parameter.IsOptional)
            {
                return false;
            }
        }

        if (position != segments.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> required in RequiredValues)
        {
            if (values.TryGetValue(required.Key, out object? actual))
            {
                if (!string.Equals(actual as string, required.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Keep the action's own spelling (NotesController gives "Notes", even for /notes).
                values[required.Key] = required.Value;
            }
            else
            {
                values[required.Key] = required.Value;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the path of a link to this route from <paramref name="values"/> (ASP.NET's outbound
    /// routing): parameters are filled, trailing defaults left out, and values the template does not
    /// use go to the query string.
    /// </summary>
    /// <param name="values">Explicit values; a required value may be omitted but not contradicted.</param>
    /// <param name="path">The path, starting with <c>/</c>, with its query string.</param>
    /// <returns><see langword="false"/> when a parameter is missing, a constraint fails or a required value differs.</returns>
    public bool TryGeneratePath(IReadOnlyDictionary<string, object?> values, out string path)
    {
        ArgumentNullException.ThrowIfNull(values);
        path = string.Empty;
        HackRouteValueDictionary explicitValues = new(values);
        foreach (KeyValuePair<string, string> required in RequiredValues)
        {
            if (explicitValues.TryGetValue(required.Key, out object? given) && given is not null
                && !string.Equals(HackRouteValueDictionary.FormatValue(given), required.Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        HashSet<string> used = new(RequiredValues.Keys, StringComparer.OrdinalIgnoreCase);
        List<(string Text, bool Omittable)> parts = [];
        foreach (HackRouteSegment segment in Template.Segments)
        {
            if (segment.Literal is string literal)
            {
                parts.Add((Uri.EscapeDataString(literal), false));
                continue;
            }

            HackRouteParameter parameter = segment.Parameter!;
            used.Add(parameter.Name);
            string? value = explicitValues.TryGetValue(parameter.Name, out object? raw) ? HackRouteValueDictionary.FormatValue(raw) : null;
            if (string.IsNullOrEmpty(value) && RequiredValues.TryGetValue(parameter.Name, out string? requiredValue))
            {
                value = requiredValue;
            }

            if (string.IsNullOrEmpty(value))
            {
                if (parameter.DefaultValue is string defaultValue)
                {
                    parts.Add((Uri.EscapeDataString(defaultValue), true));
                    continue;
                }

                if (parameter.IsOptional || parameter.IsCatchAll)
                {
                    parts.Add((string.Empty, true));
                    continue;
                }

                return false;
            }

            if (!SatisfiesConstraints(parameter, value))
            {
                return false;
            }

            bool isDefault = parameter.DefaultValue is string defaultText && defaultText.Equals(value, StringComparison.OrdinalIgnoreCase);
            string escaped = parameter.IsCatchAll
                ? string.Join('/', value.Split('/').Select(Uri.EscapeDataString))
                : Uri.EscapeDataString(value);
            parts.Add((escaped, isDefault));
        }

        // Trailing defaults and absent optional parameters are left out; one in the middle must be written.
        int keep = parts.Count;
        while (keep > 0 && parts[keep - 1].Omittable)
        {
            keep--;
        }

        StringBuilder builder = new("/");
        for (int index = 0; index < keep; index++)
        {
            if (parts[index].Text.Length == 0)
            {
                return false;
            }

            if (index > 0)
            {
                builder.Append('/');
            }

            builder.Append(parts[index].Text);
        }

        char separator = '?';
        foreach (KeyValuePair<string, object?> extra in explicitValues)
        {
            if (used.Contains(extra.Key) || extra.Value is null)
            {
                continue;
            }

            builder.Append(separator).Append(Uri.EscapeDataString(extra.Key)).Append('=')
                .Append(Uri.EscapeDataString(HackRouteValueDictionary.FormatValue(extra.Value) ?? string.Empty));
            separator = '&';
        }

        path = builder.ToString();
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => RawText;

    private bool SatisfiesConstraints(HackRouteParameter parameter, string value)
    {
        foreach (HackRouteConstraint constraint in parameter.Constraints)
        {
            if (!Satisfies(constraint, value))
            {
                return false;
            }
        }

        return true;
    }

    private bool Satisfies(HackRouteConstraint constraint, string value)
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;
        long[] arguments = constraint.Argument is null || constraint.Name == "regex"
            ? []
            : [.. constraint.Argument.Split(',').Select(text => long.Parse(text.Trim(), NumberStyles.Integer, invariant))];
        switch (constraint.Name)
        {
            case "int":
                return int.TryParse(value, NumberStyles.Integer, invariant, out _);
            case "long":
                return long.TryParse(value, NumberStyles.Integer, invariant, out _);
            case "bool":
                return bool.TryParse(value, out _);
            case "guid":
                return Guid.TryParse(value, out _);
            case "double":
                return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, invariant, out _);
            case "float":
                return float.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, invariant, out _);
            case "decimal":
                return decimal.TryParse(value, NumberStyles.Number, invariant, out _);
            case "datetime":
                return DateTime.TryParse(value, invariant, DateTimeStyles.None, out _);
            case "alpha":
                return value.Length > 0 && value.All(char.IsAsciiLetter);
            case "required":
                return value.Length > 0;
            case "min":
                return long.TryParse(value, NumberStyles.Integer, invariant, out long min) && min >= arguments[0];
            case "max":
                return long.TryParse(value, NumberStyles.Integer, invariant, out long max) && max <= arguments[0];
            case "range":
                return long.TryParse(value, NumberStyles.Integer, invariant, out long ranged) && ranged >= arguments[0] && ranged <= arguments[1];
            case "length":
                return arguments.Length == 1 ? value.Length == arguments[0] : value.Length >= arguments[0] && value.Length <= arguments[1];
            case "minlength":
                return value.Length >= arguments[0];
            case "maxlength":
                return value.Length <= arguments[0];
            case "regex":
                try
                {
                    return _regexes[constraint].IsMatch(value);
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }

            default:
                return false;
        }
    }
}
