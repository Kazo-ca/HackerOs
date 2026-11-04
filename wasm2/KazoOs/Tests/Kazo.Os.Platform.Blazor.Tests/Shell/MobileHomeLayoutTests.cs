using Kazo.Os.Platform.Blazor.Shell;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Platform.Core.Shell;
using static Kazo.Os.Platform.Blazor.Tests.WindowGeometryPersistenceTests;

namespace Kazo.Os.Platform.Blazor.Tests.Shell;

/// <summary>Checks the on-disk Home contract, bounded parsing and serialized user edits.</summary>
public sealed class MobileHomeLayoutTests
{
    [Fact]
    public void Layout_lives_in_the_desktops_hidden_mobile_directory()
    {
        Assert.Equal("/home/alice/Desktop/.mobile/layout.conf", MobileHomeLayoutStore.File.Resolve("/home/alice").Value);
        Assert.Equal(OsSettingsLocation.UserHome, MobileHomeLayoutStore.File.Location);
        Assert.Throws<InvalidOperationException>(() => MobileHomeLayoutStore.File.Resolve(null));
        Assert.True(OsSettingsDocumentRegistry.TryGet("mobile", out OsSettingsDocument document));
        Assert.Same(MobileSettingsDocuments.Document, document);
    }

    [Fact]
    public void Pages_and_dock_preserve_order_and_unavailable_app_ids()
    {
        MobileHomeLayout layout = MobileHomeLayout.Default();
        layout.Pages.Add(["com.example.uninstalled", "org.kazoos.calculator"]);
        layout.Dock.Reverse();
        MobileHomeLayout restored = MobileHomeLayout.Decode(layout.Encode())!;
        Assert.Equal(layout.Encode(), restored.Encode());
        Assert.Equal("com.example.uninstalled", restored.Pages[1][0]);
        Assert.Equal(layout.Dock, restored.Dock);
    }

    [Theory]
    [InlineData("not-config")]
    [InlineData("version=2\n[dock]\napps=\n[pages]\n1=")]
    [InlineData("version=1\n[dock]\napps=\n[pages]")]
    [InlineData("version=1\n[dock]\napps=\n[pages]\n1=../app")]
    [InlineData("version=1\n[dock]\napps=\n[pages]\n1=org.app\n2=org.app")]
    [InlineData("version=1\n[dock]\napps=org.app,org.app\n[pages]\n1=")]
    [InlineData("version=1\nunknown=true\n[dock]\napps=\n[pages]\n1=")]
    [InlineData("version=1\n[dock]\napps=\n[pages]\n1=\n1=org.app")]
    [InlineData("version=1\n[dock]\napps=\n[pages]\n2=")]
    [InlineData("version=1\n[dock]\napps=\n[pages]\n1=org.app,")]
    public void Invalid_or_duplicate_layout_data_is_rejected(string content) => Assert.Null(MobileHomeLayout.Decode(content));

    [Fact]
    public void Text_format_accepts_comments_whitespace_empty_pages_and_numeric_page_order()
    {
        MobileHomeLayout layout = MobileHomeLayout.Decode("# My launcher\r\nversion = 1\r\n[dock]\r\napps = org.app.b, org.app.a\r\n[pages]\r\n2=\r\n1=org.app.c\r\n")!;
        Assert.Equal("org.app.c", Assert.Single(layout.Pages[0]));
        Assert.Empty(layout.Pages[1]);
        Assert.Equal(new[] { "org.app.b", "org.app.a" }, layout.Dock);
        Assert.Contains("[pages]\n1=org.app.c\n2=\n", layout.Encode());
    }

