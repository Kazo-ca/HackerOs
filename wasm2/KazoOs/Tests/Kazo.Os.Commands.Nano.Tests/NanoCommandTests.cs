using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using System.Text;
using Xunit;

namespace Kazo.Os.Commands.Nano.Tests;

public sealed class NanoCommandTests
{
    [Fact]
    public async Task ExecuteAsync_WithoutFullScreen_ReturnsUnsupportedError()
    {
        NanoCommand command = new(AppDefinitionMapper.ToManifest(new NanoApp()));
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        using StringReader stdin = new("");

        TerminalExecutionContext context = new(
            app: new StubAppExecutionContext(AppDefinitionMapper.ToManifest(new NanoApp())),
            arguments: ["sample.txt"],
            standardInput: stdin,
            standardOutput: stdout,
            standardError: stderr,
            workingDirectory: "/home/user",
            environment: new Dictionary<string, string>()
        );

        int exitCode = await command.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("does not provide full-screen editing", stderr.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_EditsSavesAndAlwaysLeavesAlternateScreen()
    {
        TestFileSystemGateway fileSystem = new("hello", "/home/user/sample.txt");
        TestFullScreenTerminal terminal = new(
            new TerminalKeyEvent(TerminalKey.End),
            new TerminalKeyEvent(TerminalKey.Character, "!"),
            new TerminalKeyEvent(TerminalKey.Character, "o", Control: true),
            // ADR 0064: ^O asks "File Name to Write:" prefilled with the file, like GNU nano.
            new TerminalKeyEvent(TerminalKey.Enter),
            new TerminalKeyEvent(TerminalKey.Character, "x", Control: true));
        TerminalExecutionContext context = CreateContext(fileSystem, terminal, "sample.txt");

        int exitCode = await new NanoCommand(AppDefinitionMapper.ToManifest(new NanoApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("hello!", fileSystem.WrittenContent);
        Assert.True(terminal.Entered);
        Assert.True(terminal.Left);
        Assert.NotEmpty(terminal.Frames);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationStillLeavesAlternateScreen()
    {
        TestFileSystemGateway fileSystem = new("hello", "/home/user/sample.txt");
        TestFullScreenTerminal terminal = new() { ThrowCancellationOnRead = true };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new NanoCommand(AppDefinitionMapper.ToManifest(new NanoApp())).ExecuteAsync(
                CreateContext(fileSystem, terminal, "sample.txt"), CancellationToken.None));

        Assert.True(terminal.Entered);
        Assert.True(terminal.Left);
    }

    [Fact]
    public async Task ExecuteAsync_SaveAsCreatesAndWritesTheSelectedVirtualPath()
    {
        TestFileSystemGateway fileSystem = new("hello", "/home/user/sample.txt");
        TestFullScreenTerminal terminal = new(
            new TerminalKeyEvent(TerminalKey.End),
            new TerminalKeyEvent(TerminalKey.Character, "!"),
            new TerminalKeyEvent(TerminalKey.Character, "o", Control: true, Shift: true),
            new TerminalKeyEvent(TerminalKey.Character, "copy.txt"),
            new TerminalKeyEvent(TerminalKey.Enter),
            new TerminalKeyEvent(TerminalKey.Character, "x", Control: true));

        int exitCode = await new NanoCommand(AppDefinitionMapper.ToManifest(new NanoApp())).ExecuteAsync(
            CreateContext(fileSystem, terminal, "sample.txt"), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("/home/user/copy.txt", fileSystem.CreatedPath);
        Assert.Equal("/home/user/copy.txt", fileSystem.WrittenPath);
        Assert.Equal("hello!", fileSystem.WrittenContent);
    }

    [Fact]
    public async Task WriteOut_prompt_is_prefilled_and_editing_it_saves_under_another_name()
    {
        TestFileSystemGateway fileSystem = new("hello", "/home/user/sample.txt");
        List<TerminalKeyEvent> keys = [new(TerminalKey.Character, "o", Control: true)];
        keys.AddRange(Enumerable.Repeat(new TerminalKeyEvent(TerminalKey.Backspace), "sample.txt".Length));
        keys.Add(new(TerminalKey.Character, "renamed.txt"));
        keys.Add(new(TerminalKey.Enter));
        keys.Add(new(TerminalKey.Character, "x", Control: true));
        TestFullScreenTerminal terminal = new([.. keys]);

        int exitCode = await new NanoCommand(AppDefinitionMapper.ToManifest(new NanoApp())).ExecuteAsync(
            CreateContext(fileSystem, terminal, "sample.txt"), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("/home/user/renamed.txt", fileSystem.CreatedPath);
        Assert.Contains(terminal.Frames, frame => frame.Lines.Any(line => line.Contains("File Name to Write: sample.txt")));
    }

    [Fact]
    public async Task Cut_and_paste_move_whole_lines()
    {
        TestFileSystemGateway fileSystem = new("a\nb\nc", "/home/user/sample.txt");
        TestFullScreenTerminal terminal = new(
            new TerminalKeyEvent(TerminalKey.Character, "k", Control: true),
            new TerminalKeyEvent(TerminalKey.ArrowDown),
            new TerminalKeyEvent(TerminalKey.Character, "u", Control: true),
            new TerminalKeyEvent(TerminalKey.Character, "s", Control: true),
            new TerminalKeyEvent(TerminalKey.Character, "x", Control: true));

        int exitCode = await new NanoCommand(AppDefinitionMapper.ToManifest(new NanoApp())).ExecuteAsync(
            CreateContext(fileSystem, terminal, "sample.txt"), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("b\na\nc", fileSystem.WrittenContent);
    }

    [Fact]
    public async Task Where_is_moves_the_cursor_to_the_next_match()
    {
        TestFileSystemGateway fileSystem = new("one\ntwo\nthree", "/home/user/sample.txt");
        TestFullScreenTerminal terminal = new(
            new TerminalKeyEvent(TerminalKey.Character, "w", Control: true),
            new TerminalKeyEvent(TerminalKey.Character, "thr"),
            new TerminalKeyEvent(TerminalKey.Enter),
            new TerminalKeyEvent(TerminalKey.Character, "X"),
            new TerminalKeyEvent(TerminalKey.Character, "s", Control: true),
            new TerminalKeyEvent(TerminalKey.Character, "x", Control: true));

        int exitCode = await new NanoCommand(AppDefinitionMapper.ToManifest(new NanoApp())).ExecuteAsync(
            CreateContext(fileSystem, terminal, "sample.txt"), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("one\ntwo\nXthree", fileSystem.WrittenContent);
    }

    [Fact]
    public async Task A_long_line_scrolls_horizontally_to_keep_the_cursor_visible()
    {
        string longLine = new string('x', 100) + "END";
        TestFileSystemGateway fileSystem = new(longLine, "/home/user/sample.txt");
        TestFullScreenTerminal terminal = new(
            new TerminalKeyEvent(TerminalKey.End),
            new TerminalKeyEvent(TerminalKey.Character, "x", Control: true));

        await new NanoCommand(AppDefinitionMapper.ToManifest(new NanoApp())).ExecuteAsync(
            CreateContext(fileSystem, terminal, "sample.txt"), CancellationToken.None);

        TerminalScreenFrame frame = terminal.Frames[^1];
        Assert.StartsWith("<", frame.Lines[1]);
        Assert.Contains("END", frame.Lines[1]);
        Assert.InRange(frame.Cursor.Column, 1, 79);
        Assert.Equal('D', frame.Lines[1][frame.Cursor.Column - 1]);
    }

    [Fact]
    public async Task ExecuteAsync_ReadCapabilityDenied_FailsBeforeTakingAlternateScreen()
    {
        TestFileSystemGateway fileSystem = new(
            string.Empty, "/home/user/denied.txt", denyRead: true);
        TestFullScreenTerminal terminal = new();
        TerminalExecutionContext context = CreateContext(fileSystem, terminal, "denied.txt");

        int exitCode = await new NanoCommand(AppDefinitionMapper.ToManifest(new NanoApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("CapabilityDenied", context.StandardError.ToString());
        Assert.False(terminal.Entered);
    }

    [Fact]
    public async Task ExecuteAsync_InAFrenchProcess_DrawsTheBarsInFrench()
    {
        TestFileSystemGateway fileSystem = new("hello", "/home/user/sample.txt");
        TestFullScreenTerminal terminal = new(new TerminalKeyEvent(TerminalKey.Character, "x", Control: true));
        TerminalExecutionContext context = new(
            new StubAppExecutionContext(AppDefinitionMapper.ToManifest(new NanoApp()), fileSystem)
            {
                // ADR 0104: what LANG=fr_FR.UTF-8 resolves to for the process.
                Culture = System.Globalization.CultureInfo.GetCultureInfo("fr")
            },
            ["sample.txt"],
            new StringReader(string.Empty),
            new StringWriter(),
            new StringWriter(),
            "/home/user",
            new Dictionary<string, string>(),
            terminal);

        int exitCode = await new NanoCommand(AppDefinitionMapper.ToManifest(new NanoApp())).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        TerminalScreenFrame first = terminal.Frames[0];
        Assert.Contains(first.Lines, line => line.Contains("[ 1 lignes lues ]"));
        Assert.Contains(first.Lines, line => line.Contains("Quitter") && line.Contains("Sauver"));
        Assert.Equal("Éditeur Nano, /home/user/sample.txt", first.AccessibleLabel);
    }

    private static TerminalExecutionContext CreateContext(
        IAppFileSystemGateway fileSystem,
        IFullScreenTerminalSession? terminal,
        params string[] arguments) => new(
            new StubAppExecutionContext(AppDefinitionMapper.ToManifest(new NanoApp()), fileSystem),
            arguments,
            new StringReader(string.Empty),
            new StringWriter(),
            new StringWriter(),
            "/home/user",
            new Dictionary<string, string>(),
            terminal);

    private sealed class StubAppExecutionContext(
        AppManifest manifest,
        IAppFileSystemGateway? fileSystem = null) : IAppExecutionContext
    {
        public AppManifest Manifest { get; } = manifest;
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId => "user";
        public AppAuthority UserAuthority => AppAuthority.User;
        public IReadOnlySet<string> GrantedCapabilities { get; } =
            new HashSet<string>(manifest.Capabilities, StringComparer.Ordinal);
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);
        public CancellationToken CancellationToken => CancellationToken.None;
        public ICapabilityChecker Capabilities => throw new NotSupportedException();
        public IAppFileSystemGateway FileSystem { get; } = fileSystem ?? new TestFileSystemGateway(string.Empty, "/unused");
        public IAppEventGateway Events => throw new NotSupportedException();
        public IAppNotificationGateway Notifications => throw new NotSupportedException();
        public IAppLoggingGateway Logging => throw new NotSupportedException();
        public IAppClockGateway Clock => throw new NotSupportedException();
        public IAppProcessGateway Processes => throw new NotSupportedException();

        /// <summary>The language of the process; English unless a test sets it (ADR 0104).</summary>
        public System.Globalization.CultureInfo Culture { get; init; } = Kazo.Os.AppSdk.Globalization.KazoLocale.DefaultLanguage;
    }

    private sealed class TestFullScreenTerminal(params TerminalKeyEvent[] keys) : IFullScreenTerminalSession
    {
        private readonly Queue<TerminalKeyEvent> _keys = new(keys);
        public TerminalViewport Viewport { get; } = TerminalViewport.Create(80, 24);
        public bool Entered { get; private set; }
        public bool Left { get; private set; }
        public bool ThrowCancellationOnRead { get; init; }
        public List<TerminalScreenFrame> Frames { get; } = [];
        public ValueTask EnterAlternateScreenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entered = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask RenderAsync(TerminalScreenFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frames.Add(frame);
            return ValueTask.CompletedTask;
        }
        public ValueTask<TerminalKeyEvent> ReadKeyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowCancellationOnRead)
                throw new OperationCanceledException(cancellationToken);
            return ValueTask.FromResult(_keys.Dequeue());
        }
        public ValueTask LeaveAlternateScreenAsync(CancellationToken cancellationToken = default)
        {
            Left = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestFileSystemGateway(
        string initialContent,
        string expectedPath,
        bool denyRead = false) : IAppFileSystemGateway
    {
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
        public string? WrittenContent { get; private set; }
        public string? WrittenPath { get; private set; }
        public string? CreatedPath { get; private set; }

        public ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
            FileSystemReadRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal(expectedPath, request.Path.Value);
            if (denyRead)
            {
                return ValueTask.FromResult(FileSystemResult<FileSystemContentReadHandle>.Failure(
                    new FileSystemError(
                        FileSystemOperation.Read, FileSystemErrorCode.CapabilityDenied, request.Path)));
            }
            byte[] bytes = Encoding.UTF8.GetBytes(initialContent);
            return ValueTask.FromResult(FileSystemResult<FileSystemContentReadHandle>.Success(
                new FileSystemContentReadHandle(Snapshot(request.Path, 1, bytes.Length),
                    FileSystemContentDescriptor.Text(), new MemoryStream(bytes, writable: false))));
        }

        public async ValueTask<FileSystemMutationResult> WriteAsync(
            FileSystemWriteRequest request, IFileSystemContentSource content,
            CancellationToken cancellationToken = default)
        {
            await using Stream stream = await content.OpenReadAsync(cancellationToken);
            using StreamReader reader = new(stream, Encoding.UTF8);
            WrittenContent = await reader.ReadToEndAsync(cancellationToken);
            WrittenPath = request.Path.Value;
            return new FileSystemMutationResult(
                FileSystemTransactionResult.Committed(Guid.NewGuid(), [Snapshot(request.Path, 2, Encoding.UTF8.GetByteCount(WrittenContent)).Metadata.Id]),
                Snapshot(request.Path, 2, Encoding.UTF8.GetByteCount(WrittenContent)));
        }

        public ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
            FileSystemStatRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(FileSystemResult<FileSystemEntrySnapshot>.Success(
                DirectorySnapshot(request.Path, 1)));
        public ValueTask<FileSystemMutationResult> CreateAsync(
            FileSystemCreateRequest request, CancellationToken cancellationToken = default)
        {
            CreatedPath = request.Path.Value;
            FileSystemEntrySnapshot entry = Snapshot(request.Path, 1, 0);
            return ValueTask.FromResult(new FileSystemMutationResult(
                FileSystemTransactionResult.Committed(Guid.NewGuid(), [entry.Metadata.Id]), entry));
        }
        public ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(FileSystemEnumerateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileSystemMutationResult> MoveAsync(FileSystemMoveRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileSystemMutationResult> CopyAsync(FileSystemCopyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileSystemMutationResult> DeleteAsync(FileSystemDeleteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileSystemMutationResult> SetPermissionsAsync(FileSystemSetPermissionsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAppFileSystemGateway WithSelectedHandle(FileSystemSelectedResourceHandle handle) => this;

        private FileSystemEntrySnapshot Snapshot(VirtualPath path, long revision, long length) => new(
            path,
            new FileMetadata(FileSystemEntryId.FromGuid(Guid.NewGuid()), "user", "users",
                FileSystemPermissions.FromMode(0b110_100_100),
                new FileSystemTimestamps(_now, _now, _now), revision, length, "text/plain"));

        private FileSystemEntrySnapshot DirectorySnapshot(VirtualPath path, long revision) => new(
            path,
            new DirectoryMetadata(FileSystemEntryId.FromGuid(Guid.NewGuid()), "user", "users",
                FileSystemPermissions.FromMode(0b111_101_101),
                new FileSystemTimestamps(_now, _now, _now), revision));
    }
}
