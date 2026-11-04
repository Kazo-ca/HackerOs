using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using System.Text;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Nano;

/// <summary>
/// GNU nano-style terminal text editor backed by the capability-checked VFS, drawn on the
/// terminal's alternate screen (ADR 0061; key bindings and layout reworked by ADR 0064 for the
/// xterm.js terminal). Layout: an inverse title bar, the buffer (scrolled vertically and, per
/// line, horizontally; tabs expanded to 8 columns), a status line and two rows of shortcuts.
/// </summary>
/// <remarks>
/// <para>
/// Keys: <c>^O</c> Write Out (asks the file name, prefilled; Enter writes, <c>^C</c> cancels),
/// <c>^S</c> save at once, <c>^X</c> exit (asks to save a modified buffer), <c>^W</c>/<c>^F</c>
/// Where Is (search forward, wraps; an empty answer repeats the last search), <c>^K</c> cut the
/// line (consecutive cuts accumulate), <c>^U</c> paste, <c>^C</c> cursor position, <c>^G</c>
/// help, <c>^A</c>/<c>^E</c> line start/end, <c>^Y</c>/<c>^V</c> and PageUp/PageDown page,
/// arrows, Home, End, Backspace, Delete, Tab.
/// </para>
/// <para>
/// Ctrl+Shift+O cannot be told from Ctrl+O by a real terminal (both send <c>0x0F</c>), so
/// "save as" is the Write Out prompt itself, as in nano; a renderer that does report Shift
/// gets an empty prompt.
/// </para>
/// </remarks>
public sealed class NanoCommand : TerminalAppBase
{
    private const int MaxDocumentBytes = 1024 * 1024;
    private const int TabSize = 8;
    private const string Inverse = "\u001b[7m";
    private const string Plain = "\u001b[0m";

    /// <summary>Initializes the nano command with its validated manifest.</summary>
    public NanoCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc/>
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();

        if (context.FullScreen is null)
        {
            context.StandardError.WriteLine(text["NoFullScreen"]);
            return 2;
        }

        // `nano +LINE file` opens at a line, like GNU nano.
        int startLine = 1;
        List<string> files = [];
        foreach (string argument in context.Arguments)
        {
            if (argument.StartsWith('+') && int.TryParse(argument[1..].Split(',')[0], out int line))
            {
                startLine = line;
            }
            else if (!argument.StartsWith('-'))
            {
                files.Add(argument);
            }
        }

        string targetFile = files.Count > 0 ? files[0] : "new_file.txt";
        VirtualPath path = VirtualPath.Parse(ResolvePath(context.WorkingDirectory, targetFile));
        LoadResult load = await LoadAsync(context, text, path, cancellationToken);
        if (!load.Succeeded)
        {
            context.StandardError.WriteLine(text["OpenFailed", targetFile, load.Error!]);
            return 1;
        }

