using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Kazo.Os.Tools.BackgroundAnalyzer;

/// <summary>
/// Checks every call to <c>Kazo.Os.AppSdk.Threading.KazoBackground.RunAsync</c> against the rules of
/// ADR 0128, section 2: the task is a static method group (or a task class) of the app's own,
/// non-generic code, and its input and result types are reachable from a
/// <c>JsonSerializerContext</c> the assembly declares with <c>[assembly: KazoWireContext]</c>.
/// </summary>
/// <remarks>
/// A worker is another .NET runtime: the task crosses as a name (assembly, type, method) and its data
/// as JSON. What the build lets through here is what the OS can run there; the OS checks again at run
/// time and throws <c>ArgumentException</c> for anything this analyzer could not see (reflection).
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class KazoBackgroundAnalyzer : DiagnosticAnalyzer
{
    private const string KazoBackgroundType = "Kazo.Os.AppSdk.Threading.KazoBackground";
    private const string WireContextAttribute = "Kazo.Os.AppSdk.KazoWireContextAttribute";
    private const string JsonSerializableAttribute = "System.Text.Json.Serialization.JsonSerializableAttribute";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        BackgroundDiagnostics.NotStaticMethodGroup,
        BackgroundDiagnostics.UndeclaredType,
        BackgroundDiagnostics.ForeignAssembly,
        BackgroundDiagnostics.GenericTask
    ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            INamedTypeSymbol? kazoBackground = start.Compilation.GetTypeByMetadataName(KazoBackgroundType);
            if (kazoBackground is null)
            {
                // The app does not reference the SDK's threading API: nothing to check.
                return;
            }

            // The serializable types are the same for every call of the compilation: computed once.
            WireTypes wire = new(start.Compilation);
            start.RegisterOperationAction(
                operation => AnalyzeInvocation(operation, kazoBackground, wire),
                OperationKind.Invocation);
        });
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, INamedTypeSymbol kazoBackground, WireTypes wire)
    {
        IInvocationOperation invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol method = invocation.TargetMethod;
        if (method.Name != "RunAsync" || !SymbolEqualityComparer.Default.Equals(method.ContainingType, kazoBackground))
        {
            return;
        }

        IAssemblySymbol app = context.Compilation.Assembly;
        Location location = invocation.Syntax.GetLocation();
        ITypeSymbol? input = TypeArgument(method, "TInput");
        ITypeSymbol? result = TypeArgument(method, "TResult");

        if (TypeArgument(method, "TJob") is INamedTypeSymbol job)
        {
            // RunAsync<TJob, TInput, TResult>: the task is a class the task's process instantiates by name.
            CheckOwner(context, job, job.ToDisplayString(), app, location);
            if (job.IsGenericType)
            {
                context.ReportDiagnostic(Diagnostic.Create(BackgroundDiagnostics.GenericTask, location, job.ToDisplayString()));
            }
        }
        else if (invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "job") is { } jobArgument)
        {
            if (!CheckDelegate(context, Unwrap(jobArgument.Value), app))
            {
                return;
            }
        }

        CheckType(context, wire, input, "input", location);
        CheckType(context, wire, result, "result", location);
    }

    /// <summary>HOSBG0001, 0003 and 0004 for the delegate form.</summary>
    /// <returns>Whether the task is a method group worth checking further.</returns>
    private static bool CheckDelegate(OperationAnalysisContext context, IOperation value, IAssemblySymbol app)
    {
        Location location = value.Syntax.GetLocation();
        if (value is not IDelegateCreationOperation { Target: IMethodReferenceOperation reference })
        {
            // A lambda, an anonymous method or a delegate held in a variable: nothing names a method.
            string shown = value is IDelegateCreationOperation { Target: IAnonymousFunctionOperation } ? "(a lambda)" : $"'{value.Syntax}'";
            context.ReportDiagnostic(Diagnostic.Create(BackgroundDiagnostics.NotStaticMethodGroup, location, shown));
            return false;
        }

        IMethodSymbol target = reference.Method;
        string name = $"{target.ContainingType.ToDisplayString()}.{target.Name}";
        if (!target.IsStatic)
        {
            context.ReportDiagnostic(Diagnostic.Create(BackgroundDiagnostics.NotStaticMethodGroup, location, $"'{name}'"));
            return false;
        }

        CheckOwner(context, target.ContainingType, name, app, location);
        if (target.IsGenericMethod || IsInGenericType(target.ContainingType))
        {
            context.ReportDiagnostic(Diagnostic.Create(BackgroundDiagnostics.GenericTask, location, name));
        }

        return true;
    }

    /// <summary>HOSBG0003: the task's code is the calling app's.</summary>
    private static void CheckOwner(OperationAnalysisContext context, INamedTypeSymbol owner, string name, IAssemblySymbol app, Location location)
    {
        if (!SymbolEqualityComparer.Default.Equals(owner.ContainingAssembly, app))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                BackgroundDiagnostics.ForeignAssembly, location, name, owner.ContainingAssembly?.Name ?? "?", app.Name));
        }
    }

    /// <summary>HOSBG0002: a concrete input or result type the app's contexts can serialize.</summary>
    private static void CheckType(OperationAnalysisContext context, WireTypes wire, ITypeSymbol? type, string role, Location location)
    {
        if (type is null || type is ITypeParameterSymbol || wire.Contains(type))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            BackgroundDiagnostics.UndeclaredType, location, role, type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }

    private static ITypeSymbol? TypeArgument(IMethodSymbol method, string name)
    {
        for (int i = 0; i < method.TypeParameters.Length; i++)
        {
            if (method.TypeParameters[i].Name == name)
            {
                return method.TypeArguments[i];
            }
        }

        return null;
    }

    private static IOperation Unwrap(IOperation value)
    {
        while (value is IConversionOperation { IsImplicit: true } conversion && conversion.Operand is not null)
        {
            value = conversion.Operand;
        }

        return value;
    }

    private static bool IsInGenericType(INamedTypeSymbol? type)
    {
        for (; type is not null; type = type.ContainingType)
        {
            if (type.IsGenericType)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The types the assembly's <c>KazoWireContext</c> contexts can serialize: each
    /// <c>[JsonSerializable]</c> root and what the JSON source generator reaches from it (element
    /// types, generic arguments, public properties), as <c>JsonSerializerContext.GetTypeInfo</c> finds them.
    /// </summary>
    private sealed class WireTypes
    {
        private readonly HashSet<ITypeSymbol> _types = new(SymbolEqualityComparer.Default);

        public WireTypes(Compilation compilation)
        {
            foreach (AttributeData declared in compilation.Assembly.GetAttributes())
            {
                if (declared.AttributeClass?.ToDisplayString() != WireContextAttribute
                    || declared.ConstructorArguments.Length != 1
                    || declared.ConstructorArguments[0].Value is not INamedTypeSymbol serializerContext)
                {
                    continue;
                }

                foreach (AttributeData root in serializerContext.GetAttributes())
                {
                    if (root.AttributeClass?.ToDisplayString() == JsonSerializableAttribute
                        && root.ConstructorArguments.Length >= 1
                        && root.ConstructorArguments[0].Value is ITypeSymbol rootType)
                    {
                        Walk(rootType, depth: 0);
                    }
                }
            }
        }

        public bool Contains(ITypeSymbol type) => _types.Contains(Normalize(type));

        private static ITypeSymbol Normalize(ITypeSymbol type) => type.WithNullableAnnotation(NullableAnnotation.None);

        private void Walk(ITypeSymbol type, int depth)
        {
            type = Normalize(type);
            if (depth > 16 || !_types.Add(type))
            {
                return;
            }

            switch (type)
            {
                case IArrayTypeSymbol array:
                    Walk(array.ElementType, depth + 1);
                    return;
                case INamedTypeSymbol named:
                    foreach (ITypeSymbol argument in named.TypeArguments)
                    {
                        Walk(argument, depth + 1);
                    }

                    if (named.SpecialType != SpecialType.None || IsFrameworkType(named))
                    {
                        // Built-in converters (string, DateTime, List<T>...): no member of theirs is serialized.
                        return;
                    }

                    foreach (IPropertySymbol property in AllProperties(named))
                    {
                        Walk(property.Type, depth + 1);
                    }

                    return;
            }
        }

        private static IEnumerable<IPropertySymbol> AllProperties(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                foreach (ISymbol member in current.GetMembers())
                {
                    if (member is IPropertySymbol { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public, GetMethod: not null } property)
                    {
                        yield return property;
                    }
                }
            }
        }

        private static bool IsFrameworkType(INamedTypeSymbol type)
        {
            string? root = type.ContainingNamespace?.ToDisplayString();
            return root is not null && (root == "System" || root.StartsWith("System.", System.StringComparison.Ordinal));
        }
    }
}
