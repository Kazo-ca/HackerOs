using System.Text.RegularExpressions;

namespace HackerOs.Ecosystem.Tests;

/// <summary>
/// Phase 5, section 5.1 of <c>execution-isolation-plan.md</c> for <c>5-accounting-switches</c>: every
/// piece of app code under <c>Apps/</c> runs inside an ambient frame
/// (<c>HackExecutionContext</c>), because the context-switch hook (ADR 0066, 0067) charges code that
/// runs outside any frame to the compositor, not to the app.
/// </summary>
/// <remarks>
/// <para>The entry points where Blazor or JavaScript call app code, and the rule for each:</para>
/// <list type="bullet">
/// <item>every component (<c>.razor</c>, or a class deriving from a component base) derives, directly
/// or through an app-local base, from <c>WindowAppBase</c>, <c>HackComponentBase</c> or
/// <c>HackEmbeddableComponent</c>: these open the frame for parameters, lifecycle, events and
/// after-render work. A plain <c>ComponentBase</c> runs its <c>@onclick</c> handlers outside it;</item>
/// <item>every <c>[JSInvokable]</c> method opens <c>HackExecutionContext.Enter</c> as its first statement;</item>
/// <item>no app code suppresses execution-context flow or starts a raw thread (both lose the frame).</item>
/// </list>
/// <para>
/// Terminal commands and services need no rule: the OS opens the frame around
/// <c>TerminalAppBase.ExecuteAsync</c> and <c>ServiceAppBase</c> runs, and the frame flows into their
/// continuations, timers and <c>Task.Run</c>. Rendering markup and component disposal stay outside the
/// frame on purpose: they are the compositor's work (ADR 0067, section 2).
/// </para>
/// <para>The check is textual, like <see cref="SdkBoundarySourceTests"/>: it needs no compilation.</para>
/// </remarks>
public sealed partial class AmbientFrameSourceTests
{
    private static readonly HashSet<string> AmbientBases = new(StringComparer.Ordinal)
    {
        "WindowAppBase",
        "HackComponentBase",
        "HackEmbeddableComponent",
        // The layout base of a Blazor site (ADR 0084) derives from HackComponentBase in the SDK.
        "HackSiteLayoutBase",
        // ASP.Hack views (ADR 0083): rendered once to static HTML while the site serves a request, so
        // they run in the frame of the site process and have no event handler.
        "HackView",
        "HackLayout"
    };

    private static readonly HashSet<string> PlainComponentBases = new(StringComparer.Ordinal)
    {
        "ComponentBase",
        "OwningComponentBase",
        "LayoutComponentBase"
    };

    private static readonly string[] FrameEscapes =
    [
        "ExecutionContext.SuppressFlow",
        "UnsafeQueueUserWorkItem",
        "UnsafeRegister(",
        "new Thread("
    ];

