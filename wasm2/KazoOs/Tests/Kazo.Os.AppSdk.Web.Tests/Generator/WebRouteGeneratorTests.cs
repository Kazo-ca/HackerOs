extern alias generator;

using System.Collections.Immutable;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Web.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using RouteGenerator = generator::Kazo.Os.Tools.WebRouteGenerator.WebRouteGenerator;

namespace Kazo.Os.AppSdk.Web.Tests.Generator;

/// <summary>The build-time route generator: its errors and its output (ADR 0082).</summary>
public sealed class WebRouteGeneratorTests
{
    private const string Usings = """
        using System.Collections.Generic;
        using Kazo.Os.App.Abstractions;
        using Kazo.Os.AppSdk.Web.Hosting;
        using Kazo.Os.AppSdk.Web.Mvc;

        """;

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        // The test host's framework assemblies, plus the SDK assemblies a site references.
        string[] platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        IEnumerable<string> sdk = new[] { typeof(WebAppDefinition), typeof(AppDefinition), typeof(IServiceProvider), typeof(ServiceCollection) }
            .Select(type => type.Assembly.Location);
        return [.. platform.Concat(sdk).Distinct().Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    });

    private static (ImmutableArray<Diagnostic> Diagnostics, Compilation Output) Run(string source, params RazorFile[] razorFiles)
    {
        CSharpParseOptions parseOptions = new(LanguageVersion.Latest);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Site",
            [CSharpSyntaxTree.ParseText(Usings + source, parseOptions)],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RouteGenerator().AsSourceGenerator()],
            additionalTexts: razorFiles.Select(file => (AdditionalText)new RazorText(file)),
            parseOptions: parseOptions,
            optionsProvider: new RazorOptions());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);
        return (diagnostics, output);
    }

    /// <summary>A <c>.razor</c> file as the Razor SDK passes it: a project-relative <c>TargetPath</c> (base64 in the editorconfig).</summary>
    private sealed record RazorFile(string TargetPath, string Content = "");

    private sealed class RazorText(RazorFile file) : AdditionalText
    {
        public RazorFile File => file;

        public override string Path => "/site/" + file.TargetPath;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(file.Content);
    }

    private sealed class RazorOptions : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(new()
        {
            ["build_property.RootNamespace"] = "Example.Intranet-Site",
            ["build_property.MSBuildProjectDirectory"] = "/site"
        });

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Options([]);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Options(new()
        {
            ["build_metadata.AdditionalFiles.TargetPath"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(((RazorText)textFile).File.TargetPath))
        });

        private sealed class Options(Dictionary<string, string> values) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
        }
    }

    private static string[] Ids(string source) => [.. Run(source).Diagnostics.Select(diagnostic => diagnostic.Id).Distinct()];

    [Fact]
    public void A_valid_site_compiles_with_its_route_table_and_entry_point()
    {
        (ImmutableArray<Diagnostic> diagnostics, Compilation output) = Run("""
            [AppManifest]
            public sealed partial class Intranet : WebAppDefinition
            {
                public override string Id => "org.example.intranet";
                public override string Name => "Intranet";
                public override IReadOnlyList<string> Urls => ["http://intranet.hack/"];
            }

            public sealed class HomeController : KazoController
            {
                public string Index() => "home";
            }
            """);

        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.NotNull(output.GetTypeByMetadataName("Kazo.Os.AppSdk.Web.Generated.KazoWebGeneratedRoutes"));
        Assert.NotNull(output.GetTypeByMetadataName("IntranetWebHost"));
    }

    [Fact]
    public void Two_actions_on_the_same_route_and_method_conflict()
    {
        Assert.Contains("KOSWEB0001", Ids("""
            [Route("api/notes")]
            public sealed class NotesController : KazoApiController
            {
                [HttpGet("{id:int}")] public string A(int id) => "a";
                [HttpGet("{key:int}")] public string B(int key) => "b";
            }
            """));
    }

    [Fact]
    public void Same_route_with_different_methods_does_not_conflict()
    {
        Assert.Empty(Ids("""
            [Route("api/notes")]
            public sealed class NotesController : KazoApiController
            {
                [HttpGet("{id:int}")] public string Get(int id) => "a";
                [HttpDelete("{id:int}")] public string Delete(int id) => "b";
            }
            """));
    }

    [Fact]
    public void An_invalid_template_is_reported()
    {
        Assert.Contains("KOSWEB0002", Ids("""
            public sealed class NotesController : KazoApiController
            {
                [HttpGet("notes/{id")] public string Get(int id) => "a";
            }
            """));
    }

    [Fact]
    public void A_duplicate_route_name_is_reported()
    {
        Assert.Contains("KOSWEB0003", Ids("""
            public sealed class NotesController : KazoApiController
            {
                [HttpGet("a", Name = "same")] public string A() => "a";
                [HttpGet("b", Name = "same")] public string B() => "b";
            }
            """));
    }

    [Fact]
    public void A_parameter_that_cannot_be_bound_is_reported()
    {
        Assert.Contains("KOSWEB0004", Ids("""
            public sealed class NotesController : KazoController
            {
                public string Get(System.IO.Stream stream) => "a";
            }
            """));
    }

    [Fact]
    public void A_controller_without_public_constructor_is_reported()
    {
        Assert.Contains("KOSWEB0005", Ids("""
            public sealed class NotesController : KazoController
            {
                private NotesController() { }
                public string Index() => "a";
            }
            """));
    }

    [Fact]
    public void A_definition_that_is_not_partial_is_reported()
    {
        Assert.Contains("KOSWEB0006", Ids("""
            [AppManifest]
            public sealed class Intranet : WebAppDefinition
            {
                public override string Id => "org.example.intranet";
                public override string Name => "Intranet";
                public override IReadOnlyList<string> Urls => ["http://intranet.hack/"];
            }
            """));
    }

    [Fact]
    public void An_api_action_without_attribute_route_is_reported()
    {
        Assert.Contains("KOSWEB0007", Ids("""
            public sealed class NotesController : KazoApiController
            {
                public string List() => "a";
            }
            """));
    }

    [Fact]
    public void Views_under_Views_are_listed_with_the_class_names_the_razor_compiler_gives_them()
    {
        (ImmutableArray<Diagnostic> diagnostics, Compilation output) = Run("""
            [AppManifest]
            public sealed partial class Intranet : WebAppDefinition
            {
                public override string Id => "org.example.intranet";
                public override string Name => "Intranet";
                public override IReadOnlyList<string> Urls => ["http://intranet.hack/"];
            }

            namespace Example.Intranet_Site.Views.Home { public sealed class Index : Microsoft.AspNetCore.Components.ComponentBase { } public sealed class _2_items : Microsoft.AspNetCore.Components.ComponentBase { } }
            namespace Example.Intranet_Site.Views.Shared { public sealed class _Layout : Microsoft.AspNetCore.Components.ComponentBase { } }
            namespace Custom.Area { public sealed class Dashboard : Microsoft.AspNetCore.Components.ComponentBase { } }
            namespace Custom.Imported { public sealed class Card : Microsoft.AspNetCore.Components.ComponentBase { } }
            """,
            new RazorFile("Views/Home/Index.razor", "<h1>hi</h1>"),
            new RazorFile("Views/Home/2 items.razor"),
            new RazorFile("Views/Shared/_Layout.razor"),
            new RazorFile("Views/Admin/Dashboard.razor", "@namespace Custom.Area\n<p>x</p>"),
            new RazorFile("Views/Widgets/_Imports.razor", "@namespace Custom.Imported"),
            new RazorFile("Views/Widgets/Deep/Card.razor"),
            new RazorFile("_Imports.razor", "@using System"),
            new RazorFile("Components/NotAView.razor"));

        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        string table = output.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("KazoWebGeneratedRoutes.g.cs", StringComparison.Ordinal)).ToString();
        Assert.Contains("""new("Home/Index", typeof(global::Example.Intranet_Site.Views.Home.Index))""", table);
        Assert.Contains("""new("Home/2 items", typeof(global::Example.Intranet_Site.Views.Home._2_items))""", table);
        Assert.Contains("""new("Shared/_Layout", typeof(global::Example.Intranet_Site.Views.Shared._Layout))""", table);
        Assert.Contains("""new("Admin/Dashboard", typeof(global::Custom.Area.Dashboard))""", table);
        Assert.Contains("""new("Widgets/Deep/Card", typeof(global::Custom.Imported.Card))""", table);
        Assert.DoesNotContain("_Imports", table);
        Assert.DoesNotContain("NotAView", table);
        Assert.Contains("KazoWebGeneratedRoutes.Views)", output.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("WebHost.g.cs", StringComparison.Ordinal)).ToString());
    }

    [Fact]
    public void Cshtml_views_are_listed_with_the_class_names_of_the_compiler_mvc_mode()
    {
        (ImmutableArray<Diagnostic> diagnostics, Compilation output) = Run("""
            [AppManifest]
            public sealed partial class Intranet : WebAppDefinition
            {
                public override string Id => "org.example.intranet";
                public override string Name => "Intranet";
                public override IReadOnlyList<string> Urls => ["http://intranet.hack/"];
            }

            namespace AspNetCoreGeneratedDocument { internal sealed class Views_Home_Index { } internal sealed class Views__ViewStart { } internal sealed class Views_My_Dir_1_a_b { } }
            namespace Custom.Admin { internal sealed class Views_Admin_Dashboard { } }
            namespace Custom.Area.Deep { internal sealed class Views_Area_Deep_Card { } }
            namespace Own { internal sealed class Views_Area_Own { } }
            """,
            new RazorFile("Views/Home/Index.cshtml", "@model string\n<h1>hi</h1>"),
            new RazorFile("Views/_ViewStart.cshtml", "@{ Layout = \"_Layout\"; }"),
            new RazorFile("Views/_ViewImports.cshtml", "@using System"),
            new RazorFile("Views/My-Dir/1 a.b.cshtml"),
            new RazorFile("Views/Admin/_ViewImports.cshtml", "@namespace Custom.Admin"),
            new RazorFile("Views/Admin/Dashboard.cshtml"),
            new RazorFile("Views/Area/_ViewImports.cshtml", "@namespace Custom.Area"),
            new RazorFile("Views/Area/Deep/Card.cshtml"),
            new RazorFile("Views/Area/Own.cshtml", "@namespace Own"),
            new RazorFile("_ViewImports.cshtml", "@inherits KazoRazorPage<TModel>"));

        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        string table = output.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("KazoWebGeneratedRoutes.g.cs", StringComparison.Ordinal)).ToString();
        Assert.Contains("""new("Home/Index", typeof(global::AspNetCoreGeneratedDocument.Views_Home_Index))""", table);
        Assert.Contains("""new("_ViewStart", typeof(global::AspNetCoreGeneratedDocument.Views__ViewStart))""", table);
        Assert.Contains("""new("My-Dir/1 a.b", typeof(global::AspNetCoreGeneratedDocument.Views_My_Dir_1_a_b))""", table);
        Assert.Contains("""new("Admin/Dashboard", typeof(global::Custom.Admin.Views_Admin_Dashboard))""", table);
        Assert.Contains("""new("Area/Deep/Card", typeof(global::Custom.Area.Deep.Views_Area_Deep_Card))""", table);
        Assert.Contains("""new("Area/Own", typeof(global::Own.Views_Area_Own))""", table);
        Assert.DoesNotContain("_ViewImports", table);
    }

    [Fact]
    public void A_razor_and_a_cshtml_view_at_the_same_path_are_an_error()
    {
        (ImmutableArray<Diagnostic> diagnostics, _) = Run("""
            namespace AspNetCoreGeneratedDocument { internal sealed class Views_Home_Index { } }
            namespace Example.Intranet_Site.Views.Home { public sealed class Index : Microsoft.AspNetCore.Components.ComponentBase { } }
            public sealed class HomeController : KazoController { public IKazoActionResult Index() => View(); }
            """,
            new RazorFile("Views/Home/Index.cshtml"),
            new RazorFile("Views/Home/Index.razor"));

        Diagnostic duplicate = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "KOSWEB0008");
        Assert.Contains("Views/Home/Index", duplicate.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }
}