    [Fact]
    public async Task Legacy_json_is_migrated_once_without_overwriting_or_deleting_the_source()
    {
        const string json = "{\"schemaVersion\":1,\"pages\":[[\"org.app.one\"],[]],\"dock\":[\"org.app.two\"]}";
        FakeOsSettingsService settings = new();
        settings.Files[MobileHomeLayoutStore.LegacyFile.RelativePath] = json;
        MobileHomeLayoutStore store = new(settings);
        await store.InitializeAsync();
        Assert.Equal(1, settings.Writes);
        Assert.Equal(json, settings.Files[MobileHomeLayoutStore.LegacyFile.RelativePath]);
        Assert.Equal(store.Current.Encode(), settings.Files[MobileHomeLayoutStore.File.RelativePath]);
        Assert.Equal("org.app.one", Assert.Single(store.Current.Pages[0]));
        Assert.Empty(store.Current.Pages[1]);
        await new MobileHomeLayoutStore(settings).InitializeAsync();
        Assert.Equal(1, settings.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_existing_destination_wins_over_legacy_data_even_when_invalid(bool corrupt)
    {
        FakeOsSettingsService settings = new();
        settings.Files[MobileHomeLayoutStore.File.RelativePath] = corrupt ? "broken" : MobileHomeLayout.Default().Encode();
        settings.Files[MobileHomeLayoutStore.LegacyFile.RelativePath] = "{\"schemaVersion\":1,\"pages\":[[\"org.app.legacy\"]],\"dock\":[]}";
        MobileHomeLayoutStore store = new(settings);
        await store.InitializeAsync();
        Assert.Equal(MobileHomeLayout.Default().Encode(), store.Current.Encode());
        Assert.Equal(0, settings.Writes);
    }

    [Fact]
    public void Capacity_limits_accept_the_boundary_and_reject_excess_data()
    {
        MobileHomeLayout layout = new();
        for (int page = 0; page < MobileHomeLayout.MaximumPages; page++)
            layout.Pages.Add(Enumerable.Range(0, MobileHomeLayout.PageCapacity).Select(index => $"org.app.p{page}-{index}").ToList());
        layout.Dock.AddRange(Enumerable.Range(0, MobileHomeLayout.DockCapacity).Select(index => $"org.dock.app-{index}"));
        Assert.NotNull(MobileHomeLayout.Decode(layout.Encode()));
        layout.Pages.Add([]);
        Assert.Null(MobileHomeLayout.Decode(layout.Encode()));
        layout.Pages.RemoveAt(layout.Pages.Count - 1);
        layout.Pages[0].Add("org.excess.app");
        Assert.Null(MobileHomeLayout.Decode(layout.Encode()));
        layout.Pages[0].RemoveAt(MobileHomeLayout.PageCapacity);
        layout.Dock.Add("org.excess.dock");
        Assert.Null(MobileHomeLayout.Decode(layout.Encode()));
        Assert.Null(MobileHomeLayout.Decode(new string(' ', 131073)));
    }

    [Fact]
    public async Task Corrupt_files_are_left_untouched_until_an_explicit_edit()
    {
        FakeOsSettingsService settings = new();
        settings.Files[MobileHomeLayoutStore.File.RelativePath] = "broken";
        MobileHomeLayoutStore store = new(settings);
        await store.InitializeAsync();
        Assert.Equal(0, settings.Writes);
        Assert.Single(store.Current.Pages);
        Assert.Equal("broken", settings.Files[MobileHomeLayoutStore.File.RelativePath]);
        await store.EditAsync(layout => { layout.Pages.Add([]); return true; });
        Assert.Equal(1, settings.Writes);
        MobileHomeLayoutStore reloaded = new(settings);
        await reloaded.InitializeAsync();
        Assert.Equal(2, reloaded.Current.Pages.Count);
    }

    [Fact]
    public async Task Concurrent_edits_are_serialized_and_rejected_edits_write_nothing()
    {
        FakeOsSettingsService settings = new();
        MobileHomeLayoutStore store = new(settings);
        await store.InitializeAsync();
        await Task.WhenAll(Enumerable.Range(0, 10).Select(index => store.EditAsync(layout =>
        {
            layout.Pages[0].Add($"org.example.app-{index}");
            return true;
        })));
        Assert.Equal(13, store.Current.Pages[0].Count);
        Assert.Equal(10, settings.Writes);
        Assert.False(await store.EditAsync(_ => false));
        Assert.Equal(10, settings.Writes);
    }

    [Fact]
    public async Task A_failed_or_invalid_edit_keeps_the_last_good_state()
    {
        FakeOsSettingsService settings = new();
        MobileHomeLayoutStore store = new(settings);
        await store.InitializeAsync();
        string before = store.Current.Encode();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.EditAsync(layout =>
        {
            layout.Dock.AddRange(["org.a", "org.b", "org.c"]);
            return true;
        }));
        Assert.Equal(before, store.Current.Encode());
        Assert.Equal(0, settings.Writes);
        MobileHomeLayoutStore anonymous = new(new FakeOsSettingsService { NoSession = true });
        await anonymous.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => anonymous.EditAsync(_ => true));
    }
}
