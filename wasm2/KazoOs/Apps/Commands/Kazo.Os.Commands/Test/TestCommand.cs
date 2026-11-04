using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Test;

/// <summary>
/// <c>test EXPRESSION</c> and <c>[ EXPRESSION ]</c> (ADR 0064): exits 0 when the expression is
/// true, 1 when false, 2 on a usage error. Called as <c>[</c> (its <c>argv[0]</c>), the last
/// argument must be <c>]</c>. Supports file tests (<c>-e -f -d -s -r -w -x -L -h</c>), string
/// tests (<c>-n -z = == !=</c> <c>&lt; &gt;</c>), integer tests (<c>-eq -ne -lt -le -gt -ge</c>),
/// <c>!</c>, <c>-a</c>, <c>-o</c> and parentheses, with POSIX precedence.
/// </summary>
public sealed class TestCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        IReadOnlyList<string> argv = context.App.Environment.GetCommandLineArgs();
        string name = argv.Count > 0 ? argv[0] : "test";
        List<string> args = [.. context.Arguments];
        if (name == "[")
        {
            if (args.Count == 0 || args[^1] != "]")
            {
                await context.StandardError.WriteLineAsync(text["MissingBracket"]);
                return 2;
            }

            args.RemoveAt(args.Count - 1);
        }

        Evaluator evaluator = new(args, context, cancellationToken);
        try
        {
            return await evaluator.EvaluateAsync() ? 0 : 1;
        }
        catch (FormatException exception)
        {
            await context.StandardError.WriteLineAsync(text["Error", name, exception.Message]);
            return 2;
        }
    }

    /// <summary>Recursive descent over the arguments: or := and (-o and)*; and := not (-a not)*; not := ! not | primary.</summary>
    private sealed class Evaluator(List<string> args, TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        /// <summary>The syntax error texts, in the language of the command's process (ADR 0104).</summary>
        private readonly IStringLocalizer<Strings> _text = context.App.GetLocalizer<Strings>();
        private int _position;

        private static readonly HashSet<string> BinaryOperators = new(StringComparer.Ordinal)
        {
            "=", "==", "!=", "<", ">", "-eq", "-ne", "-lt", "-le", "-gt", "-ge", "-nt", "-ot", "-ef"
        };

        public async Task<bool> EvaluateAsync()
        {
            // POSIX gives fixed meanings by argument count before any grammar.
            switch (args.Count)
            {
                case 0:
                    return false;
                case 1:
                    return args[0].Length > 0;
                case 2 when args[0] == "!":
                    return args[1].Length == 0;
                case 3 when BinaryOperators.Contains(args[1]):
                    return await BinaryAsync(args[0], args[1], args[2]);
            }

            bool result = await OrAsync();
            if (_position < args.Count)
            {
                throw new FormatException(_text["UnexpectedArgument", args[_position]]);
            }

            return result;
        }

        private async Task<bool> OrAsync()
        {
            bool result = await AndAsync();
            while (_position < args.Count && args[_position] == "-o")
            {
                _position++;
                bool right = await AndAsync();
                result = result || right;
            }

            return result;
        }

        private async Task<bool> AndAsync()
        {
            bool result = await NotAsync();
            while (_position < args.Count && args[_position] == "-a")
            {
                _position++;
                bool right = await NotAsync();
                result = result && right;
            }

            return result;
        }

        private async Task<bool> NotAsync()
        {
            if (_position < args.Count && args[_position] == "!")
            {
                _position++;
                return !await NotAsync();
            }

            return await PrimaryAsync();
        }

        private async Task<bool> PrimaryAsync()
        {
            if (_position >= args.Count)
            {
                throw new FormatException(_text["ArgumentExpected"]);
            }

            string token = args[_position];
            if (token == "(")
            {
                _position++;
                bool inner = await OrAsync();
                if (_position >= args.Count || args[_position] != ")")
                {
                    throw new FormatException(_text["ParenthesisExpected"]);
                }

                _position++;
                return inner;
            }

            if (_position + 2 < args.Count && BinaryOperators.Contains(args[_position + 1]))
            {
                string left = args[_position];
                string op = args[_position + 1];
                string right = args[_position + 2];
                _position += 3;
                return await BinaryAsync(left, op, right);
            }

            if (token.Length == 2 && token[0] == '-' && _position + 1 < args.Count)
            {
                _position += 2;
                return await UnaryAsync(token[1], args[_position - 1]);
            }

            _position++;
            return token.Length > 0;
        }

        private async Task<bool> UnaryAsync(char op, string operand)
        {
            switch (op)
            {
                case 'n':
                    return operand.Length > 0;
                case 'z':
                    return operand.Length == 0;
                case 't':
                    return false;
            }

            try
            {
                HackFileInfo file = await HackFileInfo.GetAsync(operand, cancellationToken);
                bool isDirectory = await HackDirectory.ExistsAsync(operand, cancellationToken);
                bool exists = file.Exists || isDirectory;
                UnixFileMode mode = exists ? await HackFile.GetUnixFileModeAsync(operand, cancellationToken) : 0;
                bool owner = file.Owner is { } ownerId && (ownerId == context.App.UserId || ownerId == context.App.UserLoginName);
                bool root = context.App.IsRootElevated;
                return op switch
                {
                    'e' or 'a' => exists,
                    'f' => file.Exists && !isDirectory,
                    'd' => isDirectory,
                    's' => file.Exists && file.Length > 0 || isDirectory,
                    'L' or 'h' => file.IsSymbolicLink,
                    'r' => exists && (root || Allowed(mode, owner, UnixFileMode.UserRead, UnixFileMode.GroupRead | UnixFileMode.OtherRead)),
                    'w' => exists && (root || Allowed(mode, owner, UnixFileMode.UserWrite, UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)),
                    'x' => exists && Allowed(mode, owner || root, UnixFileMode.UserExecute, UnixFileMode.GroupExecute | UnixFileMode.OtherExecute),
                    'b' or 'c' or 'p' or 'S' or 'g' or 'u' or 'k' => false,
                    _ => throw new FormatException(_text["UnaryExpected", op])
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A path that cannot be looked up does not exist, for test.
                return false;
            }
        }

        private static bool Allowed(UnixFileMode mode, bool owner, UnixFileMode ownerBit, UnixFileMode otherBits) =>
            owner ? (mode & ownerBit) != 0 : (mode & otherBits) != 0;

        private async Task<bool> BinaryAsync(string left, string op, string right)
        {
            switch (op)
            {
                case "=" or "==":
                    return left == right;
                case "!=":
                    return left != right;
                case "<":
                    return string.CompareOrdinal(left, right) < 0;
                case ">":
                    return string.CompareOrdinal(left, right) > 0;
                case "-nt" or "-ot" or "-ef":
                    HackFileInfo a = await HackFileInfo.GetAsync(left, cancellationToken);
                    HackFileInfo b = await HackFileInfo.GetAsync(right, cancellationToken);
                    return op switch
                    {
                        "-nt" => a.Exists && (!b.Exists || a.LastWriteTimeUtc > b.LastWriteTimeUtc),
                        "-ot" => b.Exists && (!a.Exists || a.LastWriteTimeUtc < b.LastWriteTimeUtc),
                        _ => a.Exists && b.Exists && a.FullName == b.FullName
                    };
            }

            long x = ParseInteger(left);
            long y = ParseInteger(right);
            return op switch
            {
                "-eq" => x == y,
                "-ne" => x != y,
                "-lt" => x < y,
                "-le" => x <= y,
                "-gt" => x > y,
                _ => x >= y
            };
        }

        private long ParseInteger(string text) =>
            long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value)
                ? value
                : throw new FormatException(_text["IntegerExpected", text]);
    }
}
