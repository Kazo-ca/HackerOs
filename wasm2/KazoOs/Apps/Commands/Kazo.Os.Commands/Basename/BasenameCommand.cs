using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Basename;

/// <summary>
/// <c>basename NAME [SUFFIX]</c>, <c>basename -a [-s SUFFIX] NAME...</c> (ADR 0064): strips the
/// directory and an optional suffix, purely as text, like coreutils.
/// </summary>
public sealed class BasenameCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        bool multiple = false;
        string? suffix = null;
        List<string> names = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "-a" or "--multiple":
                    multiple = true;
                    break;
                case "-s" when i + 1 < args.Count:
                    suffix = args[++i];
                    multiple = true;
                    break;
                default:
                    names.Add(args[i]);
                    break;
            }
        }

        if (names.Count == 0)
        {
            await context.StandardError.WriteLineAsync(text["MissingOperand"]);
            return 1;
        }

        if (!multiple)
        {
            if (names.Count > 2)
            {
                await context.StandardError.WriteLineAsync(text["ExtraOperand", names[2]]);
                return 1;
            }

            suffix = names.Count == 2 ? names[1] : null;
            names = [names[0]];
        }

        foreach (string name in names)
        {
            await context.StandardOutput.WriteLineAsync(Strip(name, suffix));
        }

        return 0;
    }

    /// <summary>The last path component of <paramref name="name"/>, without <paramref name="suffix"/> (never emptied by it).</summary>
    internal static string Strip(string name, string? suffix)
    {
        string trimmed = name.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return name.Length > 0 ? "/" : string.Empty;
        }

        string last = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        if (!string.IsNullOrEmpty(suffix) && last.Length > suffix.Length && last.EndsWith(suffix, StringComparison.Ordinal))
        {
            last = last[..^suffix.Length];
        }

        return last;
    }
}
