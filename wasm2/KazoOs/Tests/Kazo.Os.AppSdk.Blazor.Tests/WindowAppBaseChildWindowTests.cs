using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Blazor.Tests;

/// <summary>ADR 0234 §3: <c>ShowWindowAsync</c> and <c>ShowDialogAsync</c>, as WinForms <c>Show</c> and <c>ShowDialog</c>.</summary>
public sealed partial class WindowAppBaseWindowTitleTests
{
    [Fact]
    public async Task ShowDialogAsync_forces_modal_and_returns_the_dialog_result()
    {
        ChildGateway gateway = new();
        TitleApp app = await RenderWithAsync(gateway);

        Task<KazoDialogResult> pending = app.DialogAsync<NoteWindow>(new KazoWindowRequest { Title = "Note" }).AsTask();
        ChildHandle dialog = Assert.Single(gateway.Opened);
        Assert.True(dialog.Request.Options!.Modal);
        Assert.Equal(typeof(NoteWindow), dialog.ComponentType);
        Assert.False(pending.IsCompleted);

        await dialog.CloseAsync(KazoDialogResult.Ok("done"));

        Assert.Equal("done", (await pending).ValueAs<string>());
    }

    [Fact]
    public async Task ShowWindowAsync_keeps_the_request_as_given()
    {
        ChildGateway gateway = new();
        TitleApp app = await RenderWithAsync(gateway);

        IKazoWindow window = await app.ShowAsync<NoteWindow>(new KazoWindowRequest());

        Assert.Same(Assert.Single(gateway.Opened), window);
        Assert.Null(gateway.Opened[0].Request.Options);
    }

    [Fact]
    public async Task Cancelling_the_wait_closes_the_dialog()
    {
        ChildGateway gateway = new();
        TitleApp app = await RenderWithAsync(gateway);
        using CancellationTokenSource cancellation = new();

        Task<KazoDialogResult> pending = app.DialogAsync<NoteWindow>(new KazoWindowRequest(), cancellation.Token).AsTask();
        await cancellation.CancelAsync();

        Assert.Equal(KazoDialogResultKind.Cancel, (await pending).Kind);
        Assert.False(gateway.Opened[0].IsOpen);
    }

    [Fact]
    public async Task Without_a_window_manager_child_windows_are_refused()
    {
        TitleApp app = new() { InitialTitle = null };
        app.Bind(new MinimalAppExecutionContext(Manifest()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => app.ShowAsync<NoteWindow>(new KazoWindowRequest()).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.DialogAsync<NoteWindow>(new KazoWindowRequest()).AsTask());
    }

    [Fact]
    public void Dialog_results_read_like_WinForms()
    {
        Assert.True(KazoDialogResult.Ok().IsAccepted);
        Assert.True(new KazoDialogResult(KazoDialogResultKind.Yes).IsAccepted);
        Assert.False(KazoDialogResult.Cancel.IsAccepted);
        Assert.Equal(0, KazoDialogResult.Ok("text").ValueAs<int>());
        Assert.Equal(KazoDialogResultKind.None, KazoDialogResult.None.Kind);
    }

    private static async Task<TitleApp> RenderWithAsync(IAppWindowGateway gateway)
    {
        using TitleRenderer renderer = new();
        TitleHost host = new() { Gateway = gateway, Context = new MinimalAppExecutionContext(Manifest()) };
        await renderer.RenderAsync(host);
        return host.App!;
    }

    /// <summary>A child window component.</summary>
    private sealed class NoteWindow : KazoWindowBase;

    /// <summary>A platform gateway that opens recorded child windows.</summary>
    private sealed class ChildGateway : IAppWindowGateway, IAppChildWindowGateway
    {
        public List<ChildHandle> Opened { get; } = [];

        public string Title => "Title App";

        public string DefaultTitle => "Title App";

        public ValueTask<string> SetTitleAsync(string? title, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(title ?? DefaultTitle);

        public ValueTask<IKazoWindow?> OpenWindowAsync(Type componentType, KazoWindowRequest request, CancellationToken cancellationToken = default)
        {
            ChildHandle handle = new(componentType, request);
            Opened.Add(handle);
            return ValueTask.FromResult<IKazoWindow?>(handle);
        }
    }

    private sealed class ChildHandle(Type componentType, KazoWindowRequest request) : IKazoWindow
    {
        private readonly TaskCompletionSource<KazoDialogResult> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Type ComponentType { get; } = componentType;

        public KazoWindowRequest Request { get; } = request;

        public string Id { get; } = Guid.NewGuid().ToString();

        public bool IsOpen => !_closed.Task.IsCompleted;

        public Task<KazoDialogResult> Closed => _closed.Task;

        public ValueTask<bool> CloseAsync(KazoDialogResult? result = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_closed.TrySetResult(result ?? KazoDialogResult.Cancel));
    }
}
