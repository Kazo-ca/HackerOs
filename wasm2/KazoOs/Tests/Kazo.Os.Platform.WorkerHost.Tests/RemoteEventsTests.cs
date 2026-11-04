using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.WorkerHost.Tests;
using Kazo.Os.Platform.WorkerHost.Worker;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Workers;

// ADR 0124, section 2: the payloads of the test apps cross between a worker and the OS as JSON.
[assembly: HackWireContext(typeof(RemoteEventsJsonContext))]

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>A payload declared by the test assembly's serializer context.</summary>
public sealed record PingPayload(int N, string Text);

/// <summary>A payload no context declares: it cannot cross into or out of a worker.</summary>
public sealed record UndeclaredPayload(int N);

/// <summary>Serializer context of the test payloads (ADR 0124, section 2).</summary>
[JsonSerializable(typeof(PingPayload))]
internal sealed partial class RemoteEventsJsonContext : JsonSerializerContext;

/// <summary>
/// ADR 0124: <c>HackEvents</c> (topic messages, channels, typed events, shared channels) and
/// <c>Watch</c> reach a process in a worker by message. Each scenario runs in-process and in loopback
/// Web Workers with the same result, except where the ADR says a worker differs (an undeclared payload).
/// </summary>
public sealed class RemoteEventsTests
{
    public static TheoryData<bool> BothModes => new() { false, true };

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task A_command_subscribes_to_its_own_topic_and_receives_what_it_publishes(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        ShellRun run = await os.RunAsync("ev-topic").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal(
            "Delivered\nDelivered\nDelivered\n1:p1:org.test.ev-topic\n2:p2:org.test.ev-topic\n3:p3:org.test.ev-topic\n",
            run.Stdout);
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task A_channel_subscription_reads_in_order_and_completes_when_disposed(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        ShellRun run = await os.RunAsync("ev-chan").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("10:a\n11:b\ncompleted\n", run.Stdout);
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task A_kernel_event_published_on_the_main_thread_reaches_a_subscriber(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        Task<ShellRun> run = os.RunAsync("ev-kernel");
        // The subscription is made when the command starts: publish until it has seen one event.
        while (!run.IsCompleted)
        {
            os.EventBus.Publish(new AppDisabledEvent("org.test.gone"));
            await Task.WhenAny(run, Task.Delay(20));
        }

        ShellRun done = await run.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(done.ExitCode == 0, done.Stderr);
        Assert.Equal("disabled org.test.gone\n", done.Stdout);
        Assert.Equal(0, ((IWireEventBus)os.EventBus).CountWireSubscriptions(WirePayloadNames.Of(typeof(AppDisabledEvent))));
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task A_directory_watch_sees_a_file_the_command_creates(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        ShellRun run = await os.RunAsync("ev-watch").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("Created /home/alice/watched/a.txt\n", run.Stdout);
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task A_subscription_the_shared_channel_policy_refuses_fails_for_the_reader(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        os.TopicBus.RegisterSharedChannel(
            RemoteEventCommands.LockedRoot, SharedChannelPolicy.OwnerOnly(), new PublisherIdentity("org.test.owner", "owner", "1"));

        ShellRun run = await os.RunAsync("ev-denied").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("denied\n", run.Stdout);
    }

    [Fact]
    public async Task An_undeclared_payload_works_on_the_main_thread_and_names_the_attribute_in_a_worker()
    {
        await using OsFixture main = await StartAsync(workers: false);
        await using OsFixture worker = await StartAsync(workers: true);

        ShellRun inProcess = await main.RunAsync("ev-undeclared").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun inWorker = await worker.RunAsync("ev-undeclared").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("subscribed\n", inProcess.Stdout);
        Assert.Equal("not supported: names HackWireContext\n", inWorker.Stdout);
    }

    [Fact]
    public async Task A_message_published_in_a_worker_reaches_a_subscriber_on_the_main_thread_as_an_object()
    {
        await using OsFixture os = await StartAsync(workers: true);
        ConcurrentQueue<TopicMessage<PingPayload>> received = new();
        using IDisposable subscription = os.TopicBus.Subscribe<PingPayload>(
            RemoteEventCommands.ShoutTopic, new PublisherIdentity("org.test.listener", "listener", "1"), received.Enqueue);

        ShellRun run = await os.RunAsync("ev-shout").WaitAsync(TimeSpan.FromSeconds(20));
        await OsFixture.WaitUntilAsync(() => !received.IsEmpty, because: "the publication reached the main thread");

        Assert.True(run.ExitCode == 0, run.Stderr);
        TopicMessage<PingPayload> message = Assert.Single(received);
        Assert.Equal(new PingPayload(7, "shout"), message.Payload);
        Assert.Equal("org.test.ev-shout", message.PublisherAppId);
        Assert.Equal(RemoteEventCommands.ShoutTopic, message.Topic);
    }

    [Fact]
    public async Task A_publication_on_a_topic_the_process_does_not_own_is_refused_in_its_log()
    {
        await using OsFixture os = await StartAsync(workers: true);

        ShellRun run = await os.RunAsync("ev-foreign").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(run.ExitCode == 0, run.Stderr);
        await OsFixture.WaitUntilAsync(
            () => os.ReadAppLogAsync("org.test.ev-foreign").Result.Contains("TopicNotOwnedByCaller", StringComparison.Ordinal),
            because: "the OS wrote the refusal to the app's log");
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task PublishAsync_returns_the_outcome_and_the_subscriber_faults_decided_by_the_OS(bool workers)
    {
        // ADR 0145: the same answer on the main thread and in a worker, refusals included.
        await using OsFixture os = await StartAsync(workers);
        using IDisposable faulty = os.TopicBus.Subscribe<PingPayload>(
            RemoteEventCommands.PublishAsyncFaultyTopic,
            new PublisherIdentity("org.test.listener", "listener", "1"),
            static _ => throw new InvalidOperationException("boom"));

        ShellRun run = await os.RunAsync("ev-pubasync").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("own Delivered 0\nforeign TopicNotOwnedByCaller 0\nfaulty Delivered 1 boom\n", run.Stdout);
        Assert.DoesNotContain("refused a publication", await os.ReadAppLogAsync("org.test.ev-pubasync"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Killing_a_service_in_its_worker_drops_its_subscriptions_from_the_bus_at_once()
    {
        await using OsFixture os = await StartAsync(workers: true);
        IWireTopicMessageBus bus = os.TopicBus;

        await os.RunAsync("systemctl start org.test.ev-listener").WaitAsync(TimeSpan.FromSeconds(10));
        await OsFixture.WaitUntilAsync(() => bus.CountSubscriptions(RemoteEventCommands.ListenerTopic) == 1, because: "the service subscribed");
        long pid = os.FindPid("org.test.ev-listener")!.Value;
        os.TopicBus.Publish(RemoteEventCommands.ListenerTopic, new PingPayload(1, "hello"), new PublisherIdentity("org.test.ev-listener", "x", "1"));
        await OsFixture.WaitUntilAsync(
            () => os.ReadAppLogAsync("org.test.ev-listener").Result.Contains("heard 1:hello", StringComparison.Ordinal),
            because: "the service received the message in its worker");

        await os.RunAsync(os.OpenShell(), $"kill -9 {pid}").WaitAsync(TimeSpan.FromSeconds(10));

        await OsFixture.WaitUntilAsync(() => bus.CountSubscriptions(RemoteEventCommands.ListenerTopic) == 0, because: "the kill unsubscribed");
    }

    [Fact]
    public async Task The_sample_ticker_service_runs_in_a_dedicated_worker_and_its_ticks_reach_the_main_thread()
    {
        await using OsFixture os = await StartAsync(workers: true);
        TopicName ticked = Kazo.Os.Samples.ServiceApp.SampleTickerTopics.Ticked;
        ConcurrentQueue<TopicMessage<Kazo.Os.Samples.ServiceApp.SampleTickerEvent>> ticks = new();
        using IDisposable subscription = os.TopicBus.Subscribe<Kazo.Os.Samples.ServiceApp.SampleTickerEvent>(
            ticked, new PublisherIdentity("org.test.listener", "listener", "1"), ticks.Enqueue);

        await os.RunAsync("systemctl start org.hackeros.samples.service-app").WaitAsync(TimeSpan.FromSeconds(10));
        await OsFixture.WaitUntilAsync(() => os.FindPid("org.hackeros.samples.service-app") is not null);
        long pid = os.FindPid("org.hackeros.samples.service-app")!.Value;
        await OsFixture.WaitUntilAsync(() => os.Pool!.GetWorkers().Any(w => w.Role == WorkerRole.Dedicated && w.ProcessIds.Contains(pid)));
        for (int second = 0; second < 100 && ticks.Count < 2; second++)
        {
            os.Clock.Advance();
            await Task.Delay(20);
        }

        Assert.True(ticks.Count >= 2, "two ticks reached the main thread");
        Assert.All(ticks, tick => Assert.Equal("org.hackeros.samples.service-app", tick.PublisherAppId));
        Assert.Equal(1, ticks.First().Payload.TickCount);
        Assert.StartsWith("OK - Tick #1", ticks.First().Payload.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_of_a_sample_project_is_loaded_on_demand_in_a_pool_worker()
    {
        RecordingLoader.Loaded.Clear();
        await using OsFixture os = await StartAsync(workers: true, () => new RecordingLoader());

        ShellRun run = await os.RunAsync("sample-cmd from-a-worker").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("from-a-worker\n", run.Stdout);
        Assert.Contains("Kazo.Os.Samples.TerminalApp.dll", RecordingLoader.Loaded);
    }

    [Theory]
    [InlineData("Kazo.Os.Commands.dll", true)]
    [InlineData("Kazo.Os.Samples.TerminalApp.dll", true)]
    [InlineData("Kazo.Os.Boot.dll", true)]
    [InlineData("Evil.dll", false)]
    public void A_worker_loads_only_assemblies_known_to_the_build_or_already_in_its_runtime(string file, bool loadable)
    {
        HashSet<string> known = new(StringComparer.Ordinal) { "Kazo.Os.Commands.dll", "Kazo.Os.Samples.TerminalApp.dll" };

        Assert.Equal(loadable, WorkerProcessHost.IsLoadable(known, file, name => name == "Kazo.Os.Boot"));
        Assert.True(WorkerProcessHost.IsLoadable(new HashSet<string>(), file, static _ => false));
    }

    private static async Task<OsFixture> StartAsync(bool workers, Func<IWorkerAssemblyLoader>? loaderFactory = null)
    {
        OsFixture os = await OsFixture.CreateAsync(workers, loaderFactory: loaderFactory);
        if (workers)
        {
            os.Startup.MarkStarted();
        }

        return os;
    }

    /// <summary>Records which app assemblies the workers loaded.</summary>
    private sealed class RecordingLoader : ResolvedAssemblyLoader
    {
        public static ConcurrentBag<string> Loaded { get; } = [];

        public override Task<Type> LoadEntryTypeAsync(string assemblyFile, string typeName)
        {
            Loaded.Add(assemblyFile);
            return base.LoadEntryTypeAsync(assemblyFile, typeName);
        }
    }
}

/// <summary>The test apps of <see cref="RemoteEventsTests"/>.</summary>
internal static class RemoteEventCommands
{
    /// <summary>A shared channel root registered owner-only by another app.</summary>
    public static TopicName LockedRoot { get; } = TopicNames.Shared("org-test-locked").Segment("data").Build();

    /// <summary>The topic <c>ev-shout</c> publishes on (its own namespace).</summary>
    public static TopicName ShoutTopic { get; } = TopicNames.ForApp("org.test.ev-shout").Segment("shout").Build();

    /// <summary>The topic the <c>ev-listener</c> service listens on (its own namespace).</summary>
    public static TopicName ListenerTopic { get; } = TopicNames.ForApp("org.test.ev-listener").Segment("in").Build();

    /// <summary>A topic of <c>ev-pubasync</c> whose main-thread subscriber throws.</summary>
    public static TopicName PublishAsyncFaultyTopic { get; } = TopicNames.ForApp("org.test.ev-pubasync").Segment("faulty").Build();

    public static IReadOnlyList<AppManifest> Manifests { get; } =
    [
        TestCommands.Command<TopicCommand>("ev-topic"),
        TestCommands.Command<ChannelCommand>("ev-chan"),
        TestCommands.Command<KernelEventCommand>("ev-kernel"),
        TestCommands.Command<WatchCommand>("ev-watch") with
        {
            Capabilities = [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite]
        },
        TestCommands.Command<DeniedCommand>("ev-denied"),
        TestCommands.Command<UndeclaredCommand>("ev-undeclared"),
        TestCommands.Command<ShoutCommand>("ev-shout"),
        TestCommands.Command<ForeignCommand>("ev-foreign"),
        TestCommands.Command<PublishAsyncCommand>("ev-pubasync"),
        TestCommands.Service<ListenerService>("org.test.ev-listener")
    ];

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static TopicName OwnTopic(AppManifest manifest, string segment) => TopicNames.ForApp(manifest.Id).Segment(segment).Build();

    /// <summary><c>ev-topic</c>: a callback subscription on its own topic, three publications.</summary>
    public sealed class TopicCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            TopicName topic = OwnTopic(Manifest, "ping");
            ConcurrentQueue<string> got = new();
            TaskCompletionSource three = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using IDisposable subscription = context.App.Events.Subscribe<PingPayload>(topic, message =>
            {
                got.Enqueue($"{message.Payload.N}:{message.Payload.Text}:{message.PublisherAppId}");
                if (got.Count == 3)
                {
                    three.TrySetResult();
                }
            });
            for (int n = 1; n <= 3; n++)
            {
                TopicPublishResult result = context.App.Events.Publish(topic, new PingPayload(n, $"p{n}"));
                await context.StandardOutput.WriteLineAsync(result.Outcome.ToString());
            }

            await three.Task.WaitAsync(Patience, cancellationToken);
            foreach (string line in got)
            {
                await context.StandardOutput.WriteLineAsync(line);
            }

            return 0;
        }
    }

    /// <summary><c>ev-chan</c>: a bounded channel subscription, read in order, then disposed.</summary>
    public sealed class ChannelCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            TopicName topic = OwnTopic(Manifest, "chan");
            ITopicChannelSubscription<PingPayload> subscription = context.App.Events.SubscribeChannel<PingPayload>(topic, boundedCapacity: 8);
            context.App.Events.Publish(topic, new PingPayload(10, "a"));
            context.App.Events.Publish(topic, new PingPayload(11, "b"));
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Patience);
            for (int i = 0; i < 2; i++)
            {
                TopicMessage<PingPayload> message = await subscription.Reader.ReadAsync(timeout.Token);
                await context.StandardOutput.WriteLineAsync($"{message.Payload.N}:{message.Payload.Text}");
            }

            await subscription.DisposeAsync();
            await subscription.Reader.Completion.WaitAsync(Patience, cancellationToken);
            await context.StandardOutput.WriteLineAsync("completed");
            return 0;
        }
    }

    /// <summary><c>ev-kernel</c>: waits for one <see cref="AppDisabledEvent"/> of the kernel lane.</summary>
    public sealed class KernelEventCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            TaskCompletionSource<string> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (context.App.Events.Subscribe<AppDisabledEvent>(e => first.TrySetResult(e.AppId)))
            {
                string appId = await first.Task.WaitAsync(Patience, cancellationToken);
                await context.StandardOutput.WriteLineAsync($"disabled {appId}");
            }

            // The unsubscribe is a message: give the OS a moment, as a process exit would.
            await Task.Delay(100, cancellationToken);
            return 0;
        }
    }

    /// <summary><c>ev-watch</c>: watches a directory of its home, creates a file in it, reports the change.</summary>
    public sealed class WatchCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            const string directory = "/home/alice/watched";
            await Kazo.Os.AppSdk.IO.HackDirectory.CreateDirectoryAsync(directory, cancellationToken);
            await using ITopicChannelSubscription<FileSystemChangeEvent> watch = await context.App.Watch.WatchAsync(
                VirtualPath.Parse(directory), FileSystemWatchScope.ImmediateChildren, cancellationToken);
            await Kazo.Os.AppSdk.IO.HackFile.WriteAllTextAsync(directory + "/a.txt", "x", cancellationToken);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Patience);
            while (true)
            {
                FileSystemChangeEvent change = (await watch.Reader.ReadAsync(timeout.Token)).Payload;
                if (change.Kind == FileSystemChangeKind.Created)
                {
                    await context.StandardOutput.WriteLineAsync($"{change.Kind} {change.Path.Value}");
                    return 0;
                }
            }
        }
    }

