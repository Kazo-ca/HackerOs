using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Kazo.Os.Ecosystem.Tests;

/// <summary>
/// Source-level checks of the SDK boundary (ADR 0055 §3 and §6) that the build cannot express:
/// <list type="bullet">
/// <item>an app only calls <c>IJSRuntime</c> to <c>import</c> its own module
/// (<c>./_content/&lt;app project&gt;/…</c>); calls on the imported <c>IJSObjectReference</c> are free;</item>
/// <item>the boundary has no way out: the temporary exemption list of ADR 0055 §6 is gone and no
/// project opts out of being an app or an SDK library.</item>
/// </list>
/// The check is textual on purpose: it needs no compilation and runs on every <c>.razor</c> and
/// <c>.cs</c> file under <c>Apps/</c>.
/// </summary>
public sealed partial class SdkBoundarySourceTests
{
    /// <summary>
    /// Build properties that would take a project out of the boundary. The targets file derives them
    /// from the project's location; a project setting one itself is a hidden exemption.
    /// </summary>
    private static readonly string[] BoundaryRoleProperties = ["KazoOsAppProject", "KazoOsSdkLibrary"];

    [Fact]
    public void Build_targets_have_no_exemption_mechanism()
    {
        string targets = File.ReadAllText(Path.Combine(FindSolutionDirectory(), "build", "Kazo.Os.Apps.targets"));

        // ADR 0055 §6: the list and its plumbing were removed by 4-sdk-boundary-final; any of these
        // names coming back means a violation can be downgraded to a warning again.
        Assert.DoesNotContain("KazoOsSdkBoundaryExemption", targets, StringComparison.Ordinal);
        Assert.DoesNotContain("_KazoOsSdkBoundaryExempt", targets, StringComparison.Ordinal);
        Assert.DoesNotContain("boundary-exempt", targets, StringComparison.Ordinal);
        Assert.DoesNotContain("<Warning", targets, StringComparison.Ordinal);
    }

    [Fact]
    public void No_app_or_sdk_project_overrides_its_boundary_role()
    {
        string root = FindSolutionDirectory();
        List<string> overrides = [];

        foreach (string projectFile in Directory.EnumerateFiles(Path.Combine(root, "Apps"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Shared"), "Kazo.Os.AppSdk*.csproj", SearchOption.AllDirectories)))
        {
            XDocument project = XDocument.Load(projectFile);
            foreach (string property in BoundaryRoleProperties)
            {
                if (project.Descendants().Any(element => element.Name.LocalName == property))
                {
                    overrides.Add($"{Path.GetRelativePath(root, projectFile)} sets {property}");
                }
            }
        }

        Assert.True(overrides.Count == 0,
            "The SDK boundary role comes from the project's location (ADR 0055 §2), not from the project:"
            + Environment.NewLine + string.Join(Environment.NewLine, overrides));
    }

    [Fact]
    public void Apps_only_use_IJSRuntime_to_import_their_own_module()
    {
        string root = FindSolutionDirectory();
        List<string> violations = [];

        foreach (string projectFile in Directory.EnumerateFiles(Path.Combine(root, "Apps"), "*.csproj", SearchOption.AllDirectories))
        {
            string projectName = Path.GetFileNameWithoutExtension(projectFile);
            violations.AddRange(FindJsRuntimeViolations(Path.GetDirectoryName(projectFile)!, projectName));
        }

        Assert.True(violations.Count == 0,
            "IJSRuntime may only import the app's own module (ADR 0055 §3):" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Checker_flags_a_foreign_call_and_accepts_an_own_import()
    {
        string directory = Directory.CreateTempSubdirectory("sdk-boundary-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "Good.razor"), """
                @inject IJSRuntime JS
                @code {
                    async Task LoadAsync() => _module = await JS.InvokeAsync<IJSObjectReference>(
                        "import", "./_content/My.App/Good.razor.js");
                    async Task UseAsync() => await _module.InvokeVoidAsync("anything");
                }
                """);
            File.WriteAllText(Path.Combine(directory, "Bad.razor.cs"), """
                public partial class Bad
                {
                    [Inject] private IJSRuntime Runtime { get; set; } = default!;
                    Task CopyAsync() => Runtime.InvokeVoidAsync("navigator.clipboard.writeText", "x").AsTask();
                    Task OtherAsync() => Runtime.InvokeAsync<IJSObjectReference>("import", "./_content/Other.Library/x.js").AsTask();
                    Task DynamicAsync(string id) => Runtime.InvokeVoidAsync(id).AsTask();
                }
                """);

            List<string> violations = FindJsRuntimeViolations(directory, "My.App");

            Assert.Equal(3, violations.Count);
            Assert.All(violations, violation => Assert.Contains("Bad.razor.cs", violation, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Finds the identifiers typed <c>IJSRuntime</c> anywhere in the project (an <c>@inject</c> in a
    /// <c>.razor</c> file is often used from its <c>.razor.cs</c>), then checks every
    /// <c>Invoke*Async</c> call on them.
    /// </summary>
    private static List<string> FindJsRuntimeViolations(string projectDirectory, string projectName)
    {
        string[] files = [.. Directory.EnumerateFiles(projectDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => !IsBuildOutput(projectDirectory, path))
            .Order(StringComparer.Ordinal)];
        Dictionary<string, string> sources = files.ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal);

        HashSet<string> runtimeNames = new(StringComparer.Ordinal);
        foreach (string source in sources.Values)
        {
            foreach (Match match in JsRuntimeDeclaration().Matches(source))
            {
                runtimeNames.Add(match.Groups["name"].Value);
            }
        }

        List<string> violations = [];
        string ownPrefix = $"./_content/{projectName}/";
        foreach ((string path, string source) in sources)
        {
            foreach (string name in runtimeNames)
            {
                Regex call = new($@"\b{Regex.Escape(name)}\s*\.\s*Invoke\w*Async\s*(<[^()]*>)?\s*\(");
                foreach (Match match in call.Matches(source))
                {
                    Match arguments = ImportArguments().Match(source, match.Index + match.Length);
                    bool ownImport = arguments.Success
                        && arguments.Index == match.Index + match.Length
                        && arguments.Groups["path"].Value.StartsWith(ownPrefix, StringComparison.Ordinal);
                    if (!ownImport)
                    {
                        int line = source.AsSpan(0, match.Index).Count('\n') + 1;
                        violations.Add($"{Path.GetRelativePath(projectDirectory, path)}:{line}: {name}.{match.Value.Trim()}… must be InvokeAsync<IJSObjectReference>(\"import\", \"{ownPrefix}…\")");
                    }
                }
            }
        }

        return violations;
    }

    private static bool IsBuildOutput(string projectDirectory, string path)
    {
        string relative = Path.GetRelativePath(projectDirectory, path).Replace('\\', '/');
        return relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal);
    }

    private static string FindSolutionDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kazo.Os.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate Kazo.Os.sln.");
    }

    /// <summary><c>@inject IJSRuntime X</c>, a property, field or parameter typed <c>IJSRuntime</c>.</summary>
    [GeneratedRegex(@"(?:@inject\s+(?:Microsoft\.JSInterop\.)?IJSRuntime\s+|\b(?:Microsoft\.JSInterop\.)?IJSRuntime\??\s+)(?<name>[A-Za-z_]\w*)")]
    private static partial Regex JsRuntimeDeclaration();

    /// <summary>The literal arguments of an own-module import: <c>"import", "./_content/…"</c>.</summary>
    [GeneratedRegex(@"\G\s*""import""\s*,\s*""(?<path>[^""]*)""")]
    private static partial Regex ImportArguments();
}
