using System.Globalization;
using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Printf;

/// <summary>
/// <c>printf FORMAT [ARGUMENT...]</c> (ADR 0064), as coreutils: escapes (<c>\n \t \\ \0NNN
/// \xHH</c>), conversions <c>%s %b %c %d %i %u %o %x %X %e %f %g %%</c> with flags, width and
/// precision (<c>%-10s</c>, <c>%05.2f</c>, <c>%*d</c>), and the format reused until every
/// argument is consumed. Unlike <c>echo</c>, no newline is added.
/// </summary>
public sealed class PrintfCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        IReadOnlyList<string> args = context.Arguments;
        if (args.Count > 0 && args[0] == "--")
        {
            args = [.. args.Skip(1)];
        }

        if (args.Count == 0)
        {
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 2;
        }

        Formatter formatter = new(args[0], [.. args.Skip(1)], text);
        string output = formatter.Run();
        await context.StandardOutput.WriteAsync(output);
        foreach (string error in formatter.Errors)
        {
            await context.StandardError.WriteLineAsync(text["Error", error]);
        }

        return formatter.Errors.Count > 0 ? 1 : 0;
    }

    /// <summary>Formats like printf(1); <paramref name="messages"/> words the errors in the process language.</summary>
    internal sealed class Formatter(string format, IReadOnlyList<string> arguments, IStringLocalizer<Strings> messages)
    {
        private int _next;

        public List<string> Errors { get; } = [];

        public string Run()
        {
            StringBuilder output = new();
            do
            {
                int consumedBefore = _next;
                if (!FormatOnce(output))
                {
                    break;
                }

                if (_next == consumedBefore)
                {
                    break;
                }
            }
            while (_next < arguments.Count);

            return output.ToString();
        }

        /// <summary>Applies the format once; false when <c>\c</c> stopped all output.</summary>
        private bool FormatOnce(StringBuilder output)
        {
            for (int i = 0; i < format.Length; i++)
            {
                char ch = format[i];
                if (ch == '\\')
                {
                    if (!AppendEscape(format, ref i, output, octalNeedsZero: false))
                    {
                        return false;
                    }

                    continue;
                }

                if (ch != '%')
                {
                    output.Append(ch);
                    continue;
                }

                if (i + 1 < format.Length && format[i + 1] == '%')
                {
                    output.Append('%');
                    i++;
                    continue;
                }

                int start = i;
                i++;
                string flags = string.Empty;
                while (i < format.Length && "-+ #0".Contains(format[i]))
                {
                    flags += format[i++];
                }

                int? width = ReadNumberOrStar(ref i);
                int? precision = null;
                if (i < format.Length && format[i] == '.')
                {
                    i++;
                    precision = ReadNumberOrStar(ref i) ?? 0;
                }

                while (i < format.Length && format[i] is 'l' or 'h' or 'q' or 'j' or 'z' or 't' or 'L')
                {
                    i++;
                }

                if (i >= format.Length)
                {
                    Errors.Add(messages["InvalidConversion", format[(start + 1)..]]);
                    output.Append(format[start..]);
                    break;
                }

                char conversion = format[i];
                if (!AppendConversion(output, conversion, flags, width, precision))
                {
                    return false;
                }
            }

            return true;
        }

        private int? ReadNumberOrStar(ref int i)
        {
            if (i < format.Length && format[i] == '*')
            {
                i++;
                return (int)ToInteger(NextArgument() ?? "0");
            }

            int start = i;
            while (i < format.Length && char.IsAsciiDigit(format[i]))
            {
                i++;
            }

            return i > start ? int.Parse(format[start..i], CultureInfo.InvariantCulture) : null;
        }

        private string? NextArgument() => _next < arguments.Count ? arguments[_next++] : null;

        private bool AppendConversion(StringBuilder output, char conversion, string flags, int? width, int? precision)
        {
            string text;
            switch (conversion)
            {
                case 's':
                    text = NextArgument() ?? string.Empty;
                    if (precision is { } max && text.Length > max)
                    {
                        text = text[..max];
                    }

                    break;
                case 'b':
                    StringBuilder expanded = new();
                    string raw = NextArgument() ?? string.Empty;
                    for (int j = 0; j < raw.Length; j++)
                    {
                        if (raw[j] == '\\')
                        {
                            if (!AppendEscape(raw, ref j, expanded, octalNeedsZero: true))
                            {
                                output.Append(Pad(expanded.ToString(), flags, width));
                                return false;
                            }

                            continue;
                        }

                        expanded.Append(raw[j]);
                    }

                    text = expanded.ToString();
                    break;
                case 'c':
                    string value = NextArgument() ?? string.Empty;
                    text = value.Length > 0 ? value[0].ToString() : string.Empty;
                    break;
                case 'd' or 'i':
                    long number = ToInteger(NextArgument() ?? "0");
                    text = FormatInteger(Math.Abs(number).ToString(CultureInfo.InvariantCulture), number < 0, flags, precision);
                    break;
                case 'u':
                    text = FormatInteger(unchecked((ulong)ToInteger(NextArgument() ?? "0")).ToString(CultureInfo.InvariantCulture), false, flags, precision);
                    break;
                case 'o':
                    text = FormatInteger(Convert.ToString(ToInteger(NextArgument() ?? "0"), 8), false, flags, precision);
                    break;
                case 'x' or 'X':
                    string hex = ToInteger(NextArgument() ?? "0").ToString(conversion == 'x' ? "x" : "X", CultureInfo.InvariantCulture);
                    text = FormatInteger(hex, false, flags, precision);
                    if (flags.Contains('#') && hex != "0")
                    {
                        text = (conversion == 'x' ? "0x" : "0X") + text;
                    }

                    break;
                case 'f' or 'F' or 'e' or 'E' or 'g' or 'G':
                    double real = ToDouble(NextArgument() ?? "0");
                    int digits = precision ?? 6;
                    text = conversion switch
                    {
                        'f' or 'F' => Math.Abs(real).ToString("F" + digits, CultureInfo.InvariantCulture),
                        'e' or 'E' => FormatExponent(Math.Abs(real), digits, conversion),
                        _ => Math.Abs(real).ToString("G" + Math.Max(1, digits), CultureInfo.InvariantCulture)
                    };
                    text = (real < 0 ? "-" : flags.Contains('+') ? "+" : flags.Contains(' ') ? " " : string.Empty) + text;
                    if (flags.Contains('0') && !flags.Contains('-') && width is { } zeroWidth && text.Length < zeroWidth)
                    {
                        string sign = text.Length > 0 && text[0] is '-' or '+' or ' ' ? text[..1] : string.Empty;
                        text = sign + text[sign.Length..].PadLeft(zeroWidth - sign.Length, '0');
                    }

                    break;
                default:
                    Errors.Add(messages["InvalidConversion", conversion]);
                    text = "%" + conversion;
                    break;
            }

            if (conversion is 'd' or 'i' or 'u' or 'o' or 'x' or 'X' && flags.Contains('0') && !flags.Contains('-') && precision is null
                && width is { } padWidth && text.Length < padWidth)
            {
                string sign = text.Length > 0 && text[0] is '-' or '+' or ' ' ? text[..1] : string.Empty;
                text = sign + text[sign.Length..].PadLeft(padWidth - sign.Length, '0');
            }

            output.Append(Pad(text, flags, width));
            return true;
        }

        private static string FormatExponent(double value, int digits, char conversion)
        {
            int exponent = value == 0 ? 0 : (int)Math.Floor(Math.Log10(value));
            string mantissa = (value == 0 ? 0 : value / Math.Pow(10, exponent)).ToString("F" + digits, CultureInfo.InvariantCulture);
            if (mantissa.StartsWith("10", StringComparison.Ordinal))
            {
                // Rounding carried into a new digit (9.9999 -> 10.00): renormalise.
                exponent++;
                mantissa = (value / Math.Pow(10, exponent)).ToString("F" + digits, CultureInfo.InvariantCulture);
            }

            return $"{mantissa}{conversion}{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent):00}";
        }

        private static string FormatInteger(string digits, bool negative, string flags, int? precision)
        {
            if (precision is { } minimum && digits.Length < minimum)
            {
                digits = digits.PadLeft(minimum, '0');
            }

            return (negative ? "-" : flags.Contains('+') ? "+" : flags.Contains(' ') ? " " : string.Empty) + digits;
        }

        private static string Pad(string text, string flags, int? width) =>
            width is not { } w || text.Length >= w ? text : flags.Contains('-') ? text.PadRight(w) : text.PadLeft(w);

        private long ToInteger(string text)
        {
            string trimmed = text.Trim();
            if (trimmed.Length >= 2 && trimmed[0] is '\'' or '"')
            {
                // printf %d "'A" prints the character code, as POSIX requires.
                return trimmed[1];
            }

            try
            {
                if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
                {
                    bool negative = trimmed[0] == '-';
                    long hex = Convert.ToInt64(trimmed[(negative ? 3 : 2)..], 16);
                    return negative ? -hex : hex;
                }

                if (trimmed.Length > 1 && trimmed[0] == '0' && trimmed.All(char.IsAsciiDigit))
                {
                    return Convert.ToInt64(trimmed, 8);
                }

                if (long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
                {
                    return value;
                }
            }
            catch (FormatException)
            {
            }

            if (trimmed.Length > 0)
            {
                Errors.Add(messages["InvalidNumber", text]);
            }

            return 0;
        }

        private double ToDouble(string text)
        {
            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return value;
            }

            Errors.Add(messages["InvalidNumber", text]);
            return 0;
        }

        /// <summary>Appends the escape at <paramref name="text"/>[<paramref name="i"/>]; false for <c>\c</c> (stop output).</summary>
        internal static bool AppendEscape(string text, ref int i, StringBuilder output, bool octalNeedsZero)
        {
            if (i + 1 >= text.Length)
            {
                output.Append('\\');
                return true;
            }

            char next = text[++i];
            switch (next)
            {
                case 'n': output.Append('\n'); break;
                case 't': output.Append('\t'); break;
                case 'r': output.Append('\r'); break;
                case 'a': output.Append('\a'); break;
                case 'b': output.Append('\b'); break;
                case 'f': output.Append('\f'); break;
                case 'v': output.Append('\v'); break;
                case 'e': output.Append('\x1b'); break;
                case '\\': output.Append('\\'); break;
                case '"': output.Append('"'); break;
                case '\'': output.Append('\''); break;
                case 'c': return false;
                case 'x':
                    int hexStart = i + 1;
                    int hexEnd = hexStart;
                    while (hexEnd < text.Length && hexEnd - hexStart < 2 && Uri.IsHexDigit(text[hexEnd]))
                    {
                        hexEnd++;
                    }

                    if (hexEnd == hexStart)
                    {
                        output.Append("\\x");
                        break;
                    }

                    output.Append((char)Convert.ToInt32(text[hexStart..hexEnd], 16));
                    i = hexEnd - 1;
                    break;
                case >= '0' and <= '7':
                    int octalStart = octalNeedsZero && next == '0' ? i + 1 : i;
                    int octalEnd = octalStart;
                    while (octalEnd < text.Length && octalEnd - octalStart < 3 && text[octalEnd] is >= '0' and <= '7')
                    {
                        octalEnd++;
                    }

                    output.Append(octalEnd > octalStart ? (char)Convert.ToInt32(text[octalStart..octalEnd], 8) : '\0');
                    i = Math.Max(i, octalEnd - 1);
                    break;
                default:
                    output.Append('\\').Append(next);
                    break;
            }

            return true;
        }
    }
}
