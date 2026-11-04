using System.Text;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Shell;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// Tab completion done by the shell, as <c>bash</c> does (ADR 0058, section 4), not by the
/// terminal emulator: command names in command position, paths everywhere else.
/// </summary>
/// <remarks>
/// Paths are listed through the public file API (<see cref="HackDirectoryInfo"/>), so the caller
/// must have entered the shell's execution context: a relative word is resolved from the shell's
/// directory and a directory the shell cannot read proposes nothing, exactly as a <c>ls</c> it
/// would run.
/// </remarks>
internal static class ShellCompletion
{
    /// <summary>Characters that end a command and start a new one: <c>|</c>, <c>&amp;&amp;</c>, <c>;</c>.</summary>
    private static readonly char[] CommandSeparators = ['|', '&', ';'];

    /// <summary>Characters that end a word without starting a command: redirections.</summary>
    private static readonly char[] RedirectionOperators = ['<', '>'];

    /// <summary>
    /// Characters a completed name must escape with a backslash to stay one literal word for
    /// <see cref="ShellParser"/> (blanks, operators, quotes, expansions), as bash inserts
    /// <c>My\ Notes.txt</c>.
    /// </summary>
    private const string CharactersToEscape = " \t\\'\"|&;<>$`()*?[]#!{}";

    /// <summary>Computes the candidates for the word that ends at <paramref name="cursor"/>.</summary>
    /// <param name="commandLine">The line being typed.</param>
    /// <param name="cursor">Cursor position.</param>
    /// <param name="commands">Installed commands (names and static aliases).</param>
    /// <param name="homeDirectory">The shell's <c>HOME</c>, for a word starting with <c>~</c>.</param>
    /// <param name="cancellationToken">Cancels the directory listing.</param>
    /// <returns>The completion, empty when nothing matches.</returns>
    public static async Task<HackShellCompletion> CompleteAsync(
        string commandLine,
        int cursor,
        IReadOnlyList<ShellCommandInfo> commands,
        string homeDirectory,
        CancellationToken cancellationToken = default)
    {
        int wordStart = FindWordStart(commandLine, cursor);
        string rawWord = commandLine[wordStart..cursor];
        if (rawWord.Length > 0 && rawWord[0] is '"' or '\'')
        {
            // Quoted words are left alone: the completion would have to close the quote.
            return HackShellCompletion.None(cursor);
        }

        string word = Unescape(rawWord);

        // A command word with a slash (`./build.sh`, `/usr/bin/x`) is a path, as in bash.
        if (IsCommandPosition(commandLine, wordStart) && !word.Contains('/'))
        {
            return new HackShellCompletion(wordStart, rawWord.Length, CompleteCommand(word, commands));
        }

        IReadOnlyList<string> candidates = await CompletePathAsync(rawWord, word, homeDirectory, cancellationToken).ConfigureAwait(false);
        return new HackShellCompletion(wordStart, rawWord.Length, candidates);
    }

