using System.Reflection;
using System.Text.Json;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.App.Abstractions.Tests.Definition;

/// <summary>
/// ADR 0048 §1: only the host (its <c>ProjectReference</c>, carried into
/// <c>BuiltInApplications.json</c>) can make an app a system app. The app's own surfaces have no way
/// to claim it.
/// </summary>
public sealed class SystemAppTrustTests
{
    private const string ManifestWithSystemClaim = """{"schemaVersion":1,"id":"org.hackeros.tests.a","name":"a","version":"1.0.0","publisherId":"pub.local","description":"a","kind":"window","entryPoint":{"assembly":"A.dll","type":"A.W"},"sdkCompatibility":{"minimumVersion":"1.0.0"},"presentation":{"category":"utilities","launchVisibility":"visible","iconAssetPaths":[]},"resources":{"baselineCpuWeight":0,"burstCpuWeight":0,"baselineMemoryWeight":0,"burstMemoryWeight":0,"baselineStorageIoWeight":0,"burstStorageIoWeight":0,"baselineNetworkIoWeight":0,"burstNetworkIoWeight":0},"isSystemApp":true}""";

    [Theory]
    [InlineData(typeof(AppDefinition))]
    [InlineData(typeof(AppManifest))]
    [InlineData(typeof(AppManifestAttribute))]
    public void App_side_types_have_no_system_flag(Type type) =>
        Assert.DoesNotContain(
            type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static),
            member => member.Name.Contains("SystemApp", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Manifest_json_cannot_claim_system_status() =>
        Assert.ThrowsAny<JsonException>(() => AppManifestJsonSerializer.DeserializeStrict(ManifestWithSystemClaim));
}
