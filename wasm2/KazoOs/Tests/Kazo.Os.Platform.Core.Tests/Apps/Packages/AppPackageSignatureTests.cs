using System.Security.Cryptography;
using System.Text.Json;
using Kazo.Os.Platform.Core.Apps.Packages;

namespace Kazo.Os.Platform.Core.Tests.Apps.Packages;

/// <summary>ADR 0214 §2: ECDSA P-256 signatures of packages and the keys of <c>[signing-keys]</c>.</summary>
public sealed class AppPackageSignatureTests
{
    [Fact]
    public async Task The_managed_verifier_accepts_the_right_key_only()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] data = [1, 2, 3, 4];
        byte[] signature = key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        IAppPackageSignatureVerifier verifier = ManagedAppPackageSignatureVerifier.Instance;

        Assert.True(await verifier.VerifyAsync(key.ExportSubjectPublicKeyInfo(), data, signature));
        Assert.False(await verifier.VerifyAsync(other.ExportSubjectPublicKeyInfo(), data, signature));
        Assert.False(await verifier.VerifyAsync(key.ExportSubjectPublicKeyInfo(), [1, 2, 3, 5], signature));
    }

    [Fact]
    public void Only_a_p256_public_key_is_accepted()
    {
        using ECDsa p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.True(AppPackageSignature.TryParsePublicKey(Convert.ToBase64String(p256.ExportSubjectPublicKeyInfo()), out byte[]? parsed));
        Assert.Equal(AppPackageSignature.PublicKeyLength, parsed!.Length);
        Assert.False(AppPackageSignature.TryParsePublicKey(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo()), out _));
        Assert.False(AppPackageSignature.TryParsePublicKey("not base64!", out _));
        Assert.False(AppPackageSignature.TryParsePublicKey(null, out _));
    }

    [Fact]
    public void A_signature_field_of_the_wrong_size_makes_the_index_invalid()
    {
        using JsonDocument good = JsonDocument.Parse($"{{\"signature\":\"{Convert.ToBase64String(new byte[64])}\"}}");
        using JsonDocument none = JsonDocument.Parse("{}");
        using JsonDocument bad = JsonDocument.Parse($"{{\"signature\":\"{Convert.ToBase64String(new byte[10])}\"}}");

        Assert.Equal(64, AppPackageSignature.ReadField(good.RootElement, "1.0.0")!.Length);
        Assert.Null(AppPackageSignature.ReadField(none.RootElement, "1.0.0"));
        Assert.Equal("apps.index.invalid", Assert.Throws<AppPackageException>(() => AppPackageSignature.ReadField(bad.RootElement, "1.0.0")).Code);
    }

    [Fact]
    public void The_os_requires_the_official_catalog_signature_with_its_p256_key()
    {
        KeyValuePair<string, string> official = Assert.Single(AppPackageDefaults.Standard.SigningKeys);

        Assert.Equal("catalog:official", official.Key);
        Assert.Equal(AppPackageDefaults.OfficialCatalogSigningKey, official.Value);
        using ECDsa key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(official.Value), out int read);
        Assert.Equal(Convert.FromBase64String(official.Value).Length, read);
        Assert.Equal(256, key.KeySize);
        Assert.Single(AppSourcesConfigDocument.Read(string.Empty, AppPackageDefaults.Standard)
            .GetSigningKeys(AppPackageSource.ForCatalog("official", "org.kazo.sample")));
    }

    [Fact]
    public void Signing_keys_are_read_per_source_and_built_in_ones_cannot_be_dropped()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        AppPackageDefaults defaults = AppPackageDefaults.FromMetadata(
            new Dictionary<string, string> { ["SigningKeys"] = $"catalog:official={publicKey}" });

        AppSourcesConfig config = AppSourcesConfigDocument.Read(
            $"[signing-keys]\ngithub.com/someone={publicKey}\ngithub.com/broken=AAAA\n", defaults);

        // The official catalog's built-in key and the one the host adds.
        Assert.Equal(2, config.GetSigningKeys(AppPackageSource.ForCatalog("official", "org.kazo.sample")).Count);
        Assert.Single(config.GetSigningKeys(new AppPackageSource("someone", "app")));
        Assert.Empty(config.GetSigningKeys(new AppPackageSource("Kazo-ca", "app")));
        Assert.NotEmpty(config.Warnings);
    }
}