    /// <summary><c>ev-denied</c>: subscribes to an owner-only shared channel of another app.</summary>
    public sealed class DeniedCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            try
            {
                ITopicChannelSubscription<PingPayload> subscription = context.App.Events.SubscribeChannel<PingPayload>(LockedRoot);
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(Patience);
                await foreach (TopicMessage<PingPayload> _ in subscription.Reader.ReadAllAsync(timeout.Token))
                {
                    await context.StandardOutput.WriteLineAsync("received");
                }
            }
            catch (AppGatewayAccessDeniedException)
            {
                // On the main thread at once; in a worker, when the OS's answer arrives.
                await context.StandardOutput.WriteLineAsync("denied");
            }

            return 0;
        }
    }

    /// <summary><c>ev-undeclared</c>: subscribes with a payload type no serializer context declares.</summary>
    public sealed class UndeclaredCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            try
            {
                using IDisposable subscription = context.App.Events.Subscribe<UndeclaredPayload>(OwnTopic(Manifest, "x"), static _ => { });
                await context.StandardOutput.WriteLineAsync("subscribed");
            }
            catch (NotSupportedException exception)
            {
                await context.StandardOutput.WriteLineAsync(
                    $"not supported: {(exception.Message.Contains("HackWireContext", StringComparison.Ordinal) ? "names HackWireContext" : exception.Message)}");
            }

            return 0;
        }
    }

    /// <summary><c>ev-shout</c>: publishes one message on its own topic for a subscriber elsewhere.</summary>
    public sealed class ShoutCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            context.App.Events.Publish(ShoutTopic, new PingPayload(7, "shout"));
            await context.StandardOutput.WriteLineAsync("shouted");
            return 0;
        }
    }

    /// <summary><c>ev-foreign</c>: publishes on another app's topic, which the OS refuses.</summary>
    public sealed class ForeignCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            context.App.Events.Publish(ShoutTopic, new PingPayload(8, "not mine"));
            await context.StandardOutput.WriteLineAsync("published");
            return 0;
        }
    }

    /// <summary><c>ev-pubasync</c>: <c>PublishAsync</c> on its own topic, another app's topic and a topic with a faulty subscriber.</summary>
    public sealed class PublishAsyncCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            TopicPublishResult own = await context.App.Events.PublishAsync(OwnTopic(Manifest, "quiet"), new PingPayload(1, "own"), cancellationToken);
            await context.StandardOutput.WriteLineAsync($"own {own.Outcome} {own.SubscriberFaults.Count}");
            TopicPublishResult foreign = await context.App.Events.PublishAsync(ShoutTopic, new PingPayload(2, "foreign"), cancellationToken);
            await context.StandardOutput.WriteLineAsync($"foreign {foreign.Outcome} {foreign.SubscriberFaults.Count}");
            TopicPublishResult faulty = await context.App.Events.PublishAsync(PublishAsyncFaultyTopic, new PingPayload(3, "faulty"), cancellationToken);
            await context.StandardOutput.WriteLineAsync(
                $"faulty {faulty.Outcome} {faulty.SubscriberFaults.Count} {string.Join(",", faulty.SubscriberFaults.Select(f => f.Exception.Message))}");
            return 0;
        }
    }

    /// <summary><c>org.test.ev-listener</c>: a service that logs every message of its topic until stopped.</summary>
    public sealed class ListenerService(AppManifest manifest) : ServiceAppBase(manifest)
    {
        protected override async Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
        {
            using IDisposable subscription = context.Events.Subscribe<PingPayload>(ListenerTopic, message =>
                context.Logging.Log(
                    Kazo.Os.Simulation.Abstractions.Diagnostics.DiagnosticSeverity.Information,
                    $"heard {message.Payload.N}:{message.Payload.Text}"));
            await Task.Delay(Timeout.Infinite, sessionCancellationToken);
        }
    }
}