    [Fact]
    public void Every_app_component_runs_inside_an_ambient_frame()
    {
        string apps = Path.Combine(FindSolutionDirectory(), "Apps");
        Dictionary<string, string> classBases = CollectClassBases(SourceFiles(apps, "*.cs"));
        List<string> violations = [];
        Dictionary<string, string> razorBases = new(StringComparer.Ordinal);

        foreach (string razor in SourceFiles(apps, "*.razor"))
        {
            if (Path.GetFileName(razor) == "_Imports.razor")
            {
                continue;
            }

            string? codeBehind = File.Exists(razor + ".cs") ? File.ReadAllText(razor + ".cs") : null;
            razorBases[razor] = RazorComponentBase(File.ReadAllText(razor), codeBehind);
        }

        // A component may inherit another component (the File Explorer's mobile window, ADR 0171).
        Dictionary<string, string> allBases = new(classBases, StringComparer.Ordinal);
        foreach ((string razor, string componentBase) in razorBases)
        {
            allBases.TryAdd(Path.GetFileNameWithoutExtension(razor), componentBase);
        }

        foreach ((string razor, string componentBase) in razorBases)
        {
            if (!ResolvesToAmbientBase(componentBase, allBases))
            {
                violations.Add($"{Relative(razor)} derives from {componentBase}");
            }
        }

        foreach ((string type, string baseType) in classBases)
        {
            if (PlainComponentBases.Contains(baseType))
            {
                violations.Add($"class {type} derives from {baseType}");
            }
        }

        Assert.True(violations.Count == 0,
            "App components must derive from WindowAppBase, HackComponentBase or HackEmbeddableComponent " +
            "(or an app base deriving from one), so their handlers run in the app's frame:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Every_javascript_callback_enters_the_app_frame_first()
    {
        string apps = Path.Combine(FindSolutionDirectory(), "Apps");
        List<string> violations = [];

        foreach (string file in SourceFiles(apps, "*.cs").Concat(SourceFiles(apps, "*.razor")))
        {
            foreach (string method in JsInvokableMethodsNotEnteringFrame(File.ReadAllText(file)))
            {
                violations.Add($"{Relative(file)}: [JSInvokable] {method}");
            }
        }

        Assert.True(violations.Count == 0,
            "A [JSInvokable] method is called from JavaScript, outside any frame: its first statement must be " +
            "'using IDisposable scope = HackExecutionContext.Enter(AppContext);'"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void No_app_code_escapes_its_frame()
    {
        string apps = Path.Combine(FindSolutionDirectory(), "Apps");
        List<string> violations = [];

        foreach (string file in SourceFiles(apps, "*.cs").Concat(SourceFiles(apps, "*.razor")))
        {
            string text = File.ReadAllText(file);
            violations.AddRange(FrameEscapes
                .Where(escape => text.Contains(escape, StringComparison.Ordinal))
                .Select(escape => $"{Relative(file)} uses {escape}"));
        }

        Assert.True(violations.Count == 0,
            "These APIs drop the ExecutionContext, so the code they run has no app frame:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // The scanners themselves must catch a violation, or the tests above would pass vacuously.
    [Fact]
    public void The_scanners_detect_plain_components_and_unscoped_callbacks()
    {
        Dictionary<string, string> bases = CollectClassBasesFromText(
            ["public abstract class MyDialogBase : HackComponentBase { }", "public class Legacy : ComponentBase { }"]);

        Assert.False(ResolvesToAmbientBase(RazorComponentBase("<div @onclick=\"Go\"></div>", null), bases));
        Assert.False(ResolvesToAmbientBase(RazorComponentBase("<p />", "public partial class Panel : ComponentBase, IDisposable {"), bases));
        Assert.True(ResolvesToAmbientBase(RazorComponentBase("@inherits MyDialogBase\n<p />", null), bases));
        Assert.True(ResolvesToAmbientBase(RazorComponentBase("@inherits HackerOs.AppSdk.Blazor.HackComponentBase\n", null), bases));
        Assert.False(ResolvesToAmbientBase(RazorComponentBase("<p />", "public partial class Panel : IDisposable {"), bases));

        Assert.Equal(["OnTick"], JsInvokableMethodsNotEnteringFrame("""
            [JSInvokable]
            public Task OnTick(int value)
            {
                Count = value;
                return Task.CompletedTask;
            }
            """));
        Assert.Empty(JsInvokableMethodsNotEnteringFrame("""
            [JSInvokable]
            public async Task OnTick(int value)
            {
                // comment
                using IDisposable scope = HackExecutionContext.Enter(AppContext);
                await Task.Yield();
            }
            """));
    }

    /// <summary>
    /// Returns the base class of a Razor component: its <c>@inherits</c>, else the first non-interface
    /// base of its code-behind's <c>partial class</c>, else Razor's default, <c>ComponentBase</c>.
    /// </summary>
    private static string RazorComponentBase(string markup, string? codeBehind)
    {
        Match inherits = InheritsDirective().Match(markup);
        if (inherits.Success)
        {
            return SimpleName(inherits.Groups["type"].Value);
        }

        if (codeBehind is not null && PartialClassBases().Match(codeBehind) is { Success: true } partial)
        {
            string? first = partial.Groups["bases"].Value.Split(',').Select(static part => part.Trim()).FirstOrDefault();
            if (first is not null && !IsInterfaceName(SimpleName(first)))
            {
                return SimpleName(first);
            }
        }

        return "ComponentBase";
    }

    /// <summary>Follows app-local base classes until an ambient base, a plain component base or an unknown type.</summary>
    private static bool ResolvesToAmbientBase(string type, IReadOnlyDictionary<string, string> classBases)
    {
        for (int depth = 0; depth < 10; depth++)
        {
            if (AmbientBases.Contains(type))
            {
                return true;
            }

            if (!classBases.TryGetValue(type, out string? next))
            {
                return false;
            }

            type = next;
        }

        return false;
    }

    private static Dictionary<string, string> CollectClassBases(IEnumerable<string> files) =>
        CollectClassBasesFromText(files.Select(File.ReadAllText));

    /// <summary>Maps each non-partial class declared with a base list to its first non-interface base.</summary>
    private static Dictionary<string, string> CollectClassBasesFromText(IEnumerable<string> sources)
    {
        Dictionary<string, string> bases = new(StringComparer.Ordinal);
        foreach (string source in sources)
        {
            foreach (Match match in ClassDeclaration().Matches(source))
            {
                string first = SimpleName(match.Groups["bases"].Value.Split(',')[0].Trim());
                if (!IsInterfaceName(first))
                {
                    bases[match.Groups["name"].Value] = first;
                }
            }
        }

        return bases;
    }

    /// <summary>Names the <c>[JSInvokable]</c> methods whose first statement does not enter a frame.</summary>
    private static List<string> JsInvokableMethodsNotEnteringFrame(string source)
    {
        List<string> offenders = [];
        foreach (Match attribute in JsInvokableAttribute().Matches(source))
        {
            int open = source.IndexOf('{', attribute.Index);
            Match signature = MethodName().Match(source, attribute.Index + attribute.Length);
            if (open < 0 || !signature.Success)
            {
                continue;
            }

            string firstStatement = string.Join(' ', source[(open + 1)..]
                .Split('\n')
                .Select(static line => line.Trim())
                .SkipWhile(static line => line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                .Take(1));
            if (!(firstStatement.StartsWith("using ", StringComparison.Ordinal)
                && firstStatement.Contains("HackExecutionContext.Enter(", StringComparison.Ordinal)))
            {
                offenders.Add(signature.Groups["name"].Value);
            }
        }

        return offenders;
    }

    private static bool IsInterfaceName(string name) => name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]);

    private static string SimpleName(string type)
    {
        string withoutGenerics = type.Split('<')[0].Trim();
        return withoutGenerics[(withoutGenerics.LastIndexOf('.') + 1)..];
    }

    private static IEnumerable<string> SourceFiles(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string path) => Path.GetRelativePath(FindSolutionDirectory(), path);

    private static string FindSolutionDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HackerOs.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("HackerOs.sln not found above the test output.");
    }

    [GeneratedRegex(@"^\s*@inherits\s+(?<type>[\w.<>]+)", RegexOptions.Multiline)]
    private static partial Regex InheritsDirective();

    [GeneratedRegex(@"partial\s+class\s+\w+\s*:\s*(?<bases>[^{\r\n]+)")]
    private static partial Regex PartialClassBases();

    [GeneratedRegex(@"(?<!partial\s)\bclass\s+(?<name>\w+)(?:<[^>]*>)?(?:\([^)]*\))?\s*:\s*(?<bases>[^{\r\n]+)")]
    private static partial Regex ClassDeclaration();

    [GeneratedRegex(@"\[JSInvokable(?:\([^)]*\))?\]")]
    private static partial Regex JsInvokableAttribute();

    [GeneratedRegex(@"\s(?<name>\w+)\s*\(")]
    private static partial Regex MethodName();
}