    /// <summary>Names of the installed commands and aliases starting with <paramref name="word"/>.</summary>
    private static string[] CompleteCommand(string word, IReadOnlyList<ShellCommandInfo> commands) =>
        // Case-sensitive, as a Linux shell: `LS` is not `ls`.
        [.. commands
            .SelectMany(static command => command.Aliases.Prepend(command.Name))
            .Where(name => name.StartsWith(word, StringComparison.Ordinal))
            .Select(Escape)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Entries of the directory named by the word's head whose name starts with its last segment.
    /// The typed head is kept as is (<c>~</c>, <c>../</c>, escapes): only the name is completed.
    /// </summary>
    private static async Task<IReadOnlyList<string>> CompletePathAsync(
        string rawWord, string word, string homeDirectory, CancellationToken cancellationToken)
    {
        if (word == "~")
        {
            // bash completes a lone tilde to the home directory, still written `~/`.
            return ["~/"];
        }

        int rawSlash = LastUnescapedSlash(rawWord);
        string rawHead = rawSlash < 0 ? string.Empty : rawWord[..(rawSlash + 1)];
        string head = Unescape(rawHead);
        string prefix = Unescape(rawSlash < 0 ? rawWord : rawWord[(rawSlash + 1)..]);

        string directory = head.Length == 0 ? "." : ExpandTilde(head, homeDirectory);
        HackFileSystemInfo[] entries;
        try
        {
            HackDirectoryInfo info = await HackDirectoryInfo.GetAsync(directory, cancellationToken).ConfigureAwait(false);
            if (!info.Exists)
            {
                return [];
            }

            entries = await info.GetFileSystemInfosAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException)
        {
            // An unreadable or missing directory proposes nothing, without an error (ADR 0058 §4).
            return [];
        }

        // Hidden entries only when the user typed the dot, as bash without `dotglob`.
        bool showHidden = prefix.StartsWith('.');
        return [.. entries
            .Where(entry => entry.Name.StartsWith(prefix, StringComparison.Ordinal))
            .Where(entry => showHidden || !entry.Name.StartsWith('.'))
            .Where(static entry => entry.Name is not ("." or ".."))
            .Select(entry => rawHead + Escape(entry.Name) + (entry is HackDirectoryInfo ? "/" : string.Empty))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Replaces a leading <c>~</c> (alone or before <c>/</c>) with the home directory.</summary>
    private static string ExpandTilde(string path, string homeDirectory)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return homeDirectory.TrimEnd('/') + path[1..];
        }

        return path;
    }

    /// <summary>
    /// Walks back from the cursor to the start of the word: a blank, a command separator or a
    /// redirection ends it unless a backslash escapes it (<c>My\ No</c> is one word).
    /// </summary>
    private static int FindWordStart(string commandLine, int cursor)
    {
        int wordStart = cursor;
        while (wordStart > 0)
        {
            char previous = commandLine[wordStart - 1];
            bool delimiter = char.IsWhiteSpace(previous)
                || Array.IndexOf(CommandSeparators, previous) >= 0
                || Array.IndexOf(RedirectionOperators, previous) >= 0;
            if (delimiter && !IsEscaped(commandLine, wordStart - 1))
            {
                break;
            }

            wordStart--;
        }

        return wordStart;
    }

    /// <summary>Whether an odd number of backslashes precedes <paramref name="index"/>.</summary>
    private static bool IsEscaped(string text, int index)
    {
        int backslashes = 0;
        for (int position = index - 1; position >= 0 && text[position] == '\\'; position--)
        {
            backslashes++;
        }

        return backslashes % 2 == 1;
    }

    /// <summary>Index of the last <c>/</c> that is not escaped, or <c>-1</c>.</summary>
    private static int LastUnescapedSlash(string word)
    {
        for (int index = word.Length - 1; index >= 0; index--)
        {
            if (word[index] == '/' && !IsEscaped(word, index))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Removes the backslashes of a word typed outside quotes (<c>My\ Notes</c> → <c>My Notes</c>).</summary>
    private static string Unescape(string rawWord)
    {
        if (!rawWord.Contains('\\'))
        {
            return rawWord;
        }

        StringBuilder builder = new(rawWord.Length);
        for (int index = 0; index < rawWord.Length; index++)
        {
            if (rawWord[index] == '\\' && index + 1 < rawWord.Length)
            {
                index++;
            }

            builder.Append(rawWord[index]);
        }

        return builder.ToString();
    }

    /// <summary>Escapes the characters the parser would split or expand.</summary>
    private static string Escape(string name)
    {
        if (name.IndexOfAny(CharactersToEscape.ToCharArray()) < 0)
        {
            return name;
        }

        StringBuilder builder = new(name.Length + 4);
        foreach (char character in name)
        {
            if (CharactersToEscape.Contains(character))
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>Whether only blanks separate <paramref name="wordStart"/> from the line start or a command separator.</summary>
    private static bool IsCommandPosition(string commandLine, int wordStart)
    {
        for (int index = wordStart - 1; index >= 0; index--)
        {
            char character = commandLine[index];
            if (Array.IndexOf(CommandSeparators, character) >= 0)
            {
                return true;
            }

            if (!char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return true;
    }
}
