using System.Security.Cryptography;
using HackerOs.Platform.Core.Apps.Packages;

namespace HackerOs.Tools.ManifestGenerator.Tests;

/// <summary>The app authors' signing tool (ADR 0219): <c>keygen</c>, <c>sign</c> and the format the OS checks (ADR 0214 §2).</summary>
[Collection(nameof(SigningKeyEnvironment))]
public sealed class PackageSignerTests
{
    private static readonly byte[] Package = "PK fake package bytes"u8.ToArray();

    [Fact]
    public void A_signature_verifies_with_its_public_key_only_and_only_for_the_same_bytes()
    {
        (string privateKey, string publicKey) = PackageSigner.CreateKeyPair();
        (_, string otherKey) = PackageSigner.CreateKeyPair();

        byte[] signature = PackageSigner.Sign(Package, privateKey);

        Assert.Equal(AppPackageSignature.SignatureLength, signature.Length);
        Assert.Equal(publicKey, PackageSigner.GetPublicKey(privateKey));
        Assert.True(AppPackageSignature.TryParsePublicKey(publicKey, out _));
        Assert.True(PackageSigner.Verify(Package, signature, publicKey));
        Assert.False(PackageSigner.Verify(Package, signature, otherKey));
        Assert.False(PackageSigner.Verify([.. Package, 0], signature, publicKey));
    }

    [Fact]
    public async Task A_signature_is_accepted_by_the_os_verifier()
    {
        (string privateKey, string publicKey) = PackageSigner.CreateKeyPair();
        Assert.True(AppPackageSignature.TryParsePublicKey(publicKey, out byte[]? der));

        bool valid = await ManagedAppPackageSignatureVerifier.Instance.VerifyAsync(der, Package, PackageSigner.Sign(Package, privateKey));

        Assert.True(valid);
    }

    [Fact]
    public void A_key_that_is_not_p256_is_refused()
    {
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.Throws<ArgumentException>(() => PackageSigner.Sign(Package, p384.ExportPkcs8PrivateKeyPem()));
        Assert.Throws<ArgumentException>(() => PackageSigner.Sign(Package, "not a pem"));
    }

    [Fact]
    public void Keygen_writes_a_new_private_file_prints_the_signing_keys_line_and_never_replaces_a_key()
    {
        string folder = Directory.CreateTempSubdirectory("hackeros-keygen-").FullName;
        string path = Path.Combine(folder, "author.pem");
        try
        {
            (int first, string output) = Run("keygen", "--private-key", path);
            string written = File.ReadAllText(path);
            (int second, _) = Run("keygen", "--private-key", path);

            Assert.Equal(0, first);
            Assert.Contains("BEGIN PRIVATE KEY", written, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE KEY", output, StringComparison.Ordinal);
            Assert.Contains($"github.com/<owner>={PackageSigner.GetPublicKey(written)}", output, StringComparison.Ordinal);
            Assert.Equal(1, second);
            Assert.Equal(written, File.ReadAllText(path));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Sign_reads_the_key_from_a_file_or_the_environment_and_fails_without_one()
    {
        string folder = Directory.CreateTempSubdirectory("hackeros-sign-").FullName;
        string package = Path.Combine(folder, "app.hackapp");
        string key = Path.Combine(folder, "author.pem");
        File.WriteAllBytes(package, Package);
        Run("keygen", "--private-key", key);
        string publicKey = PackageSigner.GetPublicKey(File.ReadAllText(key));
        try
        {
            (int fromFile, string fileOutput) = Run("sign", "--package", package, "--private-key", key);
            (int withoutKey, _) = Run("sign", "--package", package);
            Environment.SetEnvironmentVariable(PackageSigner.KeyVariable, File.ReadAllText(key));
            (int fromVariable, string variableOutput) = Run("sign", "--package", package);

            Assert.Equal(0, fromFile);
            Assert.True(PackageSigner.Verify(Package, Convert.FromBase64String(fileOutput.Trim()), publicKey));
            Assert.Equal(1, withoutKey);
            Assert.Equal(0, fromVariable);
            Assert.True(PackageSigner.Verify(Package, Convert.FromBase64String(variableOutput.Trim()), publicKey));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PackageSigner.KeyVariable, null);
            Directory.Delete(folder, recursive: true);
        }
    }

    private static (int ExitCode, string Output) Run(params string[] args)
    {
        TextWriter previous = Console.Out;
        using StringWriter output = new();
        Console.SetOut(output);
        try
        {
            return (Program.Main(args), output.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }
}

/// <summary>Tests that read or set <see cref="PackageSigner.KeyVariable"/> or the console run one at a time.</summary>
[CollectionDefinition(nameof(SigningKeyEnvironment), DisableParallelization = true)]
public sealed class SigningKeyEnvironment;
