using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HackerOs.Tools.ChangeList;

/// <summary>
/// Moves the entries of the next version into its version file when the version is published
/// (ADR 0186 §1): every <c>unreleased/&lt;id&gt;.json</c> goes, unchanged and in display order, into
/// <c>changes/&lt;version&gt;.json</c> with the release date, then the <c>unreleased/</c> files are deleted.
/// Run by <c>build/tools/collect-changes.sh</c> in the pull request that changes <c>Versions.props</c>.
/// </summary>
internal static class ChangeCollector
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        // Same escaping as the entries written by hand: accented French text stays readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Collects the next version.</summary>
    /// <param name="changesPath">The <c>changes/</c> folder.</param>
    /// <param name="version">The version being published (three parts).</param>
    /// <param name="date">The release date, <c>yyyy-MM-dd</c>.</param>
    /// <returns>The path of the version file written.</returns>
    /// <exception cref="ArgumentException">The version or the date is invalid, the version file already exists or there is nothing to collect.</exception>
    public static string Collect(string changesPath, string version, string date)
    {
        if (!Version.TryParse(version, out Version? parsed) || parsed.Build < 0 || parsed.Revision >= 0)
        {
            throw new ArgumentException($"The version must have three parts (x.y.z): {version}");
        }

        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out _))
        {
            throw new ArgumentException($"The date must be yyyy-MM-dd: {date}");
        }

        ChangeFolder folder = ChangeFolder.Load(changesPath);
        string output = Path.Combine(folder.Path, version + ".json");
        if (File.Exists(output))
        {
            throw new ArgumentException($"{output} already exists: version {version} is already collected.");
        }

        if (folder.Unreleased.Count == 0)
        {
            throw new ArgumentException($"{Path.Combine(folder.Path, "unreleased")} holds no entry: nothing to collect.");
        }

        JsonObject document = new()
        {
            ["version"] = version,
            ["date"] = date,
            ["entries"] = new JsonArray([.. folder.Unreleased.Select(entry => entry.DeepClone())])
        };
        File.WriteAllText(output, document.ToJsonString(Indented) + "\n");

        foreach (string file in Directory.GetFiles(Path.Combine(folder.Path, "unreleased"), "*.json"))
        {
            File.Delete(file);
        }

        return output;
    }
}
