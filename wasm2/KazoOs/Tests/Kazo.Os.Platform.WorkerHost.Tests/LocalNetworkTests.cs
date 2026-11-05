using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Workers;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// ADR 0074, sections 2 to 4, through a whole OS and the real commands: web apps started on demand
/// by a first request, requests and replies across the listening socket (in-process and through the
/// worker protocol), the shared web worker, <c>EADDRINUSE</c>, privileged ports, connection
/// refused, <c>netstat -tlnp</c>, <c>/proc/net/tcp</c>, <c>nslookup</c> and <c>/etc/hosts</c>.
/// </summary>
public sealed class LocalNetworkTests
{
    public static TheoryData<bool> Modes => new() { false, true };

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_first_request_starts_the_web_app_and_reaches_it_over_loopback(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        Assert.Null(os.FindPid("org.test.intranet"));

        ShellRun run = await os.RunAsync("curl http://intranet.hack/hello?x=1").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal($"intranet GET intranet.hack/hello?x=1 session=- body= in={(workers ? "worker" : "main")} from=127.0.0.1\n", run.Stdout);
        Assert.NotNull(os.FindPid("org.test.intranet"));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Cookies_redirects_and_bodies_cross_the_local_socket(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        ShellRun login = await os.RunAsync("curl http://intranet.hack/login").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun post = await os.RunAsync("curl -d a=1 http://wiki.hack/form").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(login.ExitCode == 0, login.Stderr);
        // The 302 set the cookie on the first hop; the OS sent it back on the second.
        Assert.StartsWith("intranet GET intranet.hack/ session=agent", login.Stdout, StringComparison.Ordinal);
        Assert.StartsWith("wiki POST wiki.hack/form session=- body=a=1", post.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_cookie_header_given_to_curl_reaches_the_site_without_entering_the_jar(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        ShellRun manual = await os.RunAsync("curl -i -H 'Cookie: session=manual; theme=dark' http://intranet.hack/hello").WaitAsync(TimeSpan.FromSeconds(20));
        // The site sets session=agent then redirects to /: the typed cookie still wins on that hop.
        ShellRun login = await os.RunAsync("curl -H 'Cookie: session=manual' http://intranet.hack/login").WaitAsync(TimeSpan.FromSeconds(20));
        // A redirect to another host drops the typed cookie, as curl does.
        ShellRun away = await os.RunAsync("curl -H 'Cookie: session=manual' http://intranet.hack/away").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(manual.ExitCode == 0, manual.Stderr);
        Assert.Contains("intranet GET intranet.hack/hello session=manual ", manual.Stdout, StringComparison.Ordinal);
        // A request header stores nothing: the response does not echo it back as a Set-Cookie.
        Assert.DoesNotContain("Set-Cookie", manual.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.True(login.ExitCode == 0, login.Stderr);
        Assert.StartsWith("intranet GET intranet.hack/ session=manual ", login.Stdout, StringComparison.Ordinal);
        Assert.True(away.ExitCode == 0, away.Stderr);
        Assert.StartsWith("wiki GET wiki.hack/ session=- ", away.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_site_shares_the_one_web_worker_which_ends_with_the_last_site()
    {
        await using OsFixture os = await StartAsync(workers: true);

        await os.RunAsync("curl http://intranet.hack/").WaitAsync(TimeSpan.FromSeconds(20));
        await os.RunAsync("curl http://wiki.hack/").WaitAsync(TimeSpan.FromSeconds(20));
        long intranet = os.FindPid("org.test.intranet")!.Value;
        long wiki = os.FindPid("org.test.wiki")!.Value;

        WorkerSnapshot web = Assert.Single(os.Pool!.GetWorkers(), worker => worker.Role == WorkerRole.Web);
        Assert.Contains(intranet, web.ProcessIds);
        Assert.Contains(wiki, web.ProcessIds);
        ShellRun workersFile = await os.RunAsync("cat /proc/kazoos/workers").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(" web ", workersFile.Stdout, StringComparison.Ordinal);

        await StopAsync(os, intranet);
        await StopAsync(os, wiki);
        await OsFixture.WaitUntilAsync(() => os.Pool.GetWorkers().All(worker => worker.Role != WorkerRole.Web), because: "the web worker ends with its last site");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_stopped_site_frees_its_port_and_the_next_request_starts_it_again(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        await os.RunAsync("curl http://intranet.hack/").WaitAsync(TimeSpan.FromSeconds(20));
        long first = os.FindPid("org.test.intranet")!.Value;

        await StopAsync(os, first);
        Assert.DoesNotContain(os.Ports.Snapshot(), row => row.Owner?.Pid.Value == first);
        ShellRun again = await os.RunAsync("curl http://intranet.hack/").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(again.ExitCode == 0, again.Stderr);
        Assert.NotEqual(first, os.FindPid("org.test.intranet"));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Netstat_and_proc_net_tcp_list_the_listening_sockets_with_their_owner(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        await os.RunAsync("curl http://intranet.hack/").WaitAsync(TimeSpan.FromSeconds(20));
        long pid = os.FindPid("org.test.intranet")!.Value;

        ShellRun netstat = await os.RunAsync("netstat -tlnp").WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun tcp = await os.RunAsync("cat /proc/net/tcp").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains($"tcp        0      0 0.0.0.0:80              0.0.0.0:*               LISTEN      {pid}/intranet", netstat.Stdout, StringComparison.Ordinal);
        string[] lines = tcp.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("  sl  local_address rem_address   st", lines[0], StringComparison.Ordinal);
        // 0.0.0.0:80 in the kernel's hex, state 0A (LISTEN), uid 1000 for a user.
        Assert.Contains(lines, line => line.Contains(": 00000000:0050 00000000:0000 0A ", StringComparison.Ordinal) && line.Contains(" 1000 ", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Listening_needs_network_listen_privileged_ports_need_more_and_a_taken_prefix_is_address_in_use(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        ShellRun free = await os.RunAsync("listen http://localhost:8080/app/").WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun privileged = await os.RunAsync("listen http://+:80/admin/").WaitAsync(TimeSpan.FromSeconds(10));
        await os.RunAsync("curl http://intranet.hack/").WaitAsync(TimeSpan.FromSeconds(20));
        os.Grant("org.test.listen", AppCapabilities.NetworkListenPrivileged);
        ShellRun taken = await os.RunAsync("listen http://intranet.hack/").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(free.ExitCode == 0, free.Stderr);
        Assert.Equal("listening http://localhost:8080/app/\n", free.Stdout);
        // The command ended: its socket went with it.
        Assert.DoesNotContain(os.Ports.Snapshot(), row => row.Port == 8080);
        Assert.Equal(2, privileged.ExitCode);
        Assert.Equal(1, taken.ExitCode);
        Assert.Equal("listen: ERR_ADDRESS_IN_USE (98)\n", taken.Stderr);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Nothing_listening_is_connection_refused_and_an_unknown_host_on_an_open_port_is_a_400(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        ShellRun refused = await os.RunAsync("curl http://localhost:9999/").WaitAsync(TimeSpan.FromSeconds(10));
        await os.RunAsync("curl http://intranet.hack/").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun invalidHost = await os.RunAsync("curl http://127.0.0.1/").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(7, refused.ExitCode);
        Assert.StartsWith("curl: (7) Failed to connect to localhost port 9999", refused.Stderr, StringComparison.Ordinal);
        Assert.Contains("Bad Request - Invalid Hostname", invalidHost.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Nslookup_names_the_source_and_etc_hosts_wins_over_a_web_app(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        ShellRun webApp = await os.RunAsync("nslookup intranet.hack").WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun simulated = await os.RunAsync("nslookup example.kazoos").WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun unknown = await os.RunAsync("nslookup nowhere.hack").WaitAsync(TimeSpan.FromSeconds(10));
        await WriteHostsAsync(os.FileSystem, "127.0.0.1\tlocalhost\n10.0.0.10\tintranet.hack corp\n");
        ShellRun hosts = await os.RunAsync("nslookup intranet.hack").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(";; source: web app org.test.intranet (web.urls, this machine)\nName:\tintranet.hack\nAddress: 127.0.0.1\n", webApp.Stdout, StringComparison.Ordinal);
        Assert.Contains("Non-authoritative answer:\nName:\texample.kazoos\nAddress: 10.0.0.10\n", simulated.Stdout, StringComparison.Ordinal);
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("** server can't find nowhere.hack: NXDOMAIN", unknown.Stdout, StringComparison.Ordinal);
        Assert.Contains(";; source: /etc/hosts:2 (static table)\nName:\tintranet.hack\nAddress: 10.0.0.10\n", hosts.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task An_internal_name_is_never_relayed_and_etc_hosts_can_send_a_name_to_a_simulated_host(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        await WriteHostsAsync(os.FileSystem, "127.0.0.1\tlocalhost\n10.0.0.10\tshortcut.hack\n");

        ShellRun alias = await os.RunAsync("curl http://shortcut.hack/").WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun unknown = await os.RunAsync("curl http://nowhere.hack/").WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun direct = await os.RunAsync("curl http://example.kazoos/").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(alias.ExitCode == 0, alias.Stderr);
        Assert.Equal(direct.Stdout, alias.Stdout);
        Assert.Equal(6, unknown.ExitCode);
    }

    private static async Task<OsFixture> StartAsync(bool workers)
    {
        OsFixture os = await OsFixture.CreateAsync(workers);
        if (workers)
        {
            os.Startup.MarkStarted();
        }

        return os;
    }

    /// <summary>Stops a site like the task manager does, and waits until its process is gone.</summary>
    private static async Task StopAsync(OsFixture os, long pid)
    {
        Task stopping = os.Orchestrator.StopAsync(
            Kazo.Os.Simulation.Abstractions.Processes.ProcessId.FromInt64(pid),
            Kazo.Os.Simulation.Abstractions.Processes.ProcessExitReason.CloseRequested);
        for (int tick = 0; tick < 20 && !stopping.IsCompleted; tick++)
        {
            await Task.WhenAny(stopping, Task.Delay(30));
            os.Clock.Advance();
        }

        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        await OsFixture.WaitUntilAsync(() => !os.Manager.TryGetActive(Kazo.Os.Simulation.Abstractions.Processes.ProcessId.FromInt64(pid), out _));
    }

    /// <summary>Overwrites <c>/etc/hosts</c> as root, like an administrator with <c>sudo nano</c>.</summary>
    private static async Task WriteHostsAsync(IFileSystemService fileSystem, string content)
    {
        AppOperationContext root = new()
        {
            AppId = "org.kazoos.kernel",
            UserId = "system",
            UserAuthority = AppAuthority.System,
            GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
            IsSystemOperation = true
        };
        FileSystemMutationResult write = await fileSystem.WriteAsync(
            new FileSystemWriteRequest(VirtualPath.Parse("/etc/hosts")), new TextSource(content),
            new FileSystemAuthorizationContext(root, ["system"], DateTimeOffset.UtcNow));
        Assert.True(write.Succeeded, write.Transaction.Error?.Code.ToString());
    }

    private sealed class TextSource(string content) : IFileSystemContentSource
    {
        private readonly byte[] _bytes = System.Text.Encoding.UTF8.GetBytes(content);

        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text();

        public long? Length => _bytes.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
    }
}
