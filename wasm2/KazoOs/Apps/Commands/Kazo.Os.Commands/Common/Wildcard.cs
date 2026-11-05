using System.Text;
using System.Text.RegularExpressions;

namespace Kazo.Os.Commands.Common;

/// <summary>Shell-style wildcards (<c>*</c>, <c>?</c>, <c>[abc]</c>) for commands that match names themselves (<c>locate</c>).</summary>
internal static class Wildcard
{
    /// <summary>Gets whether <paramref name="pattern"/> holds a wildcard.</summary>
    public static bool HasWildcards(string pattern) => pattern.IndexOfAny(['*', '?', '[']) >= 0;

    /// <summary>Builds an anchored regular expression for <paramref name="pattern"/>.</summary>
    public static Regex ToRegex(string pattern, bool ignoreCase)
    {
        StringBuilder regex = new("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char ch = pattern[i];
            switch (ch)
            {
                case '*':
                    regex.Append(".*");
                    break;
                case '?':
                    regex.Append('.');
                    break;
                case '[':
                    int close = pattern.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        regex.Append(@"\[");
                        break;
                    }

                    string body = pattern[(i + 1)..close];
                    regex.Append('[').Append(body.StartsWith('!') ? "^" + Regex.Escape(body[1..]) : Regex.Escape(body)).Append(']');
                    i = close;
                    break;
                case '\\' when i + 1 < pattern.Length:
                    regex.Append(Regex.Escape(pattern[++i].ToString()));
                    break;
                default:
                    regex.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }

        return new Regex(regex.Append('$').ToString(), RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None));
    }
}
