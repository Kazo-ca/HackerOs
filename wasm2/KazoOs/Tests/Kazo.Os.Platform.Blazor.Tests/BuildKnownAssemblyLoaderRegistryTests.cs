using System.Reflection;
using Kazo.Os.Platform.Blazor.LazyLoading;

namespace Kazo.Os.Platform.Blazor.Tests;

public sealed class BuildKnownAssemblyLoaderRegistryTests
{
    [Fact]
    public async Task Concurrent_declared_requests_are_coalesced()
    {
        var transport = new RecordingTransport();
        var registry = new BuildKnownAssemblyLoaderRegistry(["Kazo.Os.Platform.Blazor.dll"], transport);
        BuildKnownAssemblyLoadOutcome[] outcomes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => registry.LoadAsync("Kazo.Os.Platform.Blazor.dll")));
        Assert.All(outcomes, item => Assert.Equal(BuildKnownAssemblyLoadStatus.Loaded, item.Status));
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task Undeclared_assembly_is_rejected_without_transport()
    {
        var transport = new RecordingTransport();
        var registry = new BuildKnownAssemblyLoaderRegistry([], transport);
        BuildKnownAssemblyLoadOutcome outcome = await registry.LoadAsync("untrusted.dll");
        Assert.Equal(BuildKnownAssemblyLoadStatus.NotDeclared, outcome.Status);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Cancelled_caller_receives_recoverable_outcome_without_cancelling_shared_load()
    {
        var transport = new BlockingTransport();
        var registry = new BuildKnownAssemblyLoaderRegistry(["Kazo.Os.Platform.Blazor.dll"], transport);
        using var cancelled = new CancellationTokenSource();

        Task<BuildKnownAssemblyLoadOutcome> cancelledRequest = registry.LoadAsync(
            "Kazo.Os.Platform.Blazor.dll", cancelled.Token);
        await transport.Started.Task;
        cancelled.Cancel();

        BuildKnownAssemblyLoadOutcome cancelledOutcome = await cancelledRequest;
        Assert.Equal(BuildKnownAssemblyLoadStatus.Cancelled, cancelledOutcome.Status);

        Task<BuildKnownAssemblyLoadOutcome> survivingRequest = registry.LoadAsync("Kazo.Os.Platform.Blazor.dll");
        transport.Complete();
        BuildKnownAssemblyLoadOutcome loadedOutcome = await survivingRequest;
        Assert.Equal(BuildKnownAssemblyLoadStatus.Loaded, loadedOutcome.Status);
        Assert.Equal(1, transport.CallCount);
    }

    private sealed class RecordingTransport : IBuildKnownAssemblyTransport
    {
        public int CallCount { get; private set; }
        public Task<IReadOnlyList<Assembly>> LoadAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<Assembly>>([typeof(BuildKnownAssemblyLoaderRegistry).Assembly]);
        }
    }

    private sealed class BlockingTransport : IBuildKnownAssemblyTransport
    {
        private readonly TaskCompletionSource<IReadOnlyList<Assembly>> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<Assembly>> LoadAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
        {
            CallCount++;
            Started.TrySetResult(true);
            return _completion.Task;
        }

        public void Complete() => _completion.TrySetResult([typeof(BuildKnownAssemblyLoaderRegistry).Assembly]);
    }
}