        EditorState state = new(text, path, targetFile, load.Content!, load.Revision)
        {
            Status = load.Revision is null ? text["Status.NewFile"] : text["Status.ReadLines", CountLines(load.Content!)]
        };
        state.Row = Math.Clamp(startLine - 1, 0, state.Lines.Count - 1);
        IFullScreenTerminalSession terminal = context.FullScreen;
        await terminal.EnterAlternateScreenAsync(cancellationToken);
        try
        {
            while (true)
            {
                await RenderAsync(terminal, state, cancellationToken);
                TerminalKeyEvent key = await terminal.ReadKeyAsync(cancellationToken);
                bool cutting = false;
                if (key.Key is TerminalKey.Character && key.Control)
                {
                    switch (key.Text?.ToLowerInvariant())
                    {
                        case "o":
                            string? name = await PromptAsync(
                                terminal, state, text["Prompt.FileName"], key.Shift ? string.Empty : state.DisplayName, cancellationToken);
                            if (string.IsNullOrWhiteSpace(name))
                            {
                                state.Status = text["Status.Cancelled"];
                                continue;
                            }

                            if (name != state.DisplayName)
                            {
                                state.Path = VirtualPath.Parse(ResolvePath(context.WorkingDirectory, name));
                                state.DisplayName = name;
                                state.Revision = null;
                            }

                            await WriteOutAsync(context, state, cancellationToken);
                            continue;
                        case "s":
                            await WriteOutAsync(context, state, cancellationToken);
                            continue;
                        case "x":
                            if (!state.Dirty || await ConfirmExitAsync(context, terminal, state, cancellationToken))
                            {
                                return 0;
                            }

                            continue;
                        case "w" or "f":
                            string? query = await PromptAsync(terminal, state, LastSearchLabel(state), string.Empty, cancellationToken);
                            if (query is null)
                            {
                                state.Status = text["Status.Cancelled"];
                                continue;
                            }

                            Search(state, query.Length == 0 ? state.LastSearch : query);
                            continue;
                        case "k":
                            CutLine(state, appendToBuffer: state.LastKeyWasCut);
                            cutting = true;
                            break;
                        case "u":
                            Paste(state);
                            break;
                        case "c":
                            state.Status = Position(state);
                            break;
                        case "g":
                            await ShowHelpAsync(terminal, text, cancellationToken);
                            break;
                        case "a":
                            state.Column = 0;
                            break;
                        case "e":
                            state.Column = state.Lines[state.Row].Length;
                            break;
                        case "y":
                            MovePage(state, terminal, -1);
                            break;
                        case "v":
                            MovePage(state, terminal, 1);
                            break;
                    }
                }
                else
                {
                    switch (key.Key)
                    {
                        case TerminalKey.PageUp:
                            MovePage(state, terminal, -1);
                            break;
                        case TerminalKey.PageDown:
                            MovePage(state, terminal, 1);
                            break;
                        default:
                            ApplyKey(state, key);
                            break;
                    }
                }

                state.LastKeyWasCut = cutting;
            }
        }
        finally
        {
            // Cleanup must not be skipped merely because command cancellation fired.
            await terminal.LeaveAlternateScreenAsync(CancellationToken.None);
        }
    }

    private static async ValueTask WriteOutAsync(TerminalExecutionContext context, EditorState state, CancellationToken cancellationToken)
    {
        int lines = state.Lines.Count - (state.Lines[^1].Length == 0 && state.Lines.Count > 1 ? 1 : 0);
        state.Status = await SaveAsync(context, state, cancellationToken)
            ? state.Text[lines == 1 ? "Status.WroteOne" : "Status.WroteMany", lines]
            : state.Status ?? state.Text["Status.WriteError"];
    }

    private static string LastSearchLabel(EditorState state) =>
        state.LastSearch.Length > 0 ? state.Text["Prompt.SearchLast", state.LastSearch] : state.Text["Prompt.Search"];

    private static int CountLines(string content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        int lines = content.Count(ch => ch == '\n');
        return content.EndsWith('\n') ? lines : lines + 1;
    }

    private static async ValueTask<LoadResult> LoadAsync(
        TerminalExecutionContext context, IStringLocalizer<Strings> text, VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemContentReadHandle> result = await context.App.FileSystem.ReadAsync(
            new FileSystemReadRequest(path), cancellationToken);
        if (!result.Succeeded || result.Value is null)
        {
            return result.Error?.Code == FileSystemErrorCode.NotFound
                ? new LoadResult(true, string.Empty, null, null)
                : new LoadResult(false, null, null, result.Error?.Code.ToString() ?? text["Error.ReadFailed"]);
        }

        await using FileSystemContentReadHandle handle = result.Value;
        if (handle.Entry.Metadata is FileMetadata fileMetadata && fileMetadata.Length > MaxDocumentBytes)
            return new LoadResult(false, null, null, text["Error.TooLarge", MaxDocumentBytes]);

        using StreamReader reader = new(handle.Content, Encoding.UTF8, true, leaveOpen: true);
        string content = await reader.ReadToEndAsync(cancellationToken);
        return Encoding.UTF8.GetByteCount(content) > MaxDocumentBytes
            ? new LoadResult(false, null, null, text["Error.TooLarge", MaxDocumentBytes])
            : new LoadResult(true, content, handle.Entry.Metadata.Revision, null);
    }

    private static async ValueTask<bool> SaveAsync(
        TerminalExecutionContext context, EditorState state, CancellationToken cancellationToken)
    {
        string content = state.GetContent();
        if (Encoding.UTF8.GetByteCount(content) > MaxDocumentBytes)
        {
            state.Status = state.Text["Status.BufferTooLarge", MaxDocumentBytes];
            return false;
        }

        if (state.Revision is null)
        {
            VirtualPath parent = ParentOf(state.Path);
            FileSystemResult<FileSystemEntrySnapshot> parentStat = await context.App.FileSystem.StatAsync(
                new FileSystemStatRequest(parent), cancellationToken);
            if (!parentStat.Succeeded || parentStat.Value is null)
                return false;

            FileSystemMutationResult created = await context.App.FileSystem.CreateAsync(
                new FileSystemCreateRequest(state.Path, FileSystemEntryKind.File,
                    FileSystemPermissions.FromMode(0b110_100_100), parentStat.Value.Metadata.Revision),
                cancellationToken);
            if (!created.Succeeded || created.Entry is null)
                return false;
            state.Revision = created.Entry.Metadata.Revision;
        }

        FileSystemMutationResult written = await context.App.FileSystem.WriteAsync(
            new FileSystemWriteRequest(state.Path, state.Revision.Value),
            new Utf8TextSource(content), cancellationToken);
        if (!written.Succeeded || written.Entry is null)
            return false;

        state.Revision = written.Entry.Metadata.Revision;
        state.Dirty = false;
        return true;
    }

    private static async ValueTask<bool> ConfirmExitAsync(
        TerminalExecutionContext context,
        IFullScreenTerminalSession terminal,
        EditorState state,
        CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = state.Text;
        state.Prompt = text["Prompt.SaveModified"];
        try
        {
            while (true)
            {
                await RenderAsync(terminal, state, cancellationToken);
                TerminalKeyEvent key = await terminal.ReadKeyAsync(cancellationToken);
                // Like GNU nano, the answers follow the language ("O Oui" in French); "y"/"n" always work.
                if (IsCharacter(key, "n") || IsCharacter(key, text["Prompt.NoKey"]))
                {
                    return true;
                }

                if (IsCharacter(key, "c") || key.Key is TerminalKey.Escape)
                {
                    state.Status = text["Status.Cancelled"];
                    return false;
                }

                if (IsCharacter(key, "y") || IsCharacter(key, text["Prompt.YesKey"]))
                {
                    state.Prompt = null;
                    bool saved = await SaveAsync(context, state, cancellationToken);
                    state.Status = saved ? text["Status.WroteFile", state.Path.Value] : text["Status.WriteErrorExitCancelled"];
                    return saved;
                }
            }
        }
        finally
        {
            state.Prompt = null;
        }
    }

    /// <summary>Reads an answer on the status line; null when cancelled (^C, Escape).</summary>
    private static async ValueTask<string?> PromptAsync(
        IFullScreenTerminalSession terminal,
        EditorState state,
        string prompt,
        string initial,
        CancellationToken cancellationToken)
    {
        StringBuilder input = new(initial);
        try
        {
            while (true)
            {
                state.Prompt = prompt + input;
                await RenderAsync(terminal, state, cancellationToken);
                TerminalKeyEvent key = await terminal.ReadKeyAsync(cancellationToken);
                if (key.Key is TerminalKey.Enter)
                {
                    return input.ToString();
                }

                if (key.Key is TerminalKey.Escape || (key.Control && IsCharacter(key, "c")))
                {
                    return null;
                }

                if (key.Key is TerminalKey.Backspace && input.Length > 0)
                {
                    input.Length--;
                }
                else if (key.Control && IsCharacter(key, "u"))
                {
                    input.Clear();
                }
                else if (key.Key is TerminalKey.Character && !key.Control && !key.Alt && key.Text is not null)
                {
                    input.Append(key.Text);
                }
            }
        }
        finally
        {
            state.Prompt = null;
        }
    }

    private static void ApplyKey(EditorState state, TerminalKeyEvent key)
    {
        StringBuilder line = state.Lines[state.Row];
        state.Column = Math.Min(state.Column, line.Length);
        switch (key.Key)
        {
            case TerminalKey.Character when !key.Control && !key.Alt && !string.IsNullOrEmpty(key.Text):
                line.Insert(state.Column, key.Text);
                state.Column += key.Text.Length;
                state.Dirty = true;
                break;
            case TerminalKey.Enter:
                string remainder = line.ToString(state.Column, line.Length - state.Column);
                line.Remove(state.Column, line.Length - state.Column);
                state.Lines.Insert(++state.Row, new StringBuilder(remainder));
                state.Column = 0;
                state.Dirty = true;
                break;
            case TerminalKey.Backspace when state.Column > 0:
                line.Remove(--state.Column, 1);
                state.Dirty = true;
                break;
            case TerminalKey.Backspace when state.Row > 0:
                int previousLength = state.Lines[state.Row - 1].Length;
                state.Lines[state.Row - 1].Append(line);
                state.Lines.RemoveAt(state.Row--);
                state.Column = previousLength;
                state.Dirty = true;
                break;
            case TerminalKey.Delete when state.Column < line.Length:
                line.Remove(state.Column, 1);
                state.Dirty = true;
                break;
            case TerminalKey.Delete when state.Row + 1 < state.Lines.Count:
                line.Append(state.Lines[state.Row + 1]);
                state.Lines.RemoveAt(state.Row + 1);
                state.Dirty = true;
                break;
            case TerminalKey.ArrowLeft when state.Column > 0:
                state.Column--;
                break;
            case TerminalKey.ArrowLeft when state.Row > 0:
                state.Row--;
                state.Column = state.Lines[state.Row].Length;
                break;
            case TerminalKey.ArrowRight when state.Column < line.Length:
                state.Column++;
                break;
            case TerminalKey.ArrowRight when state.Row + 1 < state.Lines.Count:
                state.Row++;
                state.Column = 0;
                break;
            case TerminalKey.ArrowUp when state.Row > 0:
                state.Row--;
                state.Column = Math.Min(state.Column, state.Lines[state.Row].Length);
                break;
            case TerminalKey.ArrowDown when state.Row + 1 < state.Lines.Count:
                state.Row++;
                state.Column = Math.Min(state.Column, state.Lines[state.Row].Length);
                break;
            case TerminalKey.Home:
                state.Column = 0;
                break;
            case TerminalKey.End:
                state.Column = line.Length;
                break;
        }
    }

    /// <summary>^K: cuts the current line into the cut buffer (consecutive cuts add to it).</summary>
    private static void CutLine(EditorState state, bool appendToBuffer)
    {
        if (!appendToBuffer)
        {
            state.CutBuffer.Clear();
        }

        state.CutBuffer.Add(state.Lines[state.Row].ToString());
        if (state.Lines.Count == 1)
        {
            state.Lines[0].Clear();
        }
        else
        {
            state.Lines.RemoveAt(state.Row);
            state.Row = Math.Min(state.Row, state.Lines.Count - 1);
        }

        state.Column = 0;
        state.Dirty = true;
    }

    /// <summary>^U: pastes the cut lines above the current one.</summary>
    private static void Paste(EditorState state)
    {
        if (state.CutBuffer.Count == 0)
        {
            state.Status = state.Text["Status.CutBufferEmpty"];
            return;
        }

        state.Lines.InsertRange(state.Row, state.CutBuffer.Select(text => new StringBuilder(text)));
        state.Row += state.CutBuffer.Count;
        state.Column = 0;
        state.Dirty = true;
    }

    /// <summary>^W: the next occurrence after the cursor, wrapping to the top, case-insensitive like nano's default.</summary>
    private static void Search(EditorState state, string query)
    {
        if (query.Length == 0)
        {
            state.Status = state.Text["Status.NoSearchPattern"];
            return;
        }

        state.LastSearch = query;
        int count = state.Lines.Count;
        for (int step = 0; step <= count; step++)
        {
            int row = (state.Row + step) % count;
            string text = state.Lines[row].ToString();
            int from = step == 0 ? Math.Min(state.Column + 1, text.Length) : 0;
            int found = text.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
            if (found >= 0)
            {
                if (step > 0 && row <= state.Row)
                {
                    state.Status = state.Text["Status.SearchWrapped"];
                }

                state.Row = row;
                state.Column = found;
                return;
            }
        }

        state.Status = state.Text["Status.NotFound", query];
    }

    private static string Position(EditorState state)
    {
        int lines = state.Lines.Count;
        int column = state.Column + 1;
        int lineLength = state.Lines[state.Row].Length + 1;
        int totalChars = state.Lines.Sum(line => line.Length + 1) - 1;
        int charIndex = state.Lines.Take(state.Row).Sum(line => line.Length + 1) + state.Column;
        return state.Text["Status.Position",
            state.Row + 1, lines, 100 * (state.Row + 1) / lines,
            column, lineLength, 100 * column / lineLength,
            charIndex, totalChars, totalChars == 0 ? 0 : 100 * charIndex / totalChars];
    }

    private static void MovePage(EditorState state, IFullScreenTerminalSession terminal, int direction)
    {
        int page = Math.Max(1, BodyRows(terminal.Viewport.Rows) - 2);
        state.Row = Math.Clamp(state.Row + (direction * page), 0, state.Lines.Count - 1);
        state.TopRow = Math.Clamp(state.TopRow + (direction * page), 0, Math.Max(0, state.Lines.Count - 1));
        state.Column = Math.Min(state.Column, state.Lines[state.Row].Length);
    }

    private static async ValueTask ShowHelpAsync(IFullScreenTerminalSession terminal, IStringLocalizer<Strings> text, CancellationToken cancellationToken)
    {
        string[] help =
        [
            Inverse + text["Help.Title"] + Plain,
            string.Empty,
            text["Help.Intro"],
            string.Empty,
            text["Help.WriteOut"],
            text["Help.Save"],
            text["Help.Exit"],
            text["Help.WhereIs"],
            text["Help.Cut"],
            text["Help.Paste"],
            text["Help.Location"],
            text["Help.LineEnds"],
            text["Help.Pages"],
            string.Empty,
            text["Help.Return"]
        ];
        await terminal.RenderAsync(new TerminalScreenFrame(help, new TerminalCursor(0, 0, Visible: false), text["Frame.Help"]), cancellationToken);
        await terminal.ReadKeyAsync(cancellationToken);
    }

    private static int BodyRows(int height) => Math.Max(1, height - 4);

    private static ValueTask RenderAsync(
        IFullScreenTerminalSession terminal, EditorState state, CancellationToken cancellationToken)
    {
        int width = Math.Max(1, terminal.Viewport.Columns);
        int height = Math.Max(5, terminal.Viewport.Rows);
        int bodyRows = BodyRows(height);
        state.Row = Math.Clamp(state.Row, 0, state.Lines.Count - 1);
        state.Column = Math.Clamp(state.Column, 0, state.Lines[state.Row].Length);
        if (state.Row < state.TopRow)
        {
            state.TopRow = state.Row;
        }

        if (state.Row >= state.TopRow + bodyRows)
        {
            state.TopRow = state.Row - bodyRows + 1;
        }

        // Title bar: version on the left, file in the middle, "Modified" on the right.
        string left = "  GNU nano 7.2";
        IStringLocalizer<Strings> text = state.Text;
        string middle = state.Revision is null && !state.Dirty ? text["Title.NewBuffer", state.DisplayName] : state.DisplayName;
        string right = state.Dirty ? text["Title.Modified"] + "  " : string.Empty;
        int middleStart = Math.Max(left.Length + 1, (width - middle.Length) / 2);
        string title = (left.PadRight(middleStart) + middle).PadRight(Math.Max(0, width - right.Length)) + right;
        List<string> lines = [Inverse + Fit(title, width).PadRight(width) + Plain];

        // The cursor line scrolls horizontally when it is wider than the screen, nano-style.
        int cursorColumn = DisplayColumn(state.Lines[state.Row], state.Column);
        int offset = cursorColumn < width - 1 ? 0 : (cursorColumn - (width - 1) + 1 + (width / 2) - 1) / Math.Max(1, width / 2) * Math.Max(1, width / 2);
        for (int index = 0; index < bodyRows; index++)
        {
            int sourceRow = state.TopRow + index;
            if (sourceRow >= state.Lines.Count)
            {
                lines.Add(string.Empty);
                continue;
            }

            string expanded = ExpandTabs(state.Lines[sourceRow].ToString());
            int start = sourceRow == state.Row ? offset : 0;
            string visible = start < expanded.Length ? expanded[start..] : string.Empty;
            if (start > 0)
            {
                visible = "<" + (visible.Length > 0 ? visible[1..] : string.Empty);
            }

            lines.Add(visible.Length > width ? visible[..(width - 1)] + ">" : visible);
        }

        string status = state.Prompt is { } prompt
            ? Inverse + Fit(prompt, width).PadRight(width) + Plain
            : state.Status is { } message
                ? new string(' ', Math.Max(0, (width - message.Length - 4) / 2)) + Inverse + Fit($"[ {message} ]", width) + Plain
                : string.Empty;
        lines.Add(status);
        lines.Add(Shortcuts(width, ("^G", text["Shortcut.Help"]), ("^O", text["Shortcut.WriteOut"]), ("^W", text["Shortcut.WhereIs"]), ("^K", text["Shortcut.Cut"]), ("^C", text["Shortcut.Location"]), ("^Y", text["Shortcut.PrevPage"])));
        lines.Add(Shortcuts(width, ("^X", text["Shortcut.Exit"]), ("^S", text["Shortcut.Save"]), ("^F", text["Shortcut.Search"]), ("^U", text["Shortcut.Paste"]), ("^A", text["Shortcut.LineStart"]), ("^V", text["Shortcut.NextPage"])));
        if (state.Prompt is null)
        {
            state.Status = null;
        }

        int cursorRow = state.Prompt is null ? state.Row - state.TopRow + 1 : height - 3;
        int cursorX = state.Prompt is { } answer ? Math.Min(answer.Length, width - 1) : Math.Clamp(cursorColumn - offset, 0, width - 1);
        return terminal.RenderAsync(new TerminalScreenFrame(
            lines,
            new TerminalCursor(cursorX, cursorRow),
            text["Frame.Editor", state.Path.Value]), cancellationToken);
    }

    private static string Shortcuts(int width, params (string Key, string Label)[] items)
    {
        int cell = Math.Max(8, width / items.Length);
        StringBuilder row = new();
        foreach ((string key, string label) in items)
        {
            row.Append(Inverse).Append(key).Append(Plain).Append(' ').Append(Fit(label, cell - key.Length - 1).PadRight(cell - key.Length - 1));
        }

        return row.ToString();
    }

    /// <summary>Screen column of a text index, tabs advancing to the next multiple of 8.</summary>
    private static int DisplayColumn(StringBuilder line, int index)
    {
        int column = 0;
        for (int i = 0; i < Math.Min(index, line.Length); i++)
        {
            column = line[i] == '\t' ? (column / TabSize + 1) * TabSize : column + 1;
        }

        return column;
    }

    private static string ExpandTabs(string text)
    {
        if (!text.Contains('\t'))
        {
            return text;
        }

        StringBuilder expanded = new();
        foreach (char ch in text)
        {
            if (ch == '\t')
            {
                expanded.Append(' ', TabSize - (expanded.Length % TabSize));
            }
            else
            {
                expanded.Append(ch);
            }
        }

        return expanded.ToString();
    }

    private static bool IsCharacter(TerminalKeyEvent key, string value) =>
        key.Key is TerminalKey.Character
        && string.Equals(key.Text, value, StringComparison.OrdinalIgnoreCase);

    private static string Fit(string value, int width) =>
        value.Length <= width ? value : value[..Math.Max(0, width)];

    private static string ResolvePath(string cwd, string target) =>
        target.StartsWith('/') ? target : $"{cwd.TrimEnd('/')}/{target}";

    private static VirtualPath ParentOf(VirtualPath path)
    {
        int separator = path.Value.LastIndexOf('/');
        return VirtualPath.Parse(separator <= 0 ? "/" : path.Value[..separator]);
    }

    private sealed record LoadResult(bool Succeeded, string? Content, long? Revision, string? Error);

    private sealed class EditorState
    {
        public EditorState(IStringLocalizer<Strings> text, VirtualPath path, string displayName, string content, long? revision)
        {
            Text = text;
            Path = path;
            DisplayName = displayName;
            Revision = revision;
            Lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                .Select(static line => new StringBuilder(line)).ToList();
            if (Lines.Count == 0)
                Lines.Add(new StringBuilder());
        }

        /// <summary>The editor's texts, in the language of the nano process (ADR 0104).</summary>
        public IStringLocalizer<Strings> Text { get; }
        public VirtualPath Path { get; set; }
        public string DisplayName { get; set; }
        public List<StringBuilder> Lines { get; }
        public long? Revision { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public int TopRow { get; set; }
        public bool Dirty { get; set; }
        public string? Status { get; set; }
        public string? Prompt { get; set; }
        public string LastSearch { get; set; } = string.Empty;
        public List<string> CutBuffer { get; } = [];
        public bool LastKeyWasCut { get; set; }
        public string GetContent() => string.Join('\n', Lines.Select(static line => line.ToString()));
    }

    private sealed class Utf8TextSource(string content) : IFileSystemContentSource
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(content);
        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text();
        public long? Length => _bytes.LongLength;
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
        }
    }
}
