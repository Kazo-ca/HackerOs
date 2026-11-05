using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Kazo.Os.AppSdk.Threading;
using Kazo.Os.Tools.BackgroundAnalyzer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Kazo.Os.AppSdk.Tests.Threading;

/// <summary>The KOSBG rules of the KazoBackground analyzer (ADR 0128 section 2, ADR 0140).</summary>
public sealed class KazoBackgroundAnalyzerTests
{
    private const string Prelude = """
        using System.Collections.Generic;
        using System.Text.Json.Serialization;
        using System.Threading.Tasks;
        using Kazo.Os.AppSdk;
        using Kazo.Os.AppSdk.Threading;

        [assembly: KazoWireContext(typeof(App.AppJson))]

        namespace App;

        public sealed record Item(string Name, int Size);
        public sealed record Input(List<Item> Items, byte[] Data, Dictionary<string, double>? Weights);
        public sealed record Output(int Count, Item? Largest);
        public sealed record Stranger(int N);

        [JsonSerializable(typeof(Input))]
        [JsonSerializable(typeof(Output))]
        [JsonSerializable(typeof(string))]
        // No JSON source generator in the test compilation: abstract, only its attributes matter.
        internal abstract class AppJson() : JsonSerializerContext(null);

        public static class Jobs
        {
            public static Output Count(Input input, KazoBackgroundContext context) => new(input.Items.Count, null);
            public static Task<int> Size(Item item, KazoBackgroundContext context) => Task.FromResult(item.Size);
            public static int Strange(Stranger input, KazoBackgroundContext context) => input.N;
            public static void Generic<T>(string input, KazoBackgroundContext context) { }
        }

        public static class Holder<T>
        {
            public static void Run(string input, KazoBackgroundContext context) { }
        }

        public sealed class LengthJob : KazoBackgroundJob<string, int>
        {
            public override Task<int> RunAsync(string input, KazoBackgroundContext context) => Task.FromResult(input.Length);
        }

        """;

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        string[] platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        IEnumerable<string> sdk = new[] { typeof(KazoBackground), typeof(Kazo.Os.App.Abstractions.AppManifest), typeof(JsonSerializerContext) }
            .Select(type => type.Assembly.Location);
        return [.. platform.Concat(sdk).Distinct().Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    });

    [Fact]
    public async Task A_static_method_group_with_declared_types_passes()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync("""
            public class Caller
            {
                public async Task Go(Input input)
                {
                    Output output = await KazoBackground.RunAsync<Input, Output>(Jobs.Count, input);
                    await KazoBackground.RunAsync<string>(Local, "x");
                    int length = await KazoBackground.RunAsync<LengthJob, string, int>("abc");
                    static void Local(string text, KazoBackgroundContext context) { }
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_type_reached_through_a_declared_root_passes()
    {
        // Item is only reachable from Input.Items (List<Item>) and int from Item.Size: GetTypeInfo finds both.
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync("""
            public class Caller
            {
                public Task<int> Go(Item item) => KazoBackground.RunAsync<Item, int>(Jobs.Size, item);
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_lambda_an_instance_method_a_capturing_local_function_and_a_delegate_value_are_HOSBG0001()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync("""
            public class Caller
            {
                private int _offset;
                public void Instance(string input, KazoBackgroundContext context) { }
                public async Task Go(System.Action<string, KazoBackgroundContext> value)
                {
                    await KazoBackground.RunAsync<string>((text, context) => { }, "a");
                    await KazoBackground.RunAsync<string>(Instance, "b");
                    await KazoBackground.RunAsync<string>(Capturing, "c");
                    await KazoBackground.RunAsync<string>(value, "d");
                    void Capturing(string text, KazoBackgroundContext context) => _offset++;
                }
            }
            """);

        Assert.Equal(["KOSBG0001", "KOSBG0001", "KOSBG0001", "KOSBG0001"], diagnostics.Select(static d => d.Id));
        Assert.Contains("(a lambda)", diagnostics[0].GetMessage(), StringComparison.Ordinal);
        Assert.Contains("App.Caller.Instance", diagnostics[1].GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_undeclared_input_or_result_is_HOSBG0002()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync("""
            public class Caller
            {
                public Task<int> Go() => KazoBackground.RunAsync<Stranger, int>(Jobs.Strange, new Stranger(1));
            }
            """);

        Diagnostic only = Assert.Single(diagnostics);
        Assert.Equal("KOSBG0002", only.Id);
        Assert.Contains("input type 'Stranger'", only.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_wire_context_every_type_is_HOSBG0002()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            using System.Threading.Tasks;
            using Kazo.Os.AppSdk.Threading;

            public static class Jobs
            {
                public static int Twice(int input, KazoBackgroundContext context) => input * 2;
                public static Task<int> Go() => KazoBackground.RunAsync<int, int>(Twice, 2);
            }
            """,
            prelude: false);

        Assert.Equal(["KOSBG0002", "KOSBG0002"], diagnostics.Select(static d => d.Id));
    }

    [Fact]
    public async Task A_method_of_another_assembly_is_HOSBG0003()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync("""
            public class Caller
            {
                public Task Go() => KazoBackground.RunAsync<string>(System.Console.WriteLine, "hi");
            }
            """);

        Assert.Contains(diagnostics, static d => d.Id == "KOSBG0003" && d.GetMessage().Contains("System.Console.WriteLine", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_generic_method_or_a_method_of_a_generic_type_is_HOSBG0004()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync("""
            public class Caller
            {
                public async Task Go()
                {
                    await KazoBackground.RunAsync<string>(Jobs.Generic<int>, "a");
                    await KazoBackground.RunAsync<string>(Holder<int>.Run, "b");
                }
            }
            """);

        Assert.Equal(["KOSBG0004", "KOSBG0004"], diagnostics.Select(static d => d.Id));
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, bool prelude = true)
    {
        CSharpParseOptions parseOptions = new(LanguageVersion.Latest);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "App",
            [CSharpSyntaxTree.ParseText((prelude ? Prelude : string.Empty) + source, parseOptions)],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        ImmutableArray<Diagnostic> errors = [.. compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error)];
        Assert.True(errors.IsEmpty, string.Join('\n', errors));
        ImmutableArray<Diagnostic> found = await compilation
            .WithAnalyzers([new KazoBackgroundAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        return [.. found.OrderBy(static d => d.Location.SourceSpan.Start)];
    }
}
