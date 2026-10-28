using HackerOs.App.Abstractions;

namespace HackerOs.Platform.WorkerHost.Tests;

/// <summary>
/// Section 5.1 of the plan, ADR 0070 consequences: every built-in command of <c>HackerOs.Commands</c>
/// runs in both modes — in-process (no worker host) and through Web Workers (loopback runtime, real
/// protocol) — and gives the same exit codes, the same output and leaves the same files.
/// </summary>
public sealed class CommandParityTests
{
    private const string Home = "/home/alice";

    /// <summary>One scenario per built-in command (a few commands share one), keyed by its name.</summary>
    private static readonly Scenario[] All =
    [
        new("alias", ["alias ll='ls -l'", "alias", "alias ll"]),
        new("ausearch", ["ausearch --help", "ausearch -f /etc/hostname", "ausearch --bogus"]),
        new("basename", ["basename /a/b/c.txt .txt", "basename -a /x/y /z"]),
        new("cat", ["cat notes.txt", "cat missing.txt", "cat notes.txt numbers.txt | cat"]),
        new("cd", ["cd docs && pwd", "cd /nope"]),
        new("chmod", ["chmod 600 notes.txt", "chmod -R go-w docs", "chmod 777 /etc/hostname"]),
        new("clear", ["clear"]),
        new("cp", ["cp notes.txt copy.txt", "cp -r docs docs2", "cp missing.txt x"]),
        new("curl", ["curl http://example.hackeros/", "curl -I http://example.hackeros/", "curl http://nowhere.invalid/"]),
        new("cut", ["cut -d, -f2 table.csv", "cut -c1-3 notes.txt"]),
        new("df", ["df", "df -h -T"]),
        new("localectl", ["localectl", "localectl list-locales", "localectl set-locale LANG=xx_YY.UTF-8", "localectl set-locale LANG=fr_FR.UTF-8", "localectl bogus"]),
        new("hackbackup", ["hackbackup", "hackbackup create", "hackbackup restore --bogus"]),
        new("hackpkg", ["hackpkg", "hackpkg bogus", "hackpkg install --bogus", "hackpkg list --allow-untrusted"]),
        new("diff", ["diff notes.txt notes.txt", "diff -u notes.txt numbers.txt"]),
        new("dmesg", ["dmesg -l emerg", "dmesg -l nosuch", "dmesg --bogus"]),
        new("dirname", ["dirname /a/b/c /d"]),
        new("du", ["du -s docs", "du -a -h docs"]),
        new("echo", ["echo hello world", "echo -n no newline"]),
        new("env", ["env | sort"]),
        new("exit", ["exit 3"]),
        new("export", ["export GREETING=hi && env | grep GREETING"]),
        new("false", ["false", "true && false || echo recovered"]),
        new("find", ["find docs", "find . -name '*.txt' -type f"]),
        new("grep", ["grep -n line notes.txt", "grep -r -i deep docs", "grep nothing notes.txt", "cat notes.txt | grep -v first"]),
        new("head", ["head -n 2 notes.txt", "seq 100 | head -3", "head -c 5 notes.txt"]),
        new("help", ["help", "help ls"]),
        new("htop", ["htop"]),
        new("ip", ["ip addr", "ip -br link", "ip route"]),
        new("jobs", ["jobs"]),
        new("kill", ["kill -l", "kill 99999", "kill"]),
        new("launch", ["launch org.nope.app", "launch"]),
        new("ln", ["ln -s notes.txt link.txt && cat link.txt", "ln notes.txt"]),
        new("locate", ["locate notes", "locate -c txt"]),
        new("ls", ["ls", "ls -l docs", "ls -a", "ls /nope"]),
        new("mkdir", ["mkdir new", "mkdir -p a/b/c", "mkdir new"]),
        new("mount", ["mount", "mount -t nfs /mnt/x", "mount --bogus"]),
        new("mv", ["mv notes.txt moved.txt", "mv missing.txt x"]),
        new("nano", ["nano notes.txt"]),
        new("netstat", ["netstat", "netstat -i"]),
        new("nice", ["nice", "nice -n 5 echo hi", "nice -n 5 nice", "nice -n 5 ps -o ni,comm", "nice -n bogus echo hi", "nice -n -5 echo hi"]),
        new("nslookup", ["nslookup example.hackeros", "nslookup nowhere.invalid", "nslookup"]),
        new("nmap", ["nmap example.hackeros", "nmap -p 80-443 -sV 10.0.0.10"]),
        new("passwd", ["passwd -S", "passwd bob", "passwd -x"]),
        new("ping", ["ping example.hackeros", "ping nowhere.invalid"]),
        new("prlimit", ["prlimit --nofile --nproc", "prlimit --nofile=512:1024 --nofile", "prlimit --pid 99999", "prlimit --bogus"]),
        new("printf", ["printf '%s-%d\\n' a 1 b 2"]),
        new("ps", ["ps", "ps -a -u", "ps -o pid,ni,comm", "ps -o bogus"]),
        new("pwd", ["pwd"]),
        new("read", ["read -r first rest && echo \"$first|$rest\""], Input: "one two three\n"),
        new("renice", ["renice", "renice 5 99999", "renice -n 5 -g 1"]),
        new("rm", ["rm copy-of-nothing", "rm -r docs/sub", "rm --permanent numbers.txt"]),
        new("seq", ["seq 5", "seq -s , 2 2 10", "seq -w 8 11"]),
        new("sleep", ["sleep 2 && echo awake"], UsesClock: true),
        new("sort", ["sort words.txt", "sort -r -u words.txt", "sort -n numbers.txt", "sort -t, -k2 table.csv"]),
        new("stress", ["stress -c 1 -t 0", "stress --yield -t 0", "stress --vm 1 --vm-bytes 1M --vm-keep -t 0", "stress --vm 1 --vm-bytes 12Q", "stress --bogus"]),
        new("sudo", ["sudo echo hi"]),
        new("systemctl", ["systemctl list-units", "systemctl status org.test.ticker", "systemctl is-active org.test.ticker"]),
        new("tail", ["tail -n 2 notes.txt", "tail -c 6 notes.txt", "seq 20 | tail -n 3"]),
        new("tee", ["echo teed | tee out.txt", "echo again | tee -a out.txt"]),
        new("test", ["test -f notes.txt && echo file", "[ -d docs ] && echo dir", "test 3 -gt 5 || echo smaller"]),
        new("top", ["top -b -n 1"]),
        new("touch", ["touch fresh.txt", "touch docs/new.txt notes.txt"]),
        new("trash", ["trash put notes.txt", "trash list", "trash restore notes.txt", "trash put words.txt", "trash empty"]),
        new("true", ["true"]),
        new("uniq", ["sort words.txt | uniq -c", "uniq -d words.txt"]),
        new("unset", ["export TEMP_VAR=1 && unset TEMP_VAR && env | grep TEMP_VAR"]),
        new("umount", ["umount", "umount /mnt/nowhere"]),
        new("unzip", ["zip -r -q archive.zip docs", "unzip -l archive.zip", "unzip -o -d extracted archive.zip", "unzip -t archive.zip"]),
        new("watch", ["watch -e -n 1 cat missing.txt", "watch -e false"]),
        new("wc", ["wc notes.txt", "cat notes.txt | wc -l", "wc -w words.txt numbers.txt"]),
        new("wget", ["wget -q -O page.html http://example.hackeros/", "wget --spider http://example.hackeros/", "wget http://nowhere.invalid/"]),
        new("which", ["which ls", "which -a cd nope"]),
        new("whoami", ["whoami"]),
        new("xdg-open", ["xdg-open missing.txt", "xdg-open", "xdg-open notes.txt", "xdg-open docs", "xdg-open nope://thing"]),
        new("xdg-mime", ["xdg-mime", "xdg-mime query filetype notes.txt", "xdg-mime query filetype docs", "xdg-mime query filetype missing.txt",
            "xdg-mime default org.example.missing text/plain", "xdg-mime query default x-scheme-handler/https"]),
        new("zip", ["zip -q notes.zip notes.txt words.txt", "zip -r docs.zip docs", "unzip -l docs.zip"])
    ];

