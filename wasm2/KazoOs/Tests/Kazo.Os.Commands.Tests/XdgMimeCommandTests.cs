using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.Commands.XdgMime;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Commands.Tests;

/// <summary><c>xdg-mime</c> (ADR 0121, section 5): query and set the user's default applications.</summary>
public sealed class XdgMimeCommandTests
{
    private const string Notepad = "org.kazoos.notepad";
    private const string Editor = "org.kazoos.text-editor";

    [Fact]
    public async Task Query_default_prints_the_users_choice_before_the_systems()
    {
        FakeAppCatalogGateway catalog = Catalog().WithSystemDefault("text/plain", Notepad);
        catalog.UserDefaults.Add(new KazoFileAssociation("text/plain", "open", Editor, KazoFileAssociationLayer.User));

        Run run = await RunAsync(catalog, "query", "default", "text/plain");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Editor, run.Stdout.Trim());
    }

    [Fact]
    public async Task Query_default_of_a_type_without_default_prints_nothing()
    {
        Run run = await RunAsync(Catalog(), "query", "default", "image/png");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(string.Empty, run.Stdout);
    }

    [Fact]
    public async Task Default_writes_the_users_choice_for_each_type_and_ignores_a_desktop_suffix()
    {
        FakeAppCatalogGateway catalog = Catalog();

        Run run = await RunAsync(catalog, "default", Editor + ".desktop", "text/plain", ".TXT");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            [new KazoFileAssociation("text/plain", "open", Editor, KazoFileAssociationLayer.User),
             new KazoFileAssociation(".txt", "open", Editor, KazoFileAssociationLayer.User)],
            catalog.UserDefaults);
    }

    [Theory]
    [InlineData("org.example.missing", "text/plain", 4, "no installed application")]
    [InlineData(Notepad, "image/png", 4, "does not declare")]
    [InlineData(Notepad, "plain", 1, "neither a media type")]
    public async Task Default_refuses_what_the_os_could_not_use(string app, string type, int exitCode, string message)
    {
        FakeAppCatalogGateway catalog = Catalog();

        Run run = await RunAsync(catalog, "default", app, type);

        Assert.Equal(exitCode, run.ExitCode);
        Assert.Contains(message, run.Stderr, StringComparison.Ordinal);
        Assert.Empty(catalog.UserDefaults);
    }

    [Fact]
    public async Task An_invalid_user_file_is_reported_and_kept()
    {
        FakeAppCatalogGateway catalog = Catalog();
        catalog.NextSetResult = KazoDefaultHandlerStatus.FileInvalid;

        Run run = await RunAsync(catalog, "default", Notepad, "text/plain");

        Assert.Equal(4, run.ExitCode);
        Assert.Contains("is not valid", run.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("Documents", "inode/directory")]
    [InlineData("archive.qqq", ".qqq")]
    [InlineData("README", "application/octet-stream")]
    public async Task Query_filetype_prints_the_type_the_os_matches(string file, string expected)
    {
        Run run = await RunAsync(Catalog(), "query", "filetype", file);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(expected, run.Stdout.Trim());
    }

    [Fact]
    public async Task Query_filetype_of_a_missing_file_exits_2()
    {
        Run run = await RunAsync(Catalog(), "query", "filetype", "missing.txt");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("does not exist", run.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("query")]
    [InlineData("query", "default")]
    [InlineData("default", Notepad)]
    [InlineData("install", "x.xml")]
    public async Task A_bad_command_line_exits_1(params string[] arguments)
    {
        Run run = await RunAsync(Catalog(), arguments);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("usage: xdg-mime", run.Stderr, StringComparison.Ordinal);
    }

    private static FakeAppCatalogGateway Catalog() => new FakeAppCatalogGateway()
        .WithApp(FakeAppCatalogGateway.Window(Notepad, "Notepad", new KazoAppFileHandler("text/plain", [".txt"], ["open", "edit"])))
        .WithApp(FakeAppCatalogGateway.Window(Editor, "Text Editor", new KazoAppFileHandler("text/plain", [".txt", ".log"], ["open", "edit"])));

    private static async Task<Run> RunAsync(FakeAppCatalogGateway catalog, params string[] arguments)
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new XdgMimeApp());
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithFile("/home/alice/notes.txt", "hello")
            .WithFile("/home/alice/archive.qqq", "x")
            .WithFile("/home/alice/README", "x")
            .WithDirectory("/home/alice/Documents");
        MinimalAppExecutionContext app = new(
            manifest, fileSystem: fileSystem, homeDirectory: "/home/alice",
            environment: new FakeAppEnvironmentGateway("/home/alice"), catalog: catalog);
        StringWriter stdout = new();
        StringWriter stderr = new();
        TerminalExecutionContext context = new(
            app, arguments, new StringReader(string.Empty), stdout, stderr,
            "/home/alice", new Dictionary<string, string> { ["HOME"] = "/home/alice" });

        using IDisposable scope = KazoExecutionContext.Enter(app);
        int exitCode = await new XdgMimeCommand(manifest).ExecuteAsync(context, CancellationToken.None);
        return new Run(exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr);
}
