// This file is compiled twice: in Kazo.Os.AppSdk.Web (net10.0), where the router matches requests
// with it, and linked into Kazo.Os.Tools.WebRouteGenerator (netstandard2.0), where the build checks
// and normalizes every controller route (ADR 0082). Keep it free of APIs netstandard2.0 lacks
// (ranges, spans, string.Contains(char), NotNullWhen...).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kazo.Os.AppSdk.Web.Routing;

/// <summary>
/// A parsed route template such as <c>api/notes/{id:int}</c> or <c>{controller=Home}/{action=Index}/{id?}</c>,
/// with the syntax of ASP.NET Core routing (ADR 0076 §3): literal segments, <c>{name}</c>,
/// <c>{name?}</c>, <c>{name=default}</c>, <c>{*rest}</c> / <c>{**rest}</c> and constraints
/// <c>{name:int}</c>, <c>{name:min(1)}</c>, <c>{name:regex(^\d+$)}</c>...
/// </summary>
/// <remarks>
/// A segment is either a literal or a single parameter: complex segments (<c>{name}.{ext}</c>) are not
/// supported in v1. <c>{{</c> and <c>}}</c> escape a brace in a literal or in a constraint argument.
/// </remarks>
public sealed class HackRouteTemplate
{
    private static readonly string[] KnownConstraints =
    [
        "int", "long", "bool", "guid", "double", "decimal", "float", "datetime", "alpha", "required",
        "min", "max", "range", "length", "minlength", "maxlength", "regex"
    ];

    private HackRouteTemplate(string text, IReadOnlyList<HackRouteSegment> segments)
    {
        Text = text;
        Segments = segments;
    }

    /// <summary>Gets the template text, without leading <c>/</c> or <c>~/</c> nor trailing <c>/</c>.</summary>
    public string Text { get; }

    /// <summary>Gets the segments, in order; empty for the root template <c>""</c>.</summary>
    public IReadOnlyList<HackRouteSegment> Segments { get; }

    /// <summary>
    /// Parses <paramref name="template"/>. A leading <c>/</c> or <c>~/</c> (an absolute attribute route)
    /// and a trailing <c>/</c> are dropped.
    /// </summary>
    /// <param name="template">The template text.</param>
    /// <returns>The parsed template.</returns>
    /// <exception cref="FormatException">The template is malformed; the message says why.</exception>
    public static HackRouteTemplate Parse(string template)
    {
        if (template is null)
        {
            throw new ArgumentNullException(nameof(template));
        }

        string text = template;
        if (text.StartsWith("~/", StringComparison.Ordinal))
        {
            text = text.Substring(2);
        }
        else if (text.StartsWith("/", StringComparison.Ordinal))
        {
            text = text.Substring(1);
        }

        if (text.EndsWith("/", StringComparison.Ordinal))
        {
            text = text.Substring(0, text.Length - 1);
        }

        if (text.IndexOf('?') >= 0 && !ContainsOnlyParameterQuestionMarks(text))
        {
            throw new FormatException($"The route template '{template}' cannot contain a query string ('?' outside a parameter).");
        }

        List<HackRouteSegment> segments = [];
        if (text.Length == 0)
        {
            return new HackRouteTemplate(string.Empty, segments);
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        string[] parts = SplitSegments(text, template);
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            if (part.Length == 0)
            {
                throw new FormatException($"The route template '{template}' has an empty segment ('//').");
            }

            HackRouteSegment segment = ParseSegment(part, template);
            if (segment.Parameter is HackRouteParameter parameter)
            {
                if (!names.Add(parameter.Name))
                {
                    throw new FormatException($"The route parameter '{parameter.Name}' appears more than once in '{template}'.");
                }

                if (parameter.IsCatchAll && index != parts.Length - 1)
                {
                    throw new FormatException($"The catch-all parameter '{parameter.Name}' must be the last segment of '{template}'.");
                }
            }

            if (segments.Count > 0 && segments[segments.Count - 1].Parameter is { IsOptional: true } previous
                && !(segment.Parameter is { } next && (next.IsOptional || next.IsCatchAll)))
            {
                throw new FormatException(
                    $"The optional parameter '{previous.Name}' of '{template}' can only be followed by optional parameters.");
            }

            segments.Add(segment);
        }

        return new HackRouteTemplate(text, segments);
    }

