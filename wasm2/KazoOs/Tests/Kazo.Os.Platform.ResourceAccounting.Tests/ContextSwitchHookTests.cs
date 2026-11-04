using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.ResourceAccounting.Tests;

/// <summary>
/// The common context-switch hook of ADR 0067, section 1, prepared by ADR 0066: installed on
/// <see cref="KazoExecutionContext"/>'s ambient frame, it sees every process enter and leave the
/// thread, including when the runtime restores a context in an async continuation.
/// </summary>
public sealed class ContextSwitchHookTests
{
    [Fact]
    public void Hook_is_not_installed_by_default()
    {
        Assert.False(ContextSwitchHook.IsInstalled);
    }

    [Fact]
    public void Entering_and_leaving_a_scope_are_switches()
    {
        RecordingObserver observer = new();
        using (ContextSwitchHook.Install(observer))
        {
            Assert.True(ContextSwitchHook.IsInstalled);
            using (KazoExecutionContext.Enter(TestContexts.Process(10)))
            {
                Assert.Equal(10, KazoExecutionContext.Current.ProcessId.Value);
            }

            Assert.Equal(
                [
                    new ExecutionContextSwitch(null, ProcessId.FromInt64(10), false),
                    new ExecutionContextSwitch(ProcessId.FromInt64(10), null, false)
                ],
                observer.Switches);
        }

        Assert.False(ContextSwitchHook.IsInstalled);
    }

    [Fact]
    public async Task Async_continuations_are_switches_made_by_the_runtime()
    {
        RecordingObserver observer = new();
        using (ContextSwitchHook.Install(observer))
        {
            long seenAfterAwait = await RunAsProcessAsync(20);

            Assert.Equal(20, seenAfterAwait);
            Assert.True(observer.Entered(20, threadContextChanged: false), "explicit Enter");
            // The runtime restored the process's ExecutionContext to run each continuation...
            Assert.True(observer.Entered(20, threadContextChanged: true), "continuation start");
            // ...and the caller's context when the method yielded or a continuation ended.
            Assert.True(observer.Left(20, threadContextChanged: true), "continuation end");
            Assert.True(observer.Left(20, threadContextChanged: false), "explicit Dispose");
        }
    }

    [Fact]
    public async Task Work_queued_by_a_process_runs_as_that_process()
    {
        RecordingObserver observer = new();
        using (ContextSwitchHook.Install(observer))
        {
            using (KazoExecutionContext.Enter(TestContexts.Process(30)))
            {
                await Task.Run(() => Assert.Equal(30, KazoExecutionContext.Current.ProcessId.Value));
            }

            Assert.True(observer.Entered(30, threadContextChanged: true));
        }
    }

    [Fact]
    public void Nested_processes_switch_in_and_back()
    {
        RecordingObserver observer = new();
        using (ContextSwitchHook.Install(observer))
        {
            using (KazoExecutionContext.Enter(TestContexts.Process(40)))
            {
                using (KazoExecutionContext.Enter(TestContexts.Process(41)))
                {
                }
            }

            Assert.Equal(
                [
                    new ExecutionContextSwitch(null, ProcessId.FromInt64(40), false),
                    new ExecutionContextSwitch(ProcessId.FromInt64(40), ProcessId.FromInt64(41), false),
                    new ExecutionContextSwitch(ProcessId.FromInt64(41), ProcessId.FromInt64(40), false),
                    new ExecutionContextSwitch(ProcessId.FromInt64(40), null, false)
                ],
                observer.Switches);
        }
    }

    [Fact]
    public void A_nested_frame_of_the_same_process_is_not_a_switch()
    {
        RecordingObserver observer = new();
        using (ContextSwitchHook.Install(observer))
        {
            using (KazoExecutionContext.Enter(TestContexts.Process(50)))
            {
                using (KazoExecutionContext.Enter(TestContexts.Process(50), "/tmp"))
                {
                }
            }

            Assert.Equal(2, observer.Switches.Count);
        }
    }

    [Fact]
    public void Installing_twice_is_refused()
    {
        using (ContextSwitchHook.Install(new RecordingObserver()))
        {
            Assert.Throws<InvalidOperationException>(() => ContextSwitchHook.Install(new RecordingObserver()));
        }
    }

    [Fact]
    public void Installing_while_a_scope_is_open_is_refused()
    {
        // Swapping the AsyncLocal now would make this flow lose its app context.
        using (KazoExecutionContext.Enter(TestContexts.Process(60)))
        {
            Assert.Throws<InvalidOperationException>(() => ContextSwitchHook.Install(new RecordingObserver()));
            Assert.Equal(60, KazoExecutionContext.Current.ProcessId.Value);
        }

        Assert.False(ContextSwitchHook.IsInstalled);
    }

    [Fact]
    public void A_throwing_observer_never_breaks_the_app_context()
    {
        using (ContextSwitchHook.Install(new ThrowingObserver()))
        {
            using (KazoExecutionContext.Enter(TestContexts.Process(70)))
            {
                Assert.Equal(70, KazoExecutionContext.Current.ProcessId.Value);
            }

            Assert.Null(KazoExecutionContext.CurrentOrNull);
        }
    }

    [Fact]
    public void After_removal_no_switch_is_reported_and_scopes_still_work()
    {
        RecordingObserver observer = new();
        ContextSwitchHook.Install(observer).Dispose();

        using (KazoExecutionContext.Enter(TestContexts.Process(80)))
        {
            Assert.Equal(80, KazoExecutionContext.Current.ProcessId.Value);
        }

        Assert.Empty(observer.Switches);
        Assert.False(ContextSwitchHook.IsInstalled);
    }

    private static async Task<long> RunAsProcessAsync(long pid)
    {
        using (KazoExecutionContext.Enter(TestContexts.Process(pid)))
        {
            await Task.Yield();
            await Task.Delay(1);
            return KazoExecutionContext.Current.ProcessId.Value;
        }
    }

    private sealed class ThrowingObserver : IExecutionContextSwitchObserver
    {
        public void OnContextSwitch(in ExecutionContextSwitch contextSwitch) =>
            throw new InvalidOperationException("observer failure");
    }
}
