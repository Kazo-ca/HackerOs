using HackerOs.Platform.Blazor.LazyLoading;

namespace HackerOs.Platform.Blazor.Tests;

/// <summary>ADR 0223: the restart activates the downloaded service worker instead of reloading under the old one.</summary>
public sealed class HostUpdateInstallerTests
{
    [Fact]
    public async Task A_ready_download_is_kept_and_activated_once_the_restart_asks()
    {
        var worker = new FakeWorker(HostUpdateDownloadResult.Ready);
        var installer = new HostUpdateInstaller(worker);

        Assert.Equal(HostUpdateDownloadResult.Ready, await installer.DownloadAsync());
        Assert.Equal(HostUpdateDownloadResult.Ready, await installer.DownloadAsync());
        Assert.True(await installer.ActivateDownloadedAsync());

        Assert.Equal(1, worker.Downloads);
        Assert.Equal(1, worker.Activations);
    }

    [Theory]
    [InlineData(HostUpdateDownloadResult.NoWorker)]
    [InlineData(HostUpdateDownloadResult.UpToDate)]
    [InlineData(HostUpdateDownloadResult.Unavailable)]
    public async Task Without_a_downloaded_version_nothing_is_activated_and_the_next_call_checks_again(HostUpdateDownloadResult result)
    {
        var worker = new FakeWorker(result);
        var installer = new HostUpdateInstaller(worker);

        Assert.False(await installer.ActivateDownloadedAsync());
        Assert.Equal(result, await installer.DownloadAsync());

        Assert.Equal(2, worker.Downloads);
        Assert.Equal(0, worker.Activations);
    }

    [Fact]
    public async Task Calls_made_during_a_download_share_it()
    {
        var worker = new FakeWorker(HostUpdateDownloadResult.Ready) { Gate = new TaskCompletionSource() };
        var installer = new HostUpdateInstaller(worker);

        Task<HostUpdateDownloadResult> first = installer.DownloadAsync();
        Task<HostUpdateDownloadResult> second = installer.DownloadAsync();
        worker.Gate.SetResult();

        Assert.Equal(HostUpdateDownloadResult.Ready, await first);
        Assert.Equal(HostUpdateDownloadResult.Ready, await second);
        Assert.Equal(1, worker.Downloads);
    }

    [Fact]
    public async Task A_worker_that_throws_is_an_unavailable_download_and_no_activation()
    {
        var worker = new FakeWorker(HostUpdateDownloadResult.Ready) { ThrowOnDownload = true };
        var installer = new HostUpdateInstaller(worker);

        Assert.Equal(HostUpdateDownloadResult.Unavailable, await installer.DownloadAsync());

        worker.ThrowOnDownload = false;
        worker.ThrowOnActivate = true;
        Assert.False(await installer.ActivateDownloadedAsync());
    }

    [Fact]
    public async Task An_activation_by_another_tab_is_raised()
    {
        var worker = new FakeWorker(HostUpdateDownloadResult.UpToDate);
        var installer = new HostUpdateInstaller(worker);
        int raised = 0;
        installer.ActivatedElsewhere += () => raised++;

        await installer.StartAsync();
        worker.Listener!();

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task A_listener_that_cannot_register_does_not_stop_the_boot()
    {
        var installer = new HostUpdateInstaller(new FakeWorker(HostUpdateDownloadResult.UpToDate) { ThrowOnListen = true });

        await installer.StartAsync();
    }

    private sealed class FakeWorker(HostUpdateDownloadResult result) : IHostUpdateWorker
    {
        public int Downloads { get; private set; }
        public int Activations { get; private set; }
        public TaskCompletionSource? Gate { get; init; }
        public bool ThrowOnDownload { get; set; }
        public bool ThrowOnActivate { get; set; }
        public bool ThrowOnListen { get; init; }
        public Action? Listener { get; private set; }

        public async ValueTask<HostUpdateDownloadResult> DownloadAsync(CancellationToken cancellationToken)
        {
            Downloads++;
            if (Gate is not null)
            {
                await Gate.Task;
            }

            return ThrowOnDownload ? throw new InvalidOperationException("JS failed") : result;
        }

        public ValueTask<bool> ActivateAsync(CancellationToken cancellationToken)
        {
            Activations++;
            return ThrowOnActivate ? throw new InvalidOperationException("JS failed") : ValueTask.FromResult(true);
        }

        public ValueTask ListenAsync(Action activatedElsewhere, CancellationToken cancellationToken)
        {
            if (ThrowOnListen)
            {
                throw new InvalidOperationException("JS failed");
            }

            Listener = activatedElsewhere;
            return ValueTask.CompletedTask;
        }
    }
}
