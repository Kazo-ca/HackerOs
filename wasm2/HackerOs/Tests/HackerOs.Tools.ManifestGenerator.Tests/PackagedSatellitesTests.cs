namespace HackerOs.Tools.ManifestGenerator.Tests;

/// <summary>
/// ADR 0222 §1: <c>pack</c> puts the satellite assemblies of the app's own assemblies in the package,
/// never those of the SDK or of another library.
/// </summary>
public sealed class PackagedSatellitesTests : IDisposable
{
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "hackeros-satellites-" + Guid.NewGuid().ToString("N"));

    public PackagedSatellitesTests()
    {
        Write("Contoso.Notes.dll", [1]);
        Write("fr/Contoso.Notes.resources.dll", [2]);
        Write("de-CH/Contoso.Notes.resources.dll", [3]);
        Write("fr/HackerOs.AppSdk.resources.dll", [4]);
        Write("fr/Contoso.Shared.resources.dll", [5]);
        Write("runtimes/browser/Contoso.Notes.resources.dll", [6]);
        Write("wwwroot_x/Contoso.Notes.resources.dll", [7]);
    }

    public void Dispose() => Directory.Delete(_bin, recursive: true);

    [Fact]
    public void Only_the_satellites_of_the_declared_assemblies_are_packed()
    {
        SortedDictionary<string, byte[]> satellites = PackagedSatellites.Find(_bin, new HashSet<string>(["Contoso.Notes.dll"], StringComparer.Ordinal));

        Assert.Equal(["de-CH/Contoso.Notes.resources.dll", "fr/Contoso.Notes.resources.dll"], satellites.Keys);
        Assert.Equal(new byte[] { 2 }, satellites["fr/Contoso.Notes.resources.dll"]);
    }

    [Fact]
    public void A_missing_build_folder_packs_no_satellite() =>
        Assert.Empty(PackagedSatellites.Find(Path.Combine(_bin, "nowhere"), new HashSet<string>(["Contoso.Notes.dll"], StringComparer.Ordinal)));

    private void Write(string relative, byte[] bytes)
    {
        string path = Path.Combine(_bin, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}
