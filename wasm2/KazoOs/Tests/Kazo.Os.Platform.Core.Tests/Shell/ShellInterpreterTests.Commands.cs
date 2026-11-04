using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Tests.Shell;

/// <summary>The Linux commands of ADR 0064, run through the real shell, processes and filesystem.</summary>
public sealed partial class ShellInterpreterTests
{
    [Fact]
    public async Task Test_and_bracket_drive_conditions()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.WriteFileAsync("/home/alice/f.txt", "x");

        Output output = await fixture.RunAsync(
            "if [ -f f.txt ] && [ ! -d f.txt ] && test 3 -lt 10 -a abc = abc; then echo yes; fi; " +
            "[ -e missing ] || echo no; [ -z \"\" ] && [ -n x ] && echo strings; [ 1 -eq 2 ]; echo $?; [ 1 = 1");

        Assert.Equal("yes\nno\nstrings\n1\n", output.Stdout);
        Assert.Contains("missing `]'", output.Stderr);
        Assert.Equal(0, (await fixture.RunAsync("true")).ExitCode);
        Assert.Equal(1, (await fixture.RunAsync("false")).ExitCode);
    }

    [Fact]
    public async Task Read_assigns_fields_from_standard_input()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output output = await fixture.RunAsync(
            "printf 'alice 42 a b c\\nnext\\n' > in.txt; { read name age rest; read line; } < in.txt; echo \"$name|$age|$rest|$line\"; " +
            "while read word; do echo \"<$word>\"; done < in.txt");

        Assert.Equal("alice|42|a b c|next\n<alice 42 a b c>\n<next>\n", output.Stdout);
    }

    [Fact]
    public async Task Printf_and_seq_format_numbers_and_text()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output output = await fixture.RunAsync("printf '%s=%03d %-4s|%5.2f %x\\n' a 7 ok 3.14159 255; printf '%s\\n' x y; seq 3; seq -s , 2 2 6");

        Assert.Equal("a=007 ok  | 3.14 ff\nx\ny\n1\n2\n3\n2,4,6\n", output.Stdout);
    }

    [Fact]
    public async Task Text_filters_compose_in_pipelines()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.WriteFileAsync("/home/alice/users.csv", "bob,30,paris\nalice,25,lyon\nbob,30,paris\ncarol,41,nice\n");

        Output cut = await fixture.RunAsync("cut -d , -f 1,3 users.csv | sort | uniq -c");
        Output sorted = await fixture.RunAsync("sort -t , -k 2 -n -r users.csv | head -n 2");
        Output tail = await fixture.RunAsync("tail -n +3 users.csv; tail -1 users.csv; head -c 3 users.csv; echo");
        Output tee = await fixture.RunAsync("echo copied | tee one.txt two.txt | upper");

        Assert.Equal("      1 alice,lyon\n      2 bob,paris\n      1 carol,nice\n", cut.Stdout);
        Assert.Equal("carol,41,nice\nbob,30,paris\n", sorted.Stdout);
        Assert.Equal("bob,30,paris\ncarol,41,nice\ncarol,41,nice\nbob\n", tail.Stdout);
        Assert.Equal("COPIED\n", tee.Stdout);
        Assert.Equal("copied\n", await fixture.ReadFileAsync("/home/alice/one.txt"));
        Assert.Equal("copied\n", await fixture.ReadFileAsync("/home/alice/two.txt"));
    }

    [Fact]
    public async Task Chmod_plus_x_makes_a_script_runnable()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.WriteFileAsync("/home/alice/run.sh", "echo ran $#");
        await fixture.SetModeAsync("/home/alice/run.sh", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Output before = await fixture.RunAsync("./run.sh a b");
        Output chmod = await fixture.RunAsync("chmod -v +x run.sh");
        Output after = await fixture.RunAsync("./run.sh a b");
        Output symbolic = await fixture.RunAsync("chmod go-r,u=rwx run.sh && test -x run.sh && echo exec");

        Assert.Equal(126, before.ExitCode);
        Assert.Contains("chmod +x ./run.sh", before.Stderr);
        Assert.Contains("0644 (-rw-r--r--) to 0755 (-rwxr-xr-x)", chmod.Stdout);
        Assert.Equal("ran 2\n", after.Stdout);
        Assert.Equal("exec\n", symbolic.Stdout);
        using (Kazo.Os.AppSdk.HackExecutionContext.Enter(fixture.Host))
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute,
                await HackFile.GetUnixFileModeAsync("/home/alice/run.sh"));
        }
    }

    [Fact]
    public async Task Ln_creates_symbolic_links_and_refuses_hard_links()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.WriteFileAsync("/home/alice/target.txt", "linked content");

        Output symbolic = await fixture.RunAsync("ln -s target.txt link.txt && cat1 link.txt");
        Output hard = await fixture.RunAsync("ln target.txt hard.txt");

        Assert.Equal("linked content", symbolic.Stdout);
        Assert.Equal(1, hard.ExitCode);
        Assert.Contains("Operation not supported", hard.Stderr);
        using (Kazo.Os.AppSdk.HackExecutionContext.Enter(fixture.Host))
        {
            HackFileInfo link = await HackFileInfo.GetAsync("/home/alice/link.txt");
            Assert.True(link.IsSymbolicLink);
            Assert.Equal("target.txt", link.LinkTarget);
        }
    }

    [Fact]
    public async Task Zip_and_unzip_round_trip_files_and_modes()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.MakeDirectoryAsync("/home/alice/project");
        await fixture.MakeDirectoryAsync("/home/alice/project/src");
        await fixture.WriteFileAsync("/home/alice/project/README", "hello zip");
        await fixture.WriteFileAsync("/home/alice/project/src/run.sh", "echo hi");
        await fixture.SetModeAsync("/home/alice/project/src/run.sh", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Output zip = await fixture.RunAsync("zip -r -q backup project");
        Output list = await fixture.RunAsync("unzip -l backup.zip");
        Output unzip = await fixture.RunAsync("unzip -q backup.zip -d restored && cat1 restored/project/README");

        Assert.Equal(0, zip.ExitCode);
        Assert.Contains("project/src/run.sh", list.Stdout);
        Assert.Equal("hello zip", unzip.Stdout);
        using (Kazo.Os.AppSdk.HackExecutionContext.Enter(fixture.Host))
        {
            Assert.True((await HackFile.GetUnixFileModeAsync("/home/alice/restored/project/src/run.sh")).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public async Task Whoami_basename_dirname_which_and_du_report_like_coreutils()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.MakeDirectoryAsync("/home/alice/data");
        await fixture.WriteFileAsync("/home/alice/data/a.bin", new string('x', 5000));

        Output output = await fixture.RunAsync(
            "whoami; basename /a/b/archive.tar.gz .gz; dirname /a/b/c.txt; which sort sh; du -s data; du -sh data");

        Assert.Equal("alice\narchive.tar\n/a/b\n/usr/bin/sort\n/bin/sh\n12\tdata\n12K\tdata\n", output.Stdout);
    }

    [Fact]
    public async Task Locate_finds_files_by_name_or_pattern()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.MakeDirectoryAsync("/home/alice/docs");
        await fixture.WriteFileAsync("/home/alice/docs/report-2026.txt", "x");
        await fixture.WriteFileAsync("/home/alice/docs/notes.md", "x");

        Output substring = await fixture.RunAsync("locate report");
        Output pattern = await fixture.RunAsync("locate -b '*.md'");
        Output missing = await fixture.RunAsync("locate nothing-like-this");

        Assert.Equal("/home/alice/docs/report-2026.txt\n", substring.Stdout);
        Assert.Equal("/home/alice/docs/notes.md\n", pattern.Stdout);
        Assert.Equal(1, missing.ExitCode);
    }

    [Fact]
    public async Task Jobs_lists_background_jobs_and_kill_stops_them()
    {
        Fixture fixture = await Fixture.CreateAsync();

        await fixture.RunAsync("sleep 30 &");
        Output jobs = await fixture.RunAsync("jobs");
        Output killed = await fixture.RunAsync("kill %1; wait %1; echo $?");

        Assert.Equal("[1]+  Running                 sleep 30 &\n", jobs.Stdout);
        Assert.Equal("143\n", killed.Stdout);
    }

    [Fact]
    public async Task Kill_terminates_a_live_process_and_kill_9_kills_it()
    {
        // ADR 0069, section 4: kill (SIGTERM) stops it politely, kill -9 (SIGKILL) at once.
        Fixture fixture = await Fixture.CreateAsync();

        long sleeping = await StartSleepAsync(fixture);
        Output terminated = await fixture.RunAsync($"kill {sleeping}; echo $?; kill -0 {sleeping}; echo $?");
        long stubborn = await StartSleepAsync(fixture);
        Output killed = await fixture.RunAsync($"kill -9 {stubborn}; echo $?; kill -0 {stubborn}; echo $?");

        Assert.Equal("0\n1\n", terminated.Stdout);
        Assert.Equal("0\n1\n", killed.Stdout);
        // sleep honors its token: it ends by itself within the grace period. kill -9 gives it no chance.
        Assert.NotEqual(ProcessExitReason.Timeout, fixture.Manager.GetHistory().Last(r => r.Pid.Value == sleeping).ExitReason);
        Assert.Equal(ProcessExitReason.Killed, fixture.Manager.GetHistory().Last(r => r.Pid.Value == stubborn).ExitReason);

        static async Task<long> StartSleepAsync(Fixture fixture)
        {
            HashSet<long> before = [.. fixture.Manager.GetActiveProcesses().Select(static p => p.Pid.Value)];
            await fixture.RunAsync("sleep 30 &");
            for (int attempt = 0; attempt < 400; attempt++)
            {
                if (fixture.Manager.GetActiveProcesses().FirstOrDefault(p => p.AppId.EndsWith(".sleep", StringComparison.Ordinal) && !before.Contains(p.Pid.Value)) is { } process)
                {
                    return process.Pid.Value;
                }

                await Task.Delay(5);
            }

            throw new TimeoutException("sleep did not start.");
        }
    }

    [Fact]
    public async Task Grep_r_searches_a_tree_and_names_each_file()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.MakeDirectoryAsync("/home/alice/src");
        await fixture.MakeDirectoryAsync("/home/alice/src/lib");
        await fixture.WriteFileAsync("/home/alice/src/main.c", "int main() { return 0; }\n");
        await fixture.WriteFileAsync("/home/alice/src/lib/util.c", "int helper() { return 1; }\nvoid nothing() {}\n");

        Output recursive = await fixture.RunAsync("grep -r return src");
        Output here = await fixture.RunAsync("cd src && grep -rn nothing");

        Assert.Equal("src/lib/util.c:int helper() { return 1; }\nsrc/main.c:int main() { return 0; }\n", recursive.Stdout);
        Assert.Equal("lib/util.c:2:void nothing() {}\n", here.Stdout);
    }

    [Fact]
    public async Task Ip_shows_interfaces_and_routes()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output addresses = await fixture.RunAsync("ip -br addr");
        Output route = await fixture.RunAsync("ip route");

        Assert.Equal("lo               UP             127.0.0.1/8\neth0             UP             192.168.1.100/24\n", addresses.Stdout);
        Assert.StartsWith("default via 192.168.1.1 dev eth0", route.Stdout);
    }

    [Fact]
    public async Task Top_in_batch_mode_prints_its_summary()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output output = await fixture.RunAsync("top -b -n 1 | head -n 1");

        Assert.StartsWith("top - ", output.Stdout);
        Assert.Contains("load average:", output.Stdout);
    }
}
