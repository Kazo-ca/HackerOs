namespace HackerOs.Tools.ChangeList;

/// <summary>
/// Entry point of the change list tool (ADR 0186). Commands:
/// <list type="bullet">
/// <item><c>site --changes &lt;dir&gt; --version &lt;v&gt; --output &lt;file&gt; [--repository &lt;url&gt;]</c>:
/// writes the <c>whats-new.json</c> the OS reads (run by the <c>HackerOs.Ecosystem</c> build).</item>
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
            """);
}
