using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// Shell pattern matching (POSIX "Pattern Matching Notation"), used by pathname expansion,
/// <c>case</c> and <c>${x#pattern}</c>: <c>*</c> any string, <c>?</c> any character,
/// <c>[abc]</c>, <c>[a-z]</c>, <c>[!x]</c> / <c>[^x]</c> bracket expressions, and <c>\x</c> for a
/// literal character. The expander escapes every character that came from quotes, so
/// <c>"*"</c> only matches a star.
/// </summary>
public static class ShellPattern
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary>Gets whether <paramref name="pattern"/> holds an unescaped <c>*</c>, <c>?</c> or <c>[</c>.</summary>
    public static bool HasWildcards(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '\\':
                    i++;
                    break;
                case '*' or '?' or '[':
                    return true;
            }
        }

        return false;
    }

    /// <summary>Removes the backslashes that escape characters of <paramref name="pattern"/>.</summary>
    public static string Unescape(string pattern)
    {
        StringBuilder text = new(pattern.Length);
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\' && i + 1 < pattern.Length)
            {
                i++;
            }

            text.Append(pattern[i]);
        }

        return text.ToString();
    }

    /// <summary>Escapes every pattern character of <paramref name="text"/> so it only matches itself.</summary>
    public static string Escape(string text)
    {
        StringBuilder escaped = new(text.Length);
        foreach (char ch in text)
        {
            if (ch is '\\' or '*' or '?' or '[' or ']')
            {
                escaped.Append('\\');
            }

            escaped.Append(ch);
        }

        return escaped.ToString();
    }

    /// <summary>Gets whether the whole of <paramref name="text"/> matches <paramref name="pattern"/>.</summary>
    public static bool IsMatch(string text, string pattern) => GetRegex(pattern).IsMatch(text);

    /// <summary>
    /// Removes the shortest (<paramref name="longest"/> false) or longest matching prefix
    /// (<paramref name="fromStart"/>) or suffix of <paramref name="value"/>: <c>${x#p}</c>,
    /// <c>${x##p}</c>, <c>${x%p}</c>, <c>${x%%p}</c>.
    /// </summary>
    public static string RemoveAffix(string value, string pattern, bool fromStart, bool longest)
    {
        Regex regex = GetRegex(pattern);
        if (fromStart)
        {
            for (int length = longest ? value.Length : 0; longest ? length >= 0 : length <= value.Length; length += longest ? -1 : 1)
            {
                if (regex.IsMatch(value[..length]))
                {
                    return value[length..];
                }
            }
        }
        else
        {
            for (int start = longest ? 0 : value.Length; longest ? start <= value.Length : start >= 0; start += longest ? 1 : -1)
            {
                if (regex.IsMatch(value[start..]))
                {
                    return value[..start];
                }
            }
        }

        return value;
    }

    private static Regex GetRegex(string pattern) =>
        Cache.GetOrAdd(pattern, static p => new Regex(ToRegex(p), RegexOptions.CultureInvariant | RegexOptions.Singleline));

    /// <summary>Translates a shell pattern into an anchored regular expression.</summary>
    internal static string ToRegex(string pattern)
    {
        StringBuilder regex = new("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char ch = pattern[i];
            switch (ch)
            {
                case '\\' when i + 1 < pattern.Length:
                    regex.Append(Regex.Escape(pattern[++i].ToString()));
                    break;
                case '*':
                    regex.Append(".*");
                    break;
                case '?':
                    regex.Append('.');
                    break;
                case '[':
                    int close = FindBracketEnd(pattern, i);
                    if (close < 0)
                    {
                        regex.Append(@"\[");
                        break;
                    }

                    regex.Append(TranslateBracket(pattern[(i + 1)..close]));
                    i = close;
                    break;
                default:
                    regex.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }

        return regex.Append('$').ToString();
    }

    private static int FindBracketEnd(string pattern, int open)
    {
        int i = open + 1;
        if (i < pattern.Length && pattern[i] is '!' or '^')
        {
            i++;
        }

        if (i < pattern.Length && pattern[i] == ']')
        {
            // A ']' right after '[' or '[!' is a member, not the end.
            i++;
        }

        for (; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\')
            {
                i++;
            }
            else if (pattern[i] == ']')
            {
                return i;
            }
        }

        return -1;
    }

    private static string TranslateBracket(string body)
    {
        StringBuilder set = new("[");
        int i = 0;
        if (body.Length > 0 && body[0] is '!' or '^')
        {
            set.Append('^');
            i = 1;
        }

        for (; i < body.Length; i++)
        {
            char ch = body[i];
            if (ch == '\\' && i + 1 < body.Length)
            {
                ch = body[++i];
            }
            else if (ch == '[' && i + 1 < body.Length && body[i + 1] == ':')
            {
                int end = body.IndexOf(":]", i + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    set.Append(body[(i + 2)..end] switch
                    {
                        "alpha" => @"a-zA-Z",
                        "digit" => @"0-9",
                        "alnum" => @"a-zA-Z0-9",
                        "upper" => @"A-Z",
                        "lower" => @"a-z",
                        "space" => @"\s",
                        "punct" => @"\p{P}\p{S}",
                        "xdigit" => @"0-9a-fA-F",
                        _ => string.Empty
                    });
                    i = end + 1;
                    continue;
                }
            }

            if (ch == '-' && set.Length > 1 && i + 1 < body.Length)
            {
                set.Append('-');
                continue;
            }

            set.Append(ch is '\\' or ']' or '[' or '^' or '-' ? "\\" + ch : ch.ToString());
        }

        return set.Append(']').ToString();
    }
}
