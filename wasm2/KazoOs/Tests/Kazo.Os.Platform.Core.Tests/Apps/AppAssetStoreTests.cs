using System.Security.Cryptography;
using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Apps;

namespace Kazo.Os.Platform.Core.Tests.Apps;

/// <summary>ADR 0098 §5: an asset is checked against its manifest hash at its first read.</summary>
public sealed class AppAssetStoreTests
{
    private static readonly byte[] Icon = Encoding.UTF8.GetBytes("<svg/>");

    [Fact]
    public async Task A_matching_asset_is_returned_and_hashed_once()
    {
        CountingSource source = new() { Bytes = Icon };
        AppAssetStore store = new(Catalog(Hash(Icon)), source);

        Assert.Equal(Icon, await store.ReadAsync("org.hackeros.tests.icons", "icons/app.svg"));
        Assert.Equal(Icon, await store.ReadAsync("org.hackeros.tests.icons", "icons/app.svg"));

        Assert.False(store.IsDamaged("org.hackeros.tests.icons"));
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task An_altered_asset_is_refused_and_marks_the_app_damaged()
    {
        AppAssetStore store = new(Catalog(Hash(Icon)), new CountingSource { Bytes = Encoding.UTF8.GetBytes("<svg onload=x/>") });
        int changes = 0;
        store.Changed += (_, _) => changes++;

        AppAssetIntegrityException first = await Assert.ThrowsAsync<AppAssetIntegrityException>(() => store.ReadAsync("org.hackeros.tests.icons", "icons/app.svg").AsTask());
        await Assert.ThrowsAsync<AppAssetIntegrityException>(() => store.ReadAsync("org.hackeros.tests.icons", "icons/app.svg").AsTask());

        Assert.True(store.IsDamaged("org.hackeros.tests.icons"));
        Assert.Equal("icons/app.svg", first.Problem.AssetPath);
        Assert.Contains(Hash(Icon), first.Problem.Reason);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task An_undeclared_or_missing_asset_is_not_damage()
    {
        AppAssetStore store = new(Catalog(Hash(Icon)), NoAppAssetSource.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync("org.hackeros.tests.icons", "icons/other.svg").AsTask());
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.ReadAsync("org.hackeros.tests.icons", "icons/app.svg").AsTask());
        Assert.False(store.IsDamaged("org.hackeros.tests.icons"));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static AppCatalog Catalog(string sha256) => AppCatalog.Build(
    [
        new AppManifest
        {
            Id = "org.hackeros.tests.icons",
            Name = "Icons",
            Version = "1.0.0",
            PublisherId = "pub.local",
            Description = "Icons",
            Kind = AppKind.Window,
            EntryPoint = new AppEntryPointManifest("Tests.Icons.dll", "Tests.Icons.Window"),
            SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
            Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Visible, ["icons/app.svg"]),
            Resources = AppResourceProfileManifest.None,
            Assets = [new AssetManifest("icons/app.svg", AssetKind.Image, sha256, "icon-32")]
        }
    ]).Catalog!;

    private sealed class CountingSource : IAppAssetSource
    {
        public byte[]? Bytes { get; init; }

        public int Reads { get; private set; }

        public ValueTask<byte[]?> ReadAsync(AppManifest manifest, string assetPath, CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult(Bytes);
        }
    }
}
