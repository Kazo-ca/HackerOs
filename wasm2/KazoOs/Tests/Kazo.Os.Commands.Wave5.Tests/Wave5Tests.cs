using System.Collections.Immutable;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Apps.Calculator;
using Kazo.Os.Apps.HackPaint;
using Kazo.Os.Commands.Alias;
using Kazo.Os.Commands.Clear;
using Kazo.Os.Commands.Diff;
using Kazo.Os.Commands.Grep;
using Kazo.Os.Commands.Head;
using Kazo.Os.Commands.Help;
using Kazo.Os.Commands.Launch;
using Kazo.Os.Commands.Ps;
using Kazo.Os.Commands.Sort;
using Kazo.Os.Commands.Tail;
using Kazo.Os.Commands.Wc;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Commands.Wave5.Tests;

/// <summary>
/// Unit test suite for Phase 4 Wave 5 (Utility Apps & Terminal Commands).
/// Validates CalculatorEngine, PaintCanvasState, grep, head/tail, sort, wc,
/// diff, ps, launch, clear, help/man, and alias commands.
/// </summary>
public sealed class Wave5Tests
{
    // ── Calculator Engine Unit Tests ──────────────────────────────────────

    [Fact]
    public void CalculatorEngine_PerformsBasicArithmetic()
    {
        var engine = new CalculatorEngine();
        engine.InputDigit("5");
        engine.HandleOperator("+");
        engine.InputDigit("3");
        engine.HandleEquals();

        Assert.Equal("8", engine.CurrentValue);
    }

    [Fact]
    public void CalculatorEngine_HandlesSquareRoot_And_Memory()
    {
        var engine = new CalculatorEngine();
        engine.InputDigit("9");
        engine.SquareRoot();
        Assert.Equal("3", engine.CurrentValue);

        engine.MemoryAdd();
        engine.AllClear();
        Assert.Equal("0", engine.CurrentValue);

        engine.MemoryRecall();
        Assert.Equal("3", engine.CurrentValue);
    }

    // ── Paint Canvas State Unit Tests ──────────────────────────────────────

    [Fact]
    public void PaintCanvasState_NewDocument_And_Rotate90()
    {
        var state = new PaintCanvasState();
        state.NewDocument(100, 50);

        Assert.Equal(100, state.Width);
        Assert.Equal(50, state.Height);

        state.Rotate90();
        Assert.Equal(50, state.Width);
        Assert.Equal(100, state.Height);
        Assert.True(state.CanUndo);

        state.Undo();
        Assert.Equal(100, state.Width);
        Assert.Equal(50, state.Height);
    }

    [Fact]
    public void PaintCanvasState_DrawUndoRedo_RestoresActualPixels()
    {
        var state = new PaintCanvasState();
        state.NewDocument(16, 16);
        var before = state.GetCurrentBuffer();

        state.DrawLine(2, 2, 2, 2, "#ff0000", 1);
        var painted = state.GetCurrentBuffer();
        Assert.NotEqual(before, painted);
        Assert.Equal((byte)255, painted[(2 * 16 + 2) * 4]);

        state.Undo();
        Assert.Equal(before, state.GetCurrentBuffer());
        state.Redo();
        Assert.Equal(painted, state.GetCurrentBuffer());
    }

    [Fact]
    public void PaintCanvasState_CropAndRotate_PreservePixelContent()
    {
        var state = new PaintCanvasState();
        state.NewDocument(16, 16);
        state.DrawLine(3, 4, 3, 4, "#0000ff", 1);
        state.Crop(2, 3, 4, 5);

        Assert.Equal(4, state.Width);
        Assert.Equal(5, state.Height);
        Assert.Equal((byte)255, state.GetCurrentBuffer()[(1 * 4 + 1) * 4 + 2]);

        state.Rotate90();
        Assert.Equal(5, state.Width);
        Assert.Equal(4, state.Height);
        Assert.Contains((byte)255, state.GetCurrentBuffer());
    }

    [Fact]
    public void PaintCanvasState_Pan_DoesNotMutatePixelsOrHistory()
    {
        var state = new PaintCanvasState();
        state.NewDocument(16, 16);
        var pixels = state.GetCurrentBuffer();
        state.PanBy(12, -4);

        Assert.Equal(12, state.PanX);
        Assert.Equal(-4, state.PanY);
        Assert.False(state.CanUndo);
        Assert.Equal(pixels, state.GetCurrentBuffer());
    }

    // ── Command Unit Tests ────────────────────────────────────────────────

    [Fact]
    public async Task GrepCommand_FiltersMatchingLines()
    {
        var cmd = new GrepCommand(AppDefinitionMapper.ToManifest(new GrepApp()));
        using var stdin = new StringReader("apple\nbanana\ncherry\nAPRICOT\n");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var ctx = CreateContext(["-i", "ap"], stdin, stdout, stderr);
        int exitCode = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("apple", output);
        Assert.Contains("APRICOT", output);
        Assert.DoesNotContain("banana", output);
    }

