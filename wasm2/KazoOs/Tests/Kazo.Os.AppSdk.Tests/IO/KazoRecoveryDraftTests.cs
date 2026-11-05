using Kazo.Os.AppSdk.IO;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

/// <summary>ADR 0069: the recovery draft a stopped editor keeps instead of losing unsaved work.</summary>
public sealed class KazoRecoveryDraftTests
{
    [Fact]
    public async Task A_draft_is_kept_in_the_apps_private_state_directory_and_taken_back_once()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fileSystem);
        KazoRecoveryDraft draft = KazoRecoveryDraft.Open("notes");

        await draft.SaveAsync("unsaved line", "/home/alice/notes.txt");
        KazoRecoveryDraftContent? taken = await KazoRecoveryDraft.Open("notes").TakeAsync();

        Assert.Equal($"{KazoTestHost.Home}/.local/state/{KazoTestHost.Manifest.Id}/recovery/notes.json", draft.Path);
        Assert.Equal(new KazoRecoveryDraftContent("unsaved line", "/home/alice/notes.txt", KazoTestHost.Now), taken);
        Assert.False(fileSystem.Exists(draft.Path));
        Assert.Null(await draft.TakeAsync());
    }

    [Fact]
    public async Task A_new_buffer_draft_has_no_original_path_and_an_unreadable_draft_reads_as_none()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fileSystem);
        KazoRecoveryDraft draft = KazoRecoveryDraft.Open();

        await draft.SaveAsync("text");
        Assert.Null((await draft.TryReadAsync())!.OriginalPath);

        await KazoFile.WriteAllTextAsync(draft.Path, "{not json");
        Assert.Null(await draft.TryReadAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("name with space")]
    public void A_draft_name_cannot_leave_the_recovery_directory(string name)
    {
        using IDisposable scope = KazoTestHost.Enter(out _);

        Assert.Throws<ArgumentException>(() => KazoRecoveryDraft.Open(name));
    }
}