    public static TheoryData<string> Commands
    {
        get
        {
            TheoryData<string> data = [];
            foreach (Scenario scenario in All)
            {
                data.Add(scenario.Command);
            }

            return data;
        }
    }

    [Fact]
    public void Every_built_in_command_has_a_parity_scenario()
    {
        HashSet<string> covered = [.. All.Select(static scenario => scenario.Command)];
        string[] missing = [.. OsFixture.BuiltInCommands
            .Select(static definition => definition.Terminal!.Name)
            .Where(name => !covered.Contains(name))];

        Assert.Empty(missing);
        Assert.Equal(77, OsFixture.BuiltInCommands.Count);
    }

    [Fact]
    public void Every_built_in_command_declares_where_it_runs()
    {
        foreach (AppDefinition definition in OsFixture.BuiltInCommands)
        {
            AppManifest manifest = AppDefinitionMapper.ToManifest(definition);
            Assert.True(manifest.Execution is AppExecutionHost.Pool or AppExecutionHost.MainThread, manifest.Id);
        }
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task A_built_in_command_behaves_the_same_in_a_worker(string command)
    {
        Scenario scenario = All.Single(candidate => candidate.Command == command);

        Transcript inProcess = await RunAsync(scenario, workers: false);
        Transcript inWorker = await RunAsync(scenario, workers: true);

        Assert.DoesNotContain("command not found", inProcess.Text, StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("HACKEROS_PARITY_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            await File.WriteAllTextAsync(Path.Combine(dump, $"{command}.txt"), inWorker.Text);
        }

        Assert.Equal(inProcess.Text, inWorker.Text);
        Assert.Equal(inProcess.Files, inWorker.Files);
        bool runsInWorker = OsFixture.BuiltInCommands
            .Single(definition => definition.Terminal!.Name == command)
            .GetType()
            .GetCustomAttributes(typeof(AppManifestAttribute), false)
            .Cast<AppManifestAttribute>()
            .Single().Execution != AppExecutionHost.MainThread;
        if (runsInWorker)
        {
            Assert.True(inWorker.UsedWorkers, $"{command} should have run in a worker");
        }
    }

    private static async Task<Transcript> RunAsync(Scenario scenario, bool workers)
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers);
        await SeedAsync(os);
        List<string> lines = [];
        foreach (string line in scenario.Lines)
        {
            ShellRun run = scenario.UsesClock
                ? await os.RunWithClockAsync(line)
                : await os.RunAsync(line, scenario.Input).WaitAsync(TimeSpan.FromSeconds(20));
            lines.Add($"$ {line}\n[exit {run.ExitCode}]\n{run.Stdout}[stderr]\n{run.Stderr}");
        }

        // The user's ID is a fresh GUID per OS: name it, so both runs compare.
        string userId = os.Principal.UserId.ToString();
        return new Transcript(
            string.Join('\n', lines).Replace(userId, "<alice-uid>", StringComparison.Ordinal),
            (await os.SnapshotTreeAsync(Home)).Replace(userId, "<alice-uid>", StringComparison.Ordinal),
            os.Runtime is { StartedCount: > 0 });
    }

    private static async Task SeedAsync(OsFixture os)
    {
        await os.WriteFileAsync($"{Home}/notes.txt", "first line\nsecond line\nthird line\n");
        await os.WriteFileAsync($"{Home}/numbers.txt", "10\n2\n33\n4\n");
        await os.WriteFileAsync($"{Home}/words.txt", "pear\napple\npear\nfig\napple\n");
        await os.WriteFileAsync($"{Home}/table.csv", "a,3\nb,1\nc,2\n");
        await os.WriteFileAsync($"{Home}/docs/readme.md", "# Docs\n");
        await os.WriteFileAsync($"{Home}/docs/sub/deep.txt", "Deep content\n");
    }

    private sealed record Scenario(string Command, string[] Lines, string? Input = null, bool UsesClock = false);

    private sealed record Transcript(string Text, string Files, bool UsedWorkers);
}