    /// <summary>
    /// Gets a key that two templates matching the same URLs share: literals in lower case, parameters
    /// without their names (<c>api/notes/{:int}</c>). The build reports two routes with the same key
    /// and an HTTP method in common as a conflict (<c>HOSWEB0001</c>).
    /// </summary>
    public string GetConflictKey()
    {
        StringBuilder key = new();
        foreach (HackRouteSegment segment in Segments)
        {
            if (key.Length > 0)
            {
                key.Append('/');
            }

            if (segment.Parameter is HackRouteParameter parameter)
            {
                key.Append('{');
                if (parameter.IsCatchAll)
                {
                    key.Append('*');
                }

                foreach (HackRouteConstraint constraint in parameter.Constraints)
                {
                    key.Append(':').Append(constraint.Text);
                }

                if (parameter.IsOptional)
                {
                    key.Append('?');
                }

                key.Append('}');
            }
            else
            {
                key.Append(segment.Literal!.ToLowerInvariant());
            }
        }

        return key.ToString();
    }

    /// <summary>
    /// Gets the precedence of each segment, the same scale as ASP.NET's inbound precedence: 1 literal,
    /// 2 constrained parameter, 3 parameter, 4 optional parameter, 5 catch-all. Lower wins.
    /// </summary>
    public IReadOnlyList<int> GetPrecedence()
    {
        int[] digits = new int[Segments.Count];
        for (int index = 0; index < digits.Length; index++)
        {
            HackRouteParameter? parameter = Segments[index].Parameter;
            digits[index] = parameter is null ? 1
                : parameter.IsCatchAll ? 5
                : parameter.IsOptional ? 4
                : parameter.Constraints.Count > 0 ? 2
                : 3;
        }

        return digits;
    }

    /// <inheritdoc />
    public override string ToString() => Text;

