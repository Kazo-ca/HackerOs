using System.Text.Json;
using HackerOs.AppSdk.Blazor;
using HackerOs.Platform.RemoteRendering.Worker;

namespace HackerOs.Platform.RemoteRendering.Tests;

/// <summary>ADR 0133: the SDK's dialogs of a window in a worker are opened by its host on the page.</summary>
public sealed class RemoteDialogServiceTests
{
    [Fact]
    public async Task A_message_box_is_asked_of_the_page_and_returns_its_answer()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<CounterWindow>();
        RemoteDialogService dialogs = new(window.Run);

        ValueTask<MessageBoxDialogResult> pending = dialogs.MessageBoxAsync(
            window.Run.Context, new MessageBoxDialogRequest { Title = "Title", Content = "Body", DialogType = MessageBoxType.YesNo });

        JsonElement request = Assert.Single(window.Outputs);
        Assert.Equal("dlg", request.GetProperty("k").GetString());
        Assert.Equal("msg", request.GetProperty("ty").GetString());
        Assert.Equal("Body", request.GetProperty("content").GetString());
        Assert.False(pending.IsCompleted);

        int id = request.GetProperty("id").GetInt32();
        dialogs.Complete(JsonDocument.Parse($$"""{"k":"dlgr","id":{{id}},"r":{{(int)MessageBoxResult.Yes}}}""").RootElement);

        Assert.Equal(MessageBoxResult.Yes, (await pending).Result);
    }

    [Fact]
    public async Task A_message_box_carries_its_icon_to_the_page()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<CounterWindow>();
        RemoteDialogService dialogs = new(window.Run);

        _ = dialogs.MessageBoxAsync(
            window.Run.Context, new MessageBoxDialogRequest { Title = "Title", Content = "Body", Icon = MessageBoxIcon.Error });

        JsonElement request = Assert.Single(window.Outputs);
        Assert.Equal((int)MessageBoxIcon.Error, request.GetProperty("icon").GetInt32());
    }

    [Fact]
    public async Task A_text_input_returns_the_typed_text()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<CounterWindow>();
        RemoteDialogService dialogs = new(window.Run);

        ValueTask<TextInputDialogResult> pending = dialogs.TextInputAsync(
            window.Run.Context, new TextInputDialogRequest { Title = "Rename", Content = "New name", DefaultValue = "old" });
        JsonElement request = Assert.Single(window.Outputs);
        Assert.Equal("old", request.GetProperty("default").GetString());

        dialogs.Complete(JsonDocument.Parse($$"""{"k":"dlgr","id":{{request.GetProperty("id").GetInt32()}},"r":{{(int)TextInputStatus.Submitted}},"v":"new"}""").RootElement);

        TextInputDialogResult result = await pending;
        Assert.Equal(TextInputStatus.Submitted, result.Status);
        Assert.Equal("new", result.Value);
    }

    [Fact]
    public async Task Closing_the_window_cancels_open_dialogs()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<CounterWindow>();
        RemoteDialogService dialogs = new(window.Run);
        ValueTask<MessageBoxDialogResult> pending = dialogs.MessageBoxAsync(
            window.Run.Context, new MessageBoxDialogRequest { Title = "Title", Content = "Body" });

        dialogs.CancelAll();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    [Fact]
    public async Task Credentials_are_never_asked_and_file_dialogs_are_refused()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<CounterWindow>();
        RemoteDialogService dialogs = new(window.Run);

        CredentialPromptDialogResult credentials = await dialogs.CredentialPromptAsync(
            window.Run.Context, new CredentialPromptDialogRequest { Title = "Sudo", Description = "Password" });

        Assert.Equal(CredentialPromptStatus.Cancelled, credentials.Status);
        Assert.Throws<NotSupportedException>(() => dialogs.OpenFileAsync(window.Run.Context, new OpenFileDialogRequest()));
        Assert.Empty(window.Outputs);
    }
}
