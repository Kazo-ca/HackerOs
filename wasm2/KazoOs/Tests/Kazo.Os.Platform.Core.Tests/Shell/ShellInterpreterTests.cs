using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Intents;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Platform.Core.Shell;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Shell;

/// <summary>
/// The headless shell end to end (ADR 0050, sections 1 to 4 and 7): real orchestrator, process
/// manager and filesystem, a Terminal window as the shell's process, test commands.
/// </summary>
public sealed partial class ShellInterpreterTests
{
    [Fact]
    public async Task Quoted_arguments_reach_the_command_intact()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output output = await fixture.RunAsync("args \"a b\" 'c  d' e\\ f");

        Assert.Equal(0, output.ExitCode);
        Assert.Equal("a b|c  d|e f\n", output.Stdout);
    }

    [Fact]
    public async Task A_pipeline_feeds_each_stage_into_the_next()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output output = await fixture.RunAsync("echo hello world|upper | count");

        Assert.Equal(0, output.ExitCode);
        Assert.Equal("1\n", output.Stdout);
        Assert.Equal("HELLO\n", (await fixture.RunAsync("echo hello | upper")).Stdout);
    }

    [Fact]
    public async Task Stages_run_at_the_same_time_and_a_finished_reader_breaks_the_pipe()
    {
        Fixture fixture = await Fixture.CreateAsync();

        // `yes` writes forever: this only ends because both stages run concurrently and `head1`
        // closing its input makes the next write of `yes` fail.
        Output output = await fixture.RunAsync("yes | head1").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, output.ExitCode);
        Assert.Equal("y\n", output.Stdout);
        ProcessRecord yes = fixture.Manager.GetHistory().Last(p => p.AppId == "org.test.yes");
        Assert.Equal(141, yes.ExitCode);
    }

    [Fact]
    public async Task And_runs_the_next_pipeline_only_after_success_and_dollar_question_follows()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output failed = await fixture.RunAsync("exitwith 3 && echo never");
        Output status = await fixture.RunAsync("echo $? ${?}");
        Output chained = await fixture.RunAsync("exitwith 0 && echo $? && echo done");

        Assert.Equal(3, failed.ExitCode);
        Assert.Equal(string.Empty, failed.Stdout);
        Assert.Equal("3 3\n", status.Stdout);
        Assert.Equal("0\ndone\n", chained.Stdout);
    }

    [Fact]
    public async Task Variables_and_tilde_expand_and_unquoted_results_split_on_ifs()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Session.SetVariable("GREETING", "hello   there");

        // ADR 0064: POSIX field splitting, so an unquoted empty variable disappears and quotes keep one field.
        Output output = await fixture.RunAsync("args $HOME ~ ~/docs ${USER}x $GREETING \"$GREETING\" '$HOME' \"~\" $MISSING \"$MISSING\"");

        Assert.Equal("/home/alice|/home/alice|/home/alice/docs|alicex|hello|there|hello   there|$HOME|~|\n", output.Stdout);
    }

    [Fact]
    public async Task Unknown_commands_exit_127_and_syntax_errors_exit_2_without_running_anything()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output missing = await fixture.RunAsync("nope arg");
        int launchedBefore = fixture.Manager.GetHistory().Count;
        Output syntax = await fixture.RunAsync("echo a |");

        Assert.Equal(127, missing.ExitCode);
        Assert.Equal("hsh: nope: command not found\n", missing.Stderr);
        Assert.Equal(ShellFailure.CommandNotFound, missing.Result.Failure);
        Assert.Equal(2, syntax.ExitCode);
        Assert.StartsWith("hsh: syntax error", syntax.Stderr);
        Assert.Equal(launchedBefore, fixture.Manager.GetHistory().Count);
    }

    [Fact]
    public async Task Redirections_create_truncate_and_append_relative_to_the_shell_directory()
    {
        Fixture fixture = await Fixture.CreateAsync();

        await fixture.RunAsync("echo first > out.txt");
        await fixture.RunAsync("echo second >> out.txt");
        Output toFile = await fixture.RunAsync("echo third >> ~/out.txt | upper");
        await fixture.RunAsync("echo x > a.txt > b.txt");
        await fixture.RunAsync("> empty.txt");

        Assert.Equal(string.Empty, toFile.Stdout); // the redirected stage feeds `upper` nothing
        Assert.Equal("first\nsecond\nthird\n", await fixture.ReadFileAsync("/home/alice/out.txt"));
        Assert.Equal(string.Empty, await fixture.ReadFileAsync("/home/alice/a.txt"));
        Assert.Equal("x\n", await fixture.ReadFileAsync("/home/alice/b.txt"));
        Assert.Equal(string.Empty, await fixture.ReadFileAsync("/home/alice/empty.txt"));
        Assert.Equal("first\n", (await fixture.RunAsync("echo first > out.txt && cat1 out.txt")).Stdout);
    }

    [Fact]
    public async Task A_redirection_that_cannot_open_stops_the_stage()
    {
        Fixture fixture = await Fixture.CreateAsync();
        int launchedBefore = fixture.Manager.GetHistory().Count;

        Output output = await fixture.RunAsync("echo x > /missing/dir/f.txt");

        Assert.Equal(1, output.ExitCode);
        Assert.Equal("hsh: /missing/dir/f.txt: No such file or directory\n", output.Stderr);
        Assert.Equal(ShellFailure.Redirection, output.Result.Failure);
        Assert.Equal(launchedBefore, fixture.Manager.GetHistory().Count);
    }

    [Fact]
    public async Task Commands_are_children_of_the_shell_and_start_in_its_directory()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.MakeDirectoryAsync("/home/alice/projects");

        await fixture.Session.ChangeDirectoryAsync("projects");
        Output output = await fixture.RunAsync("where");

        Assert.Equal("/home/alice/projects /home/alice/projects /home/alice\n", output.Stdout);
        ProcessRecord child = fixture.Manager.GetHistory().Last(p => p.AppId == "org.test.where");
        Assert.Equal(fixture.Host.ProcessId, child.ParentPid);
        Assert.Equal("alice@hackeros:~/projects$", fixture.Session.GetPrompt());
    }

    [Fact]
    public async Task Changing_to_a_missing_directory_fails_and_keeps_the_directory()
    {
        Fixture fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<HackDirectoryNotFoundException>(() => fixture.Session.ChangeDirectoryAsync("/nope"));

        Assert.Equal("/home/alice", fixture.Session.CurrentDirectory);
    }

    [Fact]
    public async Task Interrupting_cancels_every_stage_and_stops_the_list()
    {
        Fixture fixture = await Fixture.CreateAsync();
        using CancellationTokenSource cancellation = new();

        Task<Output> running = fixture.RunAsync("hang | upper && echo never", cancellation.Token);
        await Task.Delay(50);
        cancellation.Cancel();
        Output output = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(130, output.ExitCode);
        Assert.Equal(string.Empty, output.Stdout);
        Assert.Equal(130, fixture.Session.LastExitCode);
    }

    [Fact]
    public async Task Cd_changes_the_shell_directory_for_the_rest_of_the_list()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.MakeDirectoryAsync("/home/alice/work");

        Output moved = await fixture.RunAsync("cd work && where");
        Output back = await fixture.RunAsync("cd -");
        string? oldPwd = fixture.Session.GetVariable("OLDPWD");
        Output home = await fixture.RunAsync("cd /tmp && cd && pwd");

        Assert.Equal(0, moved.ExitCode);
        Assert.Equal("/home/alice/work /home/alice/work /home/alice\n", moved.Stdout);
        Assert.Equal("/home/alice\n", back.Stdout);
        Assert.Equal("/home/alice/work", oldPwd);
        Assert.Equal("/home/alice\n", home.Stdout);
        Assert.Equal("/home/alice", fixture.Session.CurrentDirectory);
        Assert.Equal("/home/alice", fixture.Host.Environment.CurrentDirectory);
    }

    [Fact]
    public async Task Cd_reports_errors_like_linux()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.WriteFileAsync("/home/alice/notes.txt", "x");

        Output missing = await fixture.RunAsync("cd nope");
        Output file = await fixture.RunAsync("cd notes.txt");

        Assert.Equal(1, missing.ExitCode);
        Assert.Equal("cd: nope: No such file or directory\n", missing.Stderr);
        Assert.Equal("cd: notes.txt: Not a directory\n", file.Stderr);
        Assert.Equal("/home/alice", fixture.Session.CurrentDirectory);
    }

    [Fact]
    public async Task A_shell_command_inside_a_pipe_changes_only_a_copy()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output output = await fixture.RunAsync("cd /tmp | count && export LOST=1 | count");

        Assert.Equal(0, output.ExitCode);
        Assert.Equal("0\n0\n", output.Stdout);
        Assert.Equal("/home/alice", fixture.Session.CurrentDirectory);
        Assert.Null(fixture.Session.GetVariable("LOST"));
    }

    [Fact]
    public async Task Export_and_unset_change_what_later_commands_inherit()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output exported = await fixture.RunAsync("export GREETING=\"hi there\" && env");
        Output listed = await fixture.RunAsync("export");
        Output invalid = await fixture.RunAsync("export 1BAD=x");
        Output unset = await fixture.RunAsync("unset GREETING && env");

        Assert.Contains("GREETING=hi there\n", exported.Stdout);
        Assert.Contains("declare -x GREETING=\"hi there\"\n", listed.Stdout);
        Assert.Equal(1, invalid.ExitCode);
        Assert.Equal("export: `1BAD=x': not a valid identifier\n", invalid.Stderr);
        Assert.Equal(0, unset.ExitCode);
        Assert.DoesNotContain("GREETING=", unset.Stdout);
        Assert.Contains("HOME=/home/alice\n", unset.Stdout);
    }

    [Fact]
    public async Task Help_lists_the_installed_commands_from_the_catalog()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Enablement.MarkDisabled(["org.test.yes"]);

        Output all = await fixture.RunAsync("help");
        Output one = await fixture.RunAsync("help cd");
        Output unknown = await fixture.RunAsync("help yes");

        Assert.Contains("  cd ", all.Stdout);
        Assert.Contains("  upper ", all.Stdout);
        Assert.Contains("  sudo ", all.Stdout);
        Assert.DoesNotContain("  yes ", all.Stdout);
        Assert.Equal("cd - Change the working directory\nusage: cd [directory|-]\n", one.Stdout);
        Assert.Equal(1, unknown.ExitCode);
    }

    [Fact]
    public async Task Clear_writes_the_erase_sequence_for_the_terminal_to_interpret()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output screen = await fixture.RunAsync("clear");
        await fixture.RunAsync("clear > screen.txt");

        Assert.Equal("\x1b[H\x1b[2J\x1b[3J", screen.Stdout);
        Assert.Equal("\x1b[H\x1b[2J\x1b[3J", await fixture.ReadFileAsync("/home/alice/screen.txt"));
    }

    [Fact]
    public async Task Sudo_runs_one_command_as_root_as_a_child_of_sudo()
    {
        Fixture fixture = await Fixture.CreateAsync();
        List<string> prompts = [];

        Output output = await fixture.RunAsync("sudo amroot | upper && amroot", credentialPrompt: (description, _) =>
        {
            prompts.Add(description);
            ElevationTicket ticket = fixture.Tickets.Issue(ElevationScope.SudoRoot, "admin-id", "admin");
            return Task.FromResult<ShellElevation?>(new ShellElevation(ticket.Token, ticket.Scope));
        });

        Assert.Equal(0, output.ExitCode);
        Assert.Equal("ROOT\nuser\n", output.Stdout);
        Assert.Equal(["Run as root: amroot"], prompts);
        ProcessRecord sudo = fixture.Manager.GetHistory().Single(p => p.AppId == "org.hackeros.commands.sudo");
        ProcessRecord elevated = fixture.Manager.GetHistory().First(p => p.AppId == "org.test.amroot");
        Assert.Equal(sudo.Pid, elevated.ParentPid);
        Assert.Equal(fixture.Host.ProcessId, sudo.ParentPid);
    }

    [Fact]
    public async Task Sudo_nice_runs_the_command_as_root_with_a_lower_niceness()
    {
        Fixture fixture = await Fixture.CreateAsync();
        List<string> prompts = [];

        Output unelevated = await fixture.RunAsync("nice -n -5 rootnice");
        Output elevated = await fixture.RunAsync("sudo nice -n -5 rootnice", credentialPrompt: (description, _) =>
        {
            prompts.Add(description);
            ElevationTicket ticket = fixture.Tickets.Issue(ElevationScope.SudoRoot, "admin-id", "admin");
            return Task.FromResult<ShellElevation?>(new ShellElevation(ticket.Token, ticket.Scope));
        });

        // Without sudo, GNU nice warns and runs the command at the caller's niceness.
        Assert.Equal("user 0\n", unelevated.Stdout);
        Assert.Contains("nice:", unelevated.Stderr, StringComparison.Ordinal);
        // ADR 0136: one credential prompt; nice execs the command, which runs as root at -5.
        Assert.Equal(0, elevated.ExitCode);
        Assert.Equal("root -5\n", elevated.Stdout);
        Assert.Equal(string.Empty, elevated.Stderr);
        Assert.Equal(["Run as root: nice -n -5 rootnice"], prompts);
        ProcessRecord nice = fixture.Manager.GetHistory().Last(p => p.AppId == "org.hackeros.commands.nice");
        ProcessRecord command = fixture.Manager.GetHistory().Last(p => p.AppId == "org.test.rootnice");
        Assert.Equal(nice.Pid, command.ParentPid);
    }

    [Fact]
    public async Task Sudo_refuses_shell_builtins_and_failed_credentials()
    {
        Fixture fixture = await Fixture.CreateAsync();
        int prompts = 0;

        Output builtin = await fixture.RunAsync("sudo cd /root", credentialPrompt: (_, _) => { prompts++; return Task.FromResult<ShellElevation?>(null); });
        Output refused = await fixture.RunAsync("sudo amroot", credentialPrompt: (_, _) => { prompts++; return Task.FromResult<ShellElevation?>(null); });
        Output missing = await fixture.RunAsync("sudo nothing");

        Assert.Equal(1, builtin.ExitCode);
        Assert.Equal("sudo: cd: shell builtin\n", builtin.Stderr);
        Assert.Equal(1, refused.ExitCode);
        Assert.Equal("sudo: authentication failed\n", refused.Stderr);
        Assert.Equal(1, prompts);
        Assert.Equal("sudo: nothing: command not found\n", missing.Stderr);
    }

    [Fact]
    public async Task Only_a_command_declaring_shell_control_run_interactively_reaches_the_shell()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Grant(AppCapabilities.AppsLaunch);

        Output plain = await fixture.RunAsync("hasshell");
        AppIntentDispatchResult intent = await fixture.Dispatcher.DispatchAsync(
            new AppIntentRequest(Guid.NewGuid(), Fixture.HostAppId, fixture.Principal.UserId.ToString(),
                new ExecuteCommandIntent("cd /tmp")),
            fixture.Principal,
            callerContext: fixture.Host);

        Assert.Equal("False\n", plain.Stdout);
        Assert.Equal(1, intent.ExitCode);
        Assert.Equal("cd: no shell session\n", intent.StandardError);
        Assert.Equal("/home/alice", fixture.Session.CurrentDirectory);
    }

    [Fact]
    public async Task Unalias_is_a_static_alias_of_alias_read_from_argv0()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.RunAsync("alias hi=\"echo hello\"");

        Output aliased = await fixture.RunAsync("hi");
        Output removed = await fixture.RunAsync("unalias hi");
        Output gone = await fixture.RunAsync("hi");

        Assert.Equal("hello\n", aliased.Stdout);
        Assert.Equal(0, removed.ExitCode);
        Assert.Equal(127, gone.ExitCode);
    }

    [Fact]
    public async Task Aliases_from_the_alias_file_replace_the_first_word()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.MakeDirectoryAsync("/home/alice/.hackeros");
        await fixture.WriteFileAsync("/home/alice/.hackeros/aliases", "hi=echo hello\nloud=echo x | upper\n");

        Output aliased = await fixture.RunAsync("hi world");
        Output quoted = await fixture.RunAsync("'hi' world");
        Output complex = await fixture.RunAsync("loud");

        Assert.Equal("hello world\n", aliased.Stdout);
        Assert.Equal(127, quoted.ExitCode);
        Assert.Contains("only a simple command", complex.Stderr);
    }

    [Fact]
    public async Task Execute_command_intent_runs_through_the_shell_in_the_callers_process()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Grant(AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.Dispatcher.DispatchAsync(
            new AppIntentRequest(Guid.NewGuid(), Fixture.HostAppId, fixture.Principal.UserId.ToString(),
                new ExecuteCommandIntent("echo \"a  b\" | upper > shout.txt && echo ok")),
            fixture.Principal,
            callerContext: fixture.Host);

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok\n", result.StandardOutput);
        Assert.Equal("A  B\n", await fixture.ReadFileAsync("/home/alice/shout.txt"));
    }

    [Fact]
    public async Task Execute_command_intent_without_a_caller_context_runs_detached()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Grant(AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.Dispatcher.DispatchAsync(
            new AppIntentRequest(Guid.NewGuid(), Fixture.HostAppId, fixture.Principal.UserId.ToString(),
                new ExecuteCommandIntent("where && echo x > f.txt", VirtualPath.Parse("/tmp"))),
            fixture.Principal);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("/tmp /tmp /home/alice\n", result.StandardOutput);
        Assert.Contains("cannot open files", result.StandardError);
        ProcessRecord child = fixture.Manager.GetHistory().Last(p => p.AppId == "org.test.where");
        Assert.Null(child.ParentPid);
    }

    private sealed record Output(ShellExecutionResult Result, string Stdout, string Stderr)
    {
        public int ExitCode => Result.ExitCode;
    }

    // ---- Test commands -------------------------------------------------------------------

    private sealed class ArgsCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            await context.StandardOutput.WriteLineAsync(string.Join('|', context.Arguments));
            return 0;
        }
    }

    private sealed class EchoCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            await context.StandardOutput.WriteLineAsync(string.Join(' ', context.Arguments));
            return 0;
        }
    }

    private sealed class UpperCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            string input = await context.StandardInput.ReadToEndAsync(cancellationToken);
            await context.StandardOutput.WriteAsync(input.ToUpperInvariant());
            return 0;
        }
    }

    private sealed class CountCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            int lines = 0;
            while (await context.StandardInput.ReadLineAsync(cancellationToken) is not null)
            {
                lines++;
            }

            await context.StandardOutput.WriteLineAsync(lines.ToString());
            return 0;
        }
    }

    private sealed class ExitWithCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(int.Parse(context.Arguments[0]));
    }

    private sealed class YesCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            while (true)
            {
                await context.StandardOutput.WriteLineAsync("y");
            }
        }
    }

    private sealed class HeadOneCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            await context.StandardOutput.WriteLineAsync(await context.StandardInput.ReadLineAsync(cancellationToken));
            return 0;
        }
    }

    private sealed class HangCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    /// <summary>Prints the process's live directory, its PWD and its HOME.</summary>
    private sealed class WhereCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            await context.StandardOutput.WriteLineAsync(
                $"{HackEnvironment.CurrentDirectory} {HackEnvironment.GetEnvironmentVariable("PWD")} {HackEnvironment.GetEnvironmentVariable("HOME")}");
            return 0;
        }
    }

    /// <summary>Prints ROOT when its process runs elevated, user otherwise.</summary>
    private sealed class AmRootCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            await context.StandardOutput.WriteLineAsync(context.App.IsRootElevated ? "root" : "user");
            return 0;
        }
    }

    /// <summary>Prints whether it runs as root and its niceness (ADR 0136).</summary>
    private sealed class RootNiceCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            int nice = context.App.Processes.GetNice(context.App.ProcessId);
            await context.StandardOutput.WriteLineAsync($"{(context.App.IsRootElevated ? "root" : "user")} {nice}");
            return 0;
        }
    }

    /// <summary>Prints whether it received the shell; it does not declare shell.control.</summary>
    private sealed class HasShellCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            await context.StandardOutput.WriteLineAsync((context.Shell is not null).ToString());
            return 0;
        }
    }

    /// <summary>Prints one variable of its own process environment (what it inherited).</summary>
    private sealed class PrintEnvCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            await context.StandardOutput.WriteLineAsync(HackEnvironment.GetEnvironmentVariable(context.Arguments[0]) ?? "<unset>");
            return 0;
        }
    }

    private sealed class CatOneCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            await context.StandardOutput.WriteAsync(await HackFile.ReadAllTextAsync(context.Arguments[0], cancellationToken));
            return 0;
        }
    }

    // ---- Fixture -------------------------------------------------------------------------

    private sealed class Fixture
    {
        public const string HostAppId = "org.hackeros.terminal";

        private static readonly string[] FileCapabilities =
        [
            AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite,
            AppCapabilities.FileSystemSystemRead, AppCapabilities.FileSystemSystemWrite
        ];

        private int _entryId = 1;
        private int _transactionId = 100;
        private readonly DateTimeOffset _now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        private readonly CapabilityGrantRepository _grants;
        private readonly LocalSessionService _sessions;
        private readonly AppLifecycleOrchestrator _orchestrator;
        private readonly LocalLoginName _alice = LocalLoginName.Parse("alice");

        private Fixture()
        {
            FixedTimeProvider timeProvider = new(_now);
            InMemoryFileSystemRepository repository = new(
                () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                timeProvider);
            FileSystemMountRouter router = new(repository);
            _grants = new CapabilityGrantRepository(() => _now);
            InMemoryTopicMessageBus topicBus = new(_grants);
            FileSystemService fileSystem = new(
                router, new FileSystemPathResolver(router), new FileSystemAuthorizer(), topicBus,
                () => new Guid(_transactionId++, 0, 0, new byte[8]));
            FileSystemSeeder seeder = new(fileSystem, timeProvider);

            InMemoryLocalUserRepository users = new(() => _now);
            InMemoryLocalGroupRepository groups = new();
            InMemoryEventBus eventBus = new();
            LocalGroup group = groups.CreateGroup(LocalLoginName.Parse("users"));
            users.CreateUser(_alice, "Alice", AppAuthority.User, group.Id, credential: LocalPasswordHasher.Create("hunter2", iterations: 100));
            _sessions = new LocalSessionService(
                users, seeder, eventBus, new BoundedAuditLog(maxEntries: 100),
                InstallationId.FromGuid(Guid.NewGuid()), DeviceId.FromGuid(Guid.NewGuid()), () => _now);

            ManualSimulationClock clock = new(_now, TimeSpan.FromSeconds(1));
            Manager = new InMemoryProcessManager(clock, _sessions, eventBus);
            AppExecutionContextFactory contextFactory = new(
                _grants, fileSystem, eventBus, topicBus, new InMemoryNotificationQueue(maxEntriesPerUser: 20),
                new BoundedDiagnosticSink(maxEntries: 100), clock, Manager,
                shellHostProvider: () => ShellHost!);

            AppManifest[] commands =
            [
                Command<ArgsCommand>("args"), Command<EchoCommand>("echo"), Command<UpperCommand>("upper"),
                Command<CountCommand>("count"), Command<ExitWithCommand>("exitwith"), Command<YesCommand>("yes"),
                Command<HeadOneCommand>("head1"), Command<HangCommand>("hang"), Command<WhereCommand>("where"),
                Command<CatOneCommand>("cat1"), Command<AmRootCommand>("amroot"), Command<RootNiceCommand>("rootnice"), Command<HasShellCommand>("hasshell"),
                Command<PrintEnvCommand>("printenv1"),
                .. CatalogCommands
            ];
            AppManifest host = HostManifest();
            AppCatalog catalog = AppCatalog.Build([host, .. commands]).Catalog!;
            AppDiscoveryResult discovery = AppEntryPointDiscovery.Discover(
                AppCatalog.Build(commands).Catalog!,
                new Dictionary<string, System.Reflection.Assembly>(StringComparer.Ordinal)
                {
                    ["Kazo.Os.Platform.Core.Tests"] = typeof(Fixture).Assembly,
                    ["Kazo.Os.Commands.dll"] = typeof(Kazo.Os.Commands.Cd.CdCommand).Assembly
                });
            Assert.True(discovery.IsSuccess, string.Join("; ", discovery.Errors.Select(e => e.ToString())));
            Dictionary<string, AppDescriptor> descriptors = new(discovery.Descriptors!, StringComparer.Ordinal)
            {
                [HostAppId] = new AppDescriptor(host, typeof(object), typeof(object).Assembly)
            };

            AppEnablementRegistry enablement = new(catalog);
            Enablement = enablement;
            _orchestrator = new AppLifecycleOrchestrator(
                catalog, descriptors, enablement, Manager, _grants, contextFactory, elevationTickets: Tickets);
            Interpreter = new ShellInterpreter(_orchestrator, catalog, enablement);
            ShellHost = new ShellHostService(Interpreter, Manager, new FakeElevationService(Tickets));
            Dispatcher = new AppIntentDispatcher(
                _orchestrator, catalog, enablement,
                new FileAssociationResolver(catalog, enablement, new OsSettingsService(fileSystem, _sessions, clock), fileSystem),
                _grants, fileSystem);
        }

        public InMemoryProcessManager Manager { get; }

        public AppLifecycleOrchestrator Orchestrator => _orchestrator;
        public AppEnablementRegistry Enablement { get; }
        public InMemoryElevationTicketStore Tickets { get; } = new();
        public ShellInterpreter Interpreter { get; }
        public ShellHostService ShellHost { get; }
        public AppIntentDispatcher Dispatcher { get; }
        public AuthenticatedPrincipal Principal { get; private set; } = default!;
        public IAppExecutionContext Host { get; private set; } = default!;
        public ShellSession Session { get; private set; } = default!;

        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            fixture.Principal = await fixture._sessions.LoginAsync(fixture._alice, "hunter2");
            foreach (string appId in new[] { HostAppId, "org.test.cat1", "org.test.where" })
            {
                foreach (string capability in FileCapabilities)
                {
                    fixture.Grant(capability, appId);
                }
            }

            // The catalog's own commands get what they declare, as the system-app seeder grants it.
            foreach (AppManifest command in CatalogCommands)
            {
                foreach (string capability in command.Capabilities)
                {
                    fixture.Grant(capability, command.Id);
                }
            }

            AppLaunchResult launched = await fixture._orchestrator.LaunchAsync(new AppLaunchRequest(HostAppId, fixture.Principal, []));
            Assert.True(launched.IsSuccess, launched.ErrorMessage);
            fixture.Host = launched.Context!;
            fixture.Session = new ShellSession(fixture.Host, fixture.Principal);
            return fixture;
        }

        public void Grant(string capability, string appId = HostAppId) =>
            _grants.Grant(appId, Principal.UserId.ToString(), capability, CapabilityGrantSource.UserApproval, AppAuthority.Administrator);

        public async Task<Output> RunAsync(
            string line,
            CancellationToken cancellationToken = default,
            Func<string, CancellationToken, Task<ShellElevation?>>? credentialPrompt = null)
        {
            StringWriter stdout = new() { NewLine = "\n" };
            StringWriter stderr = new() { NewLine = "\n" };
            ShellExecutionResult result = await Interpreter.ExecuteAsync(
                Session,
                line,
                new ShellExecutionOptions { StandardOutput = stdout, StandardError = stderr, CredentialPrompt = credentialPrompt },
                cancellationToken);
            return new Output(result, stdout.ToString(), stderr.ToString());
        }

        public async Task<string> ReadFileAsync(string path)
        {
            using IDisposable scope = HackExecutionContext.Enter(Host);
            return await HackFile.ReadAllTextAsync(path);
        }

        public async Task WriteFileAsync(string path, string content)
        {
            using IDisposable scope = HackExecutionContext.Enter(Host);
            await HackFile.WriteAllTextAsync(path, content);
        }

        public async Task SetModeAsync(string path, UnixFileMode mode)
        {
            using IDisposable scope = HackExecutionContext.Enter(Host);
            await HackFile.SetUnixFileModeAsync(path, mode);
        }

        public async Task MakeDirectoryAsync(string path)
        {
            using IDisposable scope = HackExecutionContext.Enter(Host);
            await HackDirectory.CreateDirectoryAsync(path);
        }

        /// <summary>The real catalog commands the shell relies on (ADR 0051).</summary>
        private static readonly AppManifest[] CatalogCommands =
        [
            .. new AppDefinition[]
            {
                new Kazo.Os.Commands.Cd.CdApp(), new Kazo.Os.Commands.Pwd.PwdApp(), new Kazo.Os.Commands.Export.ExportApp(),
                new Kazo.Os.Commands.Unset.UnsetApp(), new Kazo.Os.Commands.Env.EnvApp(), new Kazo.Os.Commands.Help.HelpApp(),
                new Kazo.Os.Commands.Sudo.SudoApp(), new Kazo.Os.Commands.Clear.ClearApp(), new Kazo.Os.Commands.Alias.AliasApp(),
                new Kazo.Os.Commands.Exit.ExitApp(),
                // ADR 0064: the commands added with the POSIX shell.
                new Kazo.Os.Commands.Test.TestApp(), new Kazo.Os.Commands.True.TrueApp(), new Kazo.Os.Commands.False.FalseApp(),
                new Kazo.Os.Commands.Read.ReadApp(), new Kazo.Os.Commands.Printf.PrintfApp(), new Kazo.Os.Commands.Seq.SeqApp(),
                new Kazo.Os.Commands.Cut.CutApp(), new Kazo.Os.Commands.Tee.TeeApp(), new Kazo.Os.Commands.Uniq.UniqApp(),
                new Kazo.Os.Commands.Sort.SortApp(), new Kazo.Os.Commands.Head.HeadApp(), new Kazo.Os.Commands.Tail.TailApp(),
                new Kazo.Os.Commands.Ln.LnApp(), new Kazo.Os.Commands.Chmod.ChmodApp(), new Kazo.Os.Commands.Whoami.WhoamiApp(),
                new Kazo.Os.Commands.Basename.BasenameApp(), new Kazo.Os.Commands.Dirname.DirnameApp(), new Kazo.Os.Commands.Zip.ZipApp(),
                new Kazo.Os.Commands.Unzip.UnzipApp(), new Kazo.Os.Commands.Du.DuApp(), new Kazo.Os.Commands.Locate.LocateApp(),
                new Kazo.Os.Commands.Jobs.JobsApp(), new Kazo.Os.Commands.Kill.KillApp(), new Kazo.Os.Commands.Which.WhichApp(),
                new Kazo.Os.Commands.Sleep.SleepApp(), new Kazo.Os.Commands.Ip.IpApp(), new Kazo.Os.Commands.Top.TopApp(),
                // ADR 0069: grep -r checkpoints between files.
                new Kazo.Os.Commands.Grep.GrepApp(),
                // ADR 0127 and 0136: nice, alone and under sudo.
                new Kazo.Os.Commands.Nice.NiceApp()
            }.Select(AppDefinitionMapper.ToManifest)
        ];

        private static AppManifest HostManifest() => new()
        {
            Id = HostAppId,
            Name = "Terminal",
            Version = "1.0.0",
            PublisherId = "org.hackeros",
            Description = "Hosts the shell.",
            Kind = AppKind.Window,
            EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "org.hackeros.terminal.EntryPoint"),
            SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None,
            Capabilities = [.. FileCapabilities, AppCapabilities.AppsLaunch]
        };

        private static AppManifest Command<TCommand>(string name) => new()
        {
            Id = $"org.test.{name}",
            Name = name,
            Version = "1.0.0",
            PublisherId = "org.test",
            Description = $"Test command {name}.",
            Kind = AppKind.Terminal,
            EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", typeof(TCommand).FullName!),
            SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Hidden, []),
            Resources = AppResourceProfileManifest.None,
            Capabilities = FileCapabilities,
            Terminal = new TerminalCommandManifest(name, [], $"{name} [args]")
        };

        private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
