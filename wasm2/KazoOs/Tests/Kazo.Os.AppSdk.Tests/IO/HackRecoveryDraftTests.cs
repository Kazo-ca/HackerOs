using Kazo.Os.AppSdk.IO;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

/// <summary>ADR 0069: the recovery draft a stopped editor keeps instead of losing unsaved work.</summary>
public sealed class HackRecoveryDraftTests
{
    [Fact]
    public async Task A_draft_is_kept_in_the_apps_private_state_directory_and_taken_back_once()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fileSystem);
        HackRecoveryDraft draft = HackRecoveryDraft.Open("notes");

        await draft.SaveAsync("unsaved line", "/home/alice/notes.txt");
        HackRecoveryDraftContent? taken = await HackRecoveryDraft.Open("notes").TakeAsync();

        Assert.Equal($"{HackTestHost.Home}/.local/state/{HackTestHost.Manifest.Id}/recovery/notes.json", draft.Path);
        Assert.Equal(new HackRecoveryDraftContent("unsaved line", "/home/alice/notes.txt", HackTestHost.Now), taken);
        Assert.False(fileSystem.Exists(draft.Path));
        Assert.Null(await draft.TakeAsync());
    }

    [Fact]
    public async Task A_new_buffer_draft_has_no_original_path_and_an_unreadable_draft_reads_as_none()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fileSystem);
        HackRecoveryDraft draft = HackRecoveryDraft.Open();

        await draft.SaveAsync("text");
        Assert.Null((await draft.TryReadAsync())!.OriginalPath);

        await HackFile.WriteAllTextAsync(draft.Path, "{not json");
        Assert.Null(await draft.TryReadAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("name with space")]
    public void A_draft_name_cannot_leave_the_recovery_directory(string name)
    {
        using IDisposable scope = HackTestHost.Enter(out _);

        Assert.Throws<ArgumentException>(() => HackRecoveryDraft.Open(name));
    }
}
