using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Shell;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Tests.Shell;

/// <summary>
/// The POSIX sh subset of ADR 0064 end to end: lists, assignments, substitutions, arithmetic,
/// compound commands, functions, redirections, globbing, scripts, jobs and apps by short name.
/// </summary>
public sealed partial class ShellInterpreterTests
{
    [Fact]
    public async Task Semicolons_and_or_lists_run_in_order()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output output = await fixture.RunAsync("exitwith 1 || echo fallback; echo next && exitwith 2 || echo recovered");

        Assert.Equal("fallback\nnext\nrecovered\n", output.Stdout);
        Assert.Equal(0, output.ExitCode);
        Assert.Equal(1, (await fixture.RunAsync("! exitwith 0")).ExitCode);
    }

    [Fact]
    public async Task Assignments_make_unexported_variables_until_export()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output local = await fixture.RunAsync("X=5; echo $X; printenv1 X");
        Output exported = await fixture.RunAsync("export X; printenv1 X");
        Output prefixed = await fixture.RunAsync("ONLY=here printenv1 ONLY; echo \"[$ONLY]\"");

        Assert.Equal("5\n<unset>\n", local.Stdout);
        Assert.Equal("5\n", exported.Stdout);
        Assert.Equal("here\n[]\n", prefixed.Stdout);
    }

    [Fact]
    public async Task Command_substitution_and_arithmetic_expand_inside_words()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output output = await fixture.RunAsync("i=2; echo \"got: $(echo hi | upper) `echo there`\" $((i * 3 + 1)) $((i++)) $i ${i:-x} ${unset:-default} ${#HOME}");

        Assert.Equal("got: HI there 7 2 3 3 default 11\n", output.Stdout);
    }

    [Fact]
    public async Task Parameter_operators_trim_patterns()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output output = await fixture.RunAsync("f=/home/alice/archive.tar.gz; echo ${f##*/} ${f%.*} ${f%%.*} ${f#*/}");

        Assert.Equal("archive.tar.gz /home/alice/archive.tar /home/alice/archive home/alice/archive.tar.gz\n", output.Stdout);
    }

    [Fact]
    public async Task If_for_while_and_case_control_the_flow()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output loops = await fixture.RunAsync(
            "for n in 1 2 3; do if exitwith $((n % 2)); then echo even $n; else echo odd $n; fi; done");
        Output counter = await fixture.RunAsync("i=0; while exitwith $((i >= 3)); do i=$((i + 1)); done; echo $i");
        Output until = await fixture.RunAsync("i=0; until exitwith $((i < 2)); do i=$((i + 1)); done; echo $i");
        Output matched = await fixture.RunAsync("for f in notes.md todo.txt x; do case $f in *.md) echo md;; *.txt|*.log) echo text;; *) echo other;; esac; done");

        Assert.Equal("odd 1\neven 2\nodd 3\n", loops.Stdout);
        Assert.Equal("3\n", counter.Stdout);
        Assert.Equal("2\n", until.Stdout);
        Assert.Equal("md\ntext\nother\n", matched.Stdout);
    }

    [Fact]
    public async Task Break_and_continue_leave_or_skip_iterations()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output output = await fixture.RunAsync(
            "for i in 1 2 3 4; do case $i in 2) continue;; 4) break;; esac; echo $i; done; " +
            "for a in x y; do for b in 1 2; do echo $a$b; break 2; done; done");

        Assert.Equal("1\n3\nx1\n", output.Stdout);
    }

    [Fact]
    public async Task Functions_take_arguments_locals_and_return_codes()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output output = await fixture.RunAsync("f() { local v=in; echo \"$1-$v-$#\"; return 3; }; v=out; f arg two; echo $? $v");

        Assert.Equal("arg-in-2\n3 out\n", output.Stdout);
    }

    [Fact]
    public async Task Descriptor_redirections_here_documents_and_dev_null()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.WriteFileAsync("/home/alice/in.txt", "from file\n");

        Output toError = await fixture.RunAsync("echo oops 1>&2");
        await fixture.RunAsync("{ echo a; echo b 1>&2; } > both.txt 2>&1");
        Output input = await fixture.RunAsync("upper < in.txt");
        Output here = await fixture.RunAsync("upper <<EOF\nhi $USER\nEOF");
        Output quiet = await fixture.RunAsync("echo hidden > /dev/null; nope 2>/dev/null; echo $?");

        Assert.Equal(string.Empty, toError.Stdout);
        Assert.Equal("oops\n", toError.Stderr);
        Assert.Equal("a\nb\n", await fixture.ReadFileAsync("/home/alice/both.txt"));
        Assert.Equal("FROM FILE\n", input.Stdout);
        Assert.Equal("HI ALICE\n", here.Stdout);
        Assert.Equal("127\n", quiet.Stdout);
        Assert.Equal(string.Empty, quiet.Stderr);
    }

    [Fact]
    public async Task Unquoted_patterns_expand_to_matching_paths()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.MakeDirectoryAsync("/home/alice/g");
        foreach (string name in new[] { "b.txt", "a.txt", "c.md", ".hidden.txt" })
        {
            await fixture.WriteFileAsync($"/home/alice/g/{name}", "x");
        }

        Output output = await fixture.RunAsync("cd g && args *.txt; args '*.txt' *.none ?.md; args /home/alice/g/[ab].txt");

        Assert.Equal("a.txt|b.txt\n*.txt|*.none|c.md\n/home/alice/g/a.txt|/home/alice/g/b.txt\n", output.Stdout);
    }

    [Fact]
    public async Task A_script_without_the_execute_bit_is_refused_with_a_chmod_hint()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.WriteFileAsync("/home/alice/hello.sh", "#!/bin/sh\necho \"hello $1 from $0\"\n");
        await fixture.SetModeAsync("/home/alice/hello.sh", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Output refused = await fixture.RunAsync("./hello.sh bob");
        Output viaSh = await fixture.RunAsync("sh hello.sh bob");
        Output missing = await fixture.RunAsync("./nope.sh");

        Assert.Equal(126, refused.ExitCode);
        Assert.Contains("hsh: ./hello.sh: Permission denied", refused.Stderr);
        Assert.Contains("chmod +x ./hello.sh", refused.Stderr);
        Assert.Equal("hello bob from hello.sh\n", viaSh.Stdout);
        Assert.Equal(127, missing.ExitCode);
        Assert.Contains("No such file or directory", missing.Stderr);
    }

    [Fact]
    public async Task An_executable_script_runs_in_its_own_shell_process()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.WriteFileAsync(
            "/home/alice/deploy.sh",
            """
            #!/bin/bash
            # Comments and blank lines are fine.

            set -e
            greet() {
                echo "hi $1 ($#)"
            }
            cd /tmp
            SECRET=inside
            for target in "$@"; do
                greet "$target"
            done
            exitwith 3
            echo unreachable
            """);
        await fixture.SetModeAsync("/home/alice/deploy.sh", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Output output = await fixture.RunAsync("./deploy.sh web \"db server\"; echo \"after: $? $SECRET\"; pwd");

        Assert.Equal("hi web (1)\nhi db server (1)\nafter: 3 \n/home/alice\n", output.Stdout);
        ProcessRecord script = fixture.Manager.GetHistory().Last(p => p.AppId == ShellHostService.ShellAppId);
        Assert.Equal(fixture.Host.ProcessId, script.ParentPid);
        Assert.Equal(3, script.ExitCode);
    }

    [Fact]
    public async Task A_script_syntax_error_names_its_line()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.WriteFileAsync("/home/alice/bad.sh", "echo one\nif true; then\necho two\n");

        Output output = await fixture.RunAsync("sh bad.sh");

        Assert.Equal(2, output.ExitCode);
        Assert.StartsWith("bad.sh: line ", output.Stderr);
        Assert.Equal(string.Empty, output.Stdout);
    }

    [Fact]
    public async Task Sh_runs_command_strings_and_its_standard_input()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output command = await fixture.RunAsync("sh -c 'echo $0 $1; exit 7' name arg");
        Output piped = await fixture.RunAsync("echo 'echo piped; exitwith 4' | bash");
        Output sourced = await fixture.RunAsync("echo 'SOURCED=yes' > vars.sh; . ./vars.sh; echo $SOURCED");

        Assert.Equal("name arg\n", command.Stdout);
        Assert.Equal(7, command.ExitCode);
        Assert.Equal("piped\n", piped.Stdout);
        Assert.Equal(4, piped.ExitCode);
        Assert.Equal("yes\n", sourced.Stdout);
    }

    [Fact]
    public async Task Background_jobs_run_while_the_shell_goes_on()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output started = await fixture.RunAsync("hang & echo prompt");
        ShellJob job = Assert.Single(fixture.Session.Jobs.Jobs);
        Output waited = await fixture.RunAsync("exitwith 5 & wait %2; echo $?");

        Assert.Equal("prompt\n", started.Stdout);
        Assert.Equal(ShellJobState.Running, job.State);
        Assert.Equal("hang", job.Command);
        Assert.NotNull(fixture.Session.LastBackgroundProcessId);
        Assert.Equal("5\n", waited.Stdout);

        Assert.True(fixture.Interpreter.KillJob(fixture.Session, job.Id, force: true));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ShellJobState.Terminated, job.State);
    }

    [Fact]
    public async Task A_window_app_starts_by_its_short_name_and_writes_to_the_terminal()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Task<Output> running = fixture.RunAsync("terminal --profile dark");
        ProcessRecord window = await WaitForProcessAsync(fixture, Fixture.HostAppId, exclude: fixture.Host.ProcessId);
        Assert.Equal(fixture.Host.ProcessId, window.ParentPid);
        Assert.True(fixture.Orchestrator.TryGetRunningInstance(window.Pid, out _, out IAppExecutionContext? context));
        Assert.Equal(["terminal", "--profile", "dark"], context!.Environment.GetCommandLineArgs());
        using (KazoExecutionContext.Enter(context))
        {
            KazoConsole.WriteLine("hello from the window");
        }

        Assert.False(running.IsCompleted);
        await fixture.Orchestrator.StopAsync(window.Pid);
        Output output = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, output.ExitCode);
        Assert.Equal("hello from the window\n", output.Stdout);
    }

    [Fact]
    public async Task Interrupting_a_foreground_window_app_closes_it()
    {
        Fixture fixture = await Fixture.CreateAsync();
        using CancellationTokenSource cancellation = new();

        Task<Output> running = fixture.RunAsync("terminal", cancellation.Token);
        ProcessRecord window = await WaitForProcessAsync(fixture, Fixture.HostAppId, exclude: fixture.Host.ProcessId);
        cancellation.Cancel();
        Output output = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(130, output.ExitCode);
        Assert.False(fixture.Manager.TryGetActive(window.Pid, out _));
    }

    private static async Task<ProcessRecord> WaitForProcessAsync(Fixture fixture, string appId, ProcessId exclude)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            ProcessRecord? found = fixture.Manager.GetActiveProcesses().FirstOrDefault(p => p.AppId == appId && p.Pid != exclude);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"No process of {appId} started.");
    }
}

/// <summary>The hosted shell tells its terminal when a typed command is unfinished (PS2, ADR 0064).</summary>
public sealed partial class ShellInterpreterTests
{
    [Theory]
    [InlineData("for x in a b; do", true)]
    [InlineData("for x in a b; do\necho $x\ndone", false)]
    [InlineData("echo \"open", true)]
    [InlineData("ls |", true)]
    [InlineData("cat <<EOF", true)]
    [InlineData("ls -l", false)]
    [InlineData("fi", false)]
    public async Task The_hosted_shell_reports_unfinished_commands(string text, bool unfinished)
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Grant(AppCapabilities.ShellHost);
        await using Kazo.Os.AppSdk.Shell.IHackShell shell = await Kazo.Os.AppSdk.Shell.KazoShell.StartAsync(fixture.Host);

        Assert.Equal(unfinished, shell.NeedsMoreInput(text));
    }
}