    private static bool ContainsOnlyParameterQuestionMarks(string text)
    {
        // '?' is legal only inside a parameter ({id?}, or a regex argument); "{{" and "}}" are escapes.
        int depth = 0;
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            if ((current == '{' || current == '}') && index + 1 < text.Length && text[index + 1] == current)
            {
                index++;
            }
            else if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (current == '?' && depth == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string[] SplitSegments(string text, string template)
    {
        // A '/' inside a parameter (a regex argument) does not split.
        List<string> parts = [];
        StringBuilder current = new();
        int depth = 0;
        for (int index = 0; index < text.Length; index++)
        {
            char c = text[index];
            if ((c == '{' || c == '}') && index + 1 < text.Length && text[index + 1] == c)
            {
                current.Append(c).Append(c);
                index++;
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth < 0)
                {
                    throw new FormatException($"The route template '{template}' has an unmatched '}}'.");
                }
            }

            if (c == '/' && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (depth != 0)
        {
            throw new FormatException($"The route template '{template}' has an unmatched '{{'.");
        }

        parts.Add(current.ToString());
        return parts.ToArray();
    }

    private static HackRouteSegment ParseSegment(string part, string template)
    {
        if (part.StartsWith("{", StringComparison.Ordinal) && !part.StartsWith("{{", StringComparison.Ordinal))
        {
            if (FindParameterEnd(part) != part.Length - 1)
            {
                throw new FormatException(
                    $"The segment '{part}' of '{template}' mixes a parameter and text; complex segments are not supported.");
            }

            string inner = part.Substring(1, part.Length - 2);
            return new HackRouteSegment(null, ParseParameter(inner, part, template));
        }

        StringBuilder literal = new();
        for (int index = 0; index < part.Length; index++)
        {
            char c = part[index];
            if ((c == '{' || c == '}') && index + 1 < part.Length && part[index + 1] == c)
            {
                literal.Append(c);
                index++;
                continue;
            }

            if (c == '{' || c == '}')
            {
                throw new FormatException(
                    $"The segment '{part}' of '{template}' mixes a parameter and text; complex segments are not supported.");
            }

            literal.Append(c);
        }

        return new HackRouteSegment(literal.ToString(), null);
    }

    private static int FindParameterEnd(string part)
    {
        // The '}' closing the parameter opened at 0; "{{" and "}}" inside are escaped braces.
        for (int index = 1; index < part.Length; index++)
        {
            char c = part[index];
            if ((c == '{' || c == '}') && index + 1 < part.Length && part[index + 1] == c)
            {
                index++;
                continue;
            }

            if (c == '}')
            {
                return index;
            }
        }

        return -1;
    }

    private static HackRouteParameter ParseParameter(string inner, string part, string template)
    {
        string text = inner.Replace("{{", "{").Replace("}}", "}");
        bool catchAll = false;
        if (text.StartsWith("**", StringComparison.Ordinal))
        {
            catchAll = true;
            text = text.Substring(2);
        }
        else if (text.StartsWith("*", StringComparison.Ordinal))
        {
            catchAll = true;
            text = text.Substring(1);
        }

        int nameEnd = 0;
        while (nameEnd < text.Length && text[nameEnd] != ':' && text[nameEnd] != '=' && text[nameEnd] != '?')
        {
            nameEnd++;
        }

        string name = text.Substring(0, nameEnd);
        if (name.Length == 0 || !IsValidName(name))
        {
            throw new FormatException($"The parameter '{part}' of '{template}' has an invalid name (letters, digits and '_').");
        }

        List<HackRouteConstraint> constraints = [];
        int position = nameEnd;
        while (position < text.Length && text[position] == ':')
        {
            int start = position + 1;
            int cursor = start;
            while (cursor < text.Length && text[cursor] != ':' && text[cursor] != '=' && text[cursor] != '?' && text[cursor] != '(')
            {
                cursor++;
            }

            string constraintName = text.Substring(start, cursor - start);
            string? argument = null;
            if (cursor < text.Length && text[cursor] == '(')
            {
                // The argument ends at the last ')' followed by ':', '=', '?' or the end of the parameter.
                int close = -1;
                for (int scan = cursor + 1; scan < text.Length; scan++)
                {
                    if (text[scan] == ')' && (scan + 1 == text.Length || text[scan + 1] == ':' || text[scan + 1] == '=' || (text[scan + 1] == '?' && scan + 2 == text.Length)))
                    {
                        close = scan;
                        if (scan + 1 == text.Length || text[scan + 1] != ')')
                        {
                            break;
                        }
                    }
                }

                if (close < 0)
                {
                    throw new FormatException($"The constraint '{constraintName}' of '{part}' in '{template}' has no closing ')'.");
                }

                argument = text.Substring(cursor + 1, close - cursor - 1);
                cursor = close + 1;
            }

            constraints.Add(CreateConstraint(constraintName, argument, part, template));
            position = cursor;
        }

        string? defaultValue = null;
        bool optional = false;
        if (position < text.Length && text[position] == '=')
        {
            defaultValue = text.Substring(position + 1);
            if (defaultValue.EndsWith("?", StringComparison.Ordinal))
            {
                throw new FormatException($"The parameter '{part}' of '{template}' cannot be both optional and have a default value.");
            }

            position = text.Length;
        }
        else if (position < text.Length && text[position] == '?')
        {
            optional = true;
            position++;
        }

        if (position != text.Length)
        {
            throw new FormatException($"The parameter '{part}' of '{template}' is malformed.");
        }

        if (catchAll && (optional || defaultValue is not null))
        {
            throw new FormatException($"The catch-all parameter '{part}' of '{template}' is already optional; remove '?' or the default value.");
        }

        return new HackRouteParameter(name, optional || defaultValue is not null, defaultValue, catchAll, constraints);
    }

    private static bool IsValidName(string name)
    {
        foreach (char c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static HackRouteConstraint CreateConstraint(string name, string? argument, string part, string template)
    {
        string lower = name.ToLowerInvariant();
        if (Array.IndexOf(KnownConstraints, lower) < 0)
        {
            throw new FormatException(
                $"Unknown route constraint '{name}' in '{part}' of '{template}'. Known: {string.Join(", ", KnownConstraints)}.");
        }

        string[] arguments = argument is null ? [] : argument.Split(',');
        int expected = lower switch
        {
            "min" or "max" or "minlength" or "maxlength" => 1,
            "range" => 2,
            "length" => arguments.Length is 1 or 2 ? arguments.Length : 1,
            "regex" => -1,
            _ => 0
        };

        if (lower == "regex")
        {
            if (argument is null || argument.Length == 0)
            {
                throw new FormatException($"The regex constraint of '{part}' in '{template}' needs a pattern.");
            }

            try
            {
                _ = new Regex(argument, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException exception)
            {
                throw new FormatException($"The regex constraint of '{part}' in '{template}' is invalid: {exception.Message}");
            }

            return new HackRouteConstraint(lower, argument, $"regex({argument})");
        }

        if (arguments.Length != expected)
        {
            throw new FormatException(expected == 0
                ? $"The constraint '{name}' of '{part}' in '{template}' takes no argument."
                : $"The constraint '{name}' of '{part}' in '{template}' takes {expected} argument(s).");
        }

        foreach (string value in arguments)
        {
            if (!long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                throw new FormatException($"The constraint '{name}' of '{part}' in '{template}' needs integer arguments.");
            }
        }

        return new HackRouteConstraint(lower, argument, argument is null ? lower : $"{lower}({argument})");
    }
}

/// <summary>One segment of a <see cref="HackRouteTemplate"/>: a literal or a parameter.</summary>
public sealed class HackRouteSegment
{
    internal HackRouteSegment(string? literal, HackRouteParameter? parameter)
    {
        Literal = literal;
        Parameter = parameter;
    }

    /// <summary>Gets the literal text (braces unescaped), or <see langword="null"/> for a parameter.</summary>
    public string? Literal { get; }

    /// <summary>Gets the parameter, or <see langword="null"/> for a literal.</summary>
    public HackRouteParameter? Parameter { get; }
}

/// <summary>A route parameter: <c>{name}</c>, <c>{name?}</c>, <c>{name=default}</c>, <c>{*name}</c>.</summary>
public sealed class HackRouteParameter
{
    internal HackRouteParameter(string name, bool isOptional, string? defaultValue, bool isCatchAll, IReadOnlyList<HackRouteConstraint> constraints)
    {
        Name = name;
        IsOptional = isOptional;
        DefaultValue = defaultValue;
        IsCatchAll = isCatchAll;
        Constraints = constraints;
    }

    /// <summary>Gets the parameter name (route values compare it without case).</summary>
    public string Name { get; }

    /// <summary>Gets whether the segment may be absent (<c>?</c> or a default value).</summary>
    public bool IsOptional { get; }

    /// <summary>Gets the default value (<c>{controller=Home}</c>), or <see langword="null"/>.</summary>
    public string? DefaultValue { get; }

    /// <summary>Gets whether the parameter takes the rest of the path (<c>{*rest}</c>).</summary>
    public bool IsCatchAll { get; }

    /// <summary>Gets the inline constraints, in order.</summary>
    public IReadOnlyList<HackRouteConstraint> Constraints { get; }
}

/// <summary>An inline route constraint (<c>int</c>, <c>min(1)</c>, <c>regex(^\d+$)</c>...).</summary>
public sealed class HackRouteConstraint
{
    internal HackRouteConstraint(string name, string? argument, string text)
    {
        Name = name;
        Argument = argument;
        Text = text;
    }

    /// <summary>Gets the constraint name, lower case.</summary>
    public string Name { get; }

    /// <summary>Gets the argument between the parentheses, or <see langword="null"/>.</summary>
    public string? Argument { get; }

    /// <summary>Gets the constraint as written, normalized (<c>min(1)</c>).</summary>
    public string Text { get; }
}