    [Fact]
    public async Task HeadCommand_OutputsFirstNLines()
    {
        var cmd = new HeadCommand(AppDefinitionMapper.ToManifest(new HeadApp()));
        using var stdin = new StringReader("1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12\n");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var ctx = CreateContext(["-n", "3"], stdin, stdout, stderr);
        int exitCode = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Equal("1\n2\n3\n", output.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task TailCommand_OutputsLastNLines()
    {
        var cmd = new TailCommand(AppDefinitionMapper.ToManifest(new TailApp()));
        using var stdin = new StringReader("1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var ctx = CreateContext(["-n", "2"], stdin, stdout, stderr);
        int exitCode = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Equal("9\n10\n", output.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task SortCommand_SortsLinesNumericallyAndReverses()
    {
        var cmd = new SortCommand(AppDefinitionMapper.ToManifest(new SortApp()));
        using var stdin = new StringReader("10\n2\n1\n");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var ctx = CreateContext(["-n", "-r"], stdin, stdout, stderr);
        int exitCode = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Equal("10\n2\n1\n", output.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task WcCommand_CountsLinesWordsBytes()
    {
        var cmd = new WcCommand(AppDefinitionMapper.ToManifest(new WcApp()));
        using var stdin = new StringReader("hello world\nfoo bar\n");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var ctx = CreateContext([], stdin, stdout, stderr);
        int exitCode = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("2", output); // 2 lines
        Assert.Contains("4", output); // 4 words
    }

    [Fact]
    public async Task ClearCommand_EmitsAnsiClearSequence()
    {
        var cmd = new ClearCommand(AppDefinitionMapper.ToManifest(new ClearApp()));
        using var stdin = new StringReader("");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var ctx = CreateContext([], stdin, stdout, stderr);
        int exitCode = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("\x1b[H\x1b[2J\x1b[3J", stdout.ToString());
        Assert.Equal(ClearCommand.ClearSequence, stdout.ToString());
    }

    [Fact]
    public async Task HelpCommand_lists_the_shells_installed_commands_or_one_usage()
    {
        var cmd = new HelpCommand(AppDefinitionMapper.ToManifest(new HelpApp()));
        FakeShellSession shell = new()
        {
            Commands =
            [
                new ShellCommandInfo("ping", [], "ping <hostname|ip>", "Send ICMP echo requests"),
                new ShellCommandInfo("help", ["man"], "help [command]", "Display help")
            ]
        };
        using var stdin = new StringReader("");
        using var listing = new StringWriter();
        using var usage = new StringWriter();
        using var stderr = new StringWriter();

        int listed = await cmd.ExecuteAsync(CreateContext([], stdin, listing, stderr, shell), CancellationToken.None);
        int described = await cmd.ExecuteAsync(CreateContext(["man"], stdin, usage, stderr, shell), CancellationToken.None);
        int missing = await cmd.ExecuteAsync(CreateContext(["nope"], stdin, TextWriter.Null, stderr, shell), CancellationToken.None);
        int noShell = await cmd.ExecuteAsync(CreateContext([], stdin, TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal([0, 0, 1, 1], [listed, described, missing, noShell]);
        Assert.Contains("ping  Send ICMP echo requests", listing.ToString());
        Assert.Contains("usage: help [command]", usage.ToString());
        Assert.Contains("aliases: man", usage.ToString());
    }

    // AliasCommand now persists through the real filesystem gateway (see AliasCommandTests.cs,
    // which uses Kazo.Os.Tests.Support's FakeAppFileSystemGateway) instead of an in-memory
    // dictionary, so it needs a FileSystem-capable context that this file's FileSystem-less
    // StubAppExecutionContext cannot provide.

    // ── Helper ─────────────────────────────────────────────────────────────

    private static TerminalExecutionContext CreateContext(
        string[] args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        IShellSession? shell = null)
    {
        var manifest = AppDefinitionMapper.ToManifest(new GrepApp());
        var app = new StubAppExecutionContext(manifest);

        return new TerminalExecutionContext(
            app: app,
            arguments: args.ToImmutableArray(),
            standardInput: stdin,
            standardOutput: stdout,
            standardError: stderr,
            workingDirectory: "/home/user",
            environment: ImmutableDictionary<string, string>.Empty,
            shell: shell);
    }

    private sealed class StubAppExecutionContext : IAppExecutionContext
    {
        public StubAppExecutionContext(AppManifest manifest) => Manifest = manifest;
        public AppManifest Manifest { get; }
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId => "user";
        public AppAuthority UserAuthority => AppAuthority.User;
        public IReadOnlySet<string> GrantedCapabilities => new HashSet<string>(Manifest.Capabilities);
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);
        public CancellationToken CancellationToken => CancellationToken.None;
        public ICapabilityChecker Capabilities => throw new NotImplementedException();
        public IAppFileSystemGateway FileSystem => throw new NotImplementedException();
        public IAppEventGateway Events => throw new NotImplementedException();
        public IAppNotificationGateway Notifications => throw new NotImplementedException();
        public IAppLoggingGateway Logging => throw new NotImplementedException();
        public IAppClockGateway Clock => throw new NotImplementedException();
        public IAppProcessGateway Processes => throw new NotImplementedException();
    }
}
