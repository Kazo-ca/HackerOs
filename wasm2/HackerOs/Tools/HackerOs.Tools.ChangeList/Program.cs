namespace HackerOs.Tools.ChangeList;

/// <summary>
/// Entry point of the change list tool (ADR 0186). Commands:
/// <list type="bullet">
/// <item><c>site --changes &lt;dir&gt; --version &lt;v&gt; --output &lt;file&gt; [--repository &lt;url&gt;]</c>:
/// writes the <c>whats-new.json</c> the OS reads (run by the <c>HackerOs.Ecosystem</c> build).</item>
/// <item><c>collect --changes &lt;dir&gt; --version &lt;v&gt; --date &lt;yyyy-MM-dd&gt;</c>: moves
/// <c>unreleased/</c> into <c>&lt;version&gt;.json</c> (run by <c>build/tools/collect-changes.sh</c>).</item>
/// <item><c>notes --changes &lt;dir&gt; --version &lt;v&gt; --kind release|package --output &lt;file&gt; [--repository &lt;url&gt;]</c>:
/// writes the GitHub Release text (<c>release.yml</c>) or the <c>PackageReleaseNotes</c> (<c>build/pack-local.sh</c>).</item>
/// <item><c>changelog --changes &lt;dir&gt; --output-directory &lt;dir&gt;</c>: writes <c>CHANGELOG.md</c> and <c>CHANGELOG-fr.md</c>.</item>
/// </list>
/// Errors are printed in MSBuild's canonical format and the exit code is non-zero.
/// </summary>
public static class Program
{
    /// <summary>Runs one command.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>0 on success, 1 on any error.</returns>
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            IReadOnlyDictionary<string, string> options = ParseOptions(args.AsSpan(1));
            return args[0] switch
            {
                "site" => Site(options),
                "collect" => Collect(options),
                "notes" => Notes(options),
                "changelog" => Changelog(options),
                _ => Fail($"Unknown command: {args[0]}")
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException)
        {
            return Fail(exception.Message);
        }
    }

    private static int Site(IReadOnlyDictionary<string, string> options)
    {
        ChangeFolder folder = ChangeFolder.Load(Required(options, "changes"));
        string repository = options.TryGetValue("repository", out string? url) ? url : WhatsNewDocument.DefaultRepositoryUrl;
        WhatsNewDocument.Write(
            WhatsNewDocument.Build(folder, Required(options, "version"), repository),
            Required(options, "output"));
        return 0;
    }

    private static int Collect(IReadOnlyDictionary<string, string> options)
    {
        string output = ChangeCollector.Collect(Required(options, "changes"), Required(options, "version"), Required(options, "date"));
        Console.WriteLine($"Collected the next version into {output}");
        return 0;
    }

    private static int Notes(IReadOnlyDictionary<string, string> options)
    {
        ChangeFolder folder = ChangeFolder.Load(Required(options, "changes"));
        string version = Required(options, "version");
        string repository = options.TryGetValue("repository", out string? url) ? url : WhatsNewDocument.DefaultRepositoryUrl;
        string text = Required(options, "kind") switch
        {
            "release" => ReleaseNotes.Release(folder, version, repository),
            "package" => ReleaseNotes.Package(folder, version, repository),
            string kind => throw new ArgumentException($"--kind must be release or package, got: {kind}")
        };
        ReleaseNotes.Write(text, Required(options, "output"));
        return 0;
    }

    private static int Changelog(IReadOnlyDictionary<string, string> options)
    {
        ChangeFolder folder = ChangeFolder.Load(Required(options, "changes"));
        string directory = Required(options, "output-directory");
        ReleaseNotes.Write(ReleaseNotes.Changelog(folder, "en"), Path.Combine(directory, ReleaseNotes.EnglishChangelog));
        ReleaseNotes.Write(ReleaseNotes.Changelog(folder, "fr"), Path.Combine(directory, ReleaseNotes.FrenchChangelog));
        return 0;
    }

    internal static IReadOnlyDictionary<string, string> ParseOptions(ReadOnlySpan<string> args)
    {
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new ArgumentException($"Expected --name value, got: {args[i]}");
            }

            options[args[i][2..]] = args[++i];
        }

        return options;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"--{name} is required.");

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"ChangeList : error HOSCHG0001: {message}");
        return 1;
    }

    private static void PrintUsage() =>
        Console.WriteLine("""
            HackerOS change list tool (ADR 0186)
              site --changes <dir> --version <v> --output <file> [--repository <url>]
              collect --changes <dir> --version <v> --date <yyyy-MM-dd>
              notes --changes <dir> --version <v> --kind release|package --output <file> [--repository <url>]
              changelog --changes <dir> --output-directory <dir>
            """);
}
