using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Kazo.Os.Tools.WebRouteGenerator;

/// <summary>
/// Writes the route table of an ASP.Kazo site at build time (ADR 0076 §3, ADR 0082):
/// <list type="bullet">
/// <item><c>Kazo.Os.AppSdk.Web.Generated.KazoWebGeneratedRoutes.Table</c>: one
/// <c>KazoControllerActionDescriptor</c> per action of every public controller of the assembly, with
/// its routes (attribute routes combined with the controller's prefix and checked), the code that
/// creates the controller from the request's services, binds each argument and calls the method;</item>
/// <item><c>KazoWebGeneratedRoutes.Views</c>: one <c>KazoViewDescriptor</c> per <c>.razor</c> or <c>.cshtml</c>
/// file under <c>Views/</c>, so <c>View()</c> finds a view by name without reflection (ADR 0083, ADR 0086);</item>
/// <item>for each <c>partial</c> <c>WebAppDefinition</c> without its own entry point, a
/// <c>&lt;Name&gt;WebHost</c> process class and the <c>EntryPoint</c> override that names it.</item>
/// </list>
/// Nothing is read by reflection at run time, so trimming keeps working; conflicts, malformed
/// templates and parameters that cannot be bound are build errors (<c>KOSWEB0001</c>...).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class WebRouteGenerator : IIncrementalGenerator
{
    internal const string MvcNamespace = "Kazo.Os.AppSdk.Web.Mvc";
    internal const string GeneratedNamespace = "Kazo.Os.AppSdk.Web.Generated";
    internal const string GeneratedClass = "KazoWebGeneratedRoutes";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Sites are small assemblies: the whole compilation is analyzed when it changes. The model is
        // compared by value, so unchanged controllers do not rewrite the output.
        // Views (ADR 0083 §2, ADR 0086 §4): the .razor and .cshtml files under Views/ are AdditionalFiles of
        // the Razor SDK; their class is named from the file path as the Razor compiler names it, so the
        // table can reference it without seeing the Razor generator's output.
        IncrementalValuesProvider<RazorFileModel> razorFiles = context.AdditionalTextsProvider
            .Where(static text => text.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                || text.Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, cancellationToken) => ViewAnalyzer.Describe(pair.Left, pair.Right, cancellationToken));

        IncrementalValueProvider<GenerationResult> result = context.CompilationProvider
            .Combine(razorFiles.Collect())
            .Select(static (pair, cancellationToken) =>
                new SiteAnalyzer(pair.Left, cancellationToken).Analyze(ViewAnalyzer.CollectViews(pair.Right)));

        context.RegisterSourceOutput(result, static (production, generation) =>
        {
            foreach (Diagnostic diagnostic in generation.Diagnostics)
            {
                production.ReportDiagnostic(diagnostic);
            }

            foreach (KeyValuePair<string, string> source in generation.Sources)
            {
                production.AddSource(source.Key, SourceText.From(source.Value, Encoding.UTF8));
            }
        });
    }
}

/// <summary>What the generator produces for one compilation.</summary>
internal sealed class GenerationResult : IEquatable<GenerationResult>
{
    public GenerationResult(ImmutableArray<Diagnostic> diagnostics, ImmutableArray<KeyValuePair<string, string>> sources)
    {
        Diagnostics = diagnostics;
        Sources = sources;
    }

    public static GenerationResult Empty { get; } = new([], []);

    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public ImmutableArray<KeyValuePair<string, string>> Sources { get; }

    public bool Equals(GenerationResult? other) =>
        other is not null
        && Diagnostics.SequenceEqual(other.Diagnostics)
        && Sources.SequenceEqual(other.Sources);

    public override bool Equals(object? obj) => Equals(obj as GenerationResult);

    public override int GetHashCode() => Sources.Length ^ (Diagnostics.Length << 16);
}

/// <summary>Analyzes the controllers and site definitions of a compilation and writes the code.</summary>
internal sealed class SiteAnalyzer
{
    private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static readonly Dictionary<string, string> HttpMethodAttributes = new(StringComparer.Ordinal)
    {
        ["HttpGetAttribute"] = "GET",
        ["HttpPostAttribute"] = "POST",
        ["HttpPutAttribute"] = "PUT",
        ["HttpPatchAttribute"] = "PATCH",
        ["HttpDeleteAttribute"] = "DELETE",
        ["HttpHeadAttribute"] = "HEAD",
        ["HttpOptionsAttribute"] = "OPTIONS"
    };

    private readonly Compilation _compilation;
    private readonly System.Threading.CancellationToken _cancellationToken;
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly INamedTypeSymbol? _controllerBase;
    private readonly INamedTypeSymbol? _apiController;
    private readonly INamedTypeSymbol? _webAppDefinition;
    private readonly INamedTypeSymbol? _filterMetadata;
    private readonly INamedTypeSymbol? _httpMethodAttribute;
    private readonly INamedTypeSymbol? _validationAttribute;
    private readonly INamedTypeSymbol? _parsable;
    private readonly INamedTypeSymbol? _task;
    private readonly INamedTypeSymbol? _taskOfT;
    private readonly INamedTypeSymbol? _valueTask;
    private readonly INamedTypeSymbol? _valueTaskOfT;
    private readonly INamedTypeSymbol? _cancellationTokenType;
    private readonly INamedTypeSymbol? _disposable;
    private readonly INamedTypeSymbol? _actionResult;
    private readonly IAssemblySymbol? _frameworkAssembly;
    private readonly StringBuilder _helpers = new();
    private readonly Dictionary<string, string> _modelBinders = new(StringComparer.Ordinal);
    private int _helperIndex;

    public SiteAnalyzer(Compilation compilation, System.Threading.CancellationToken cancellationToken)
    {
        _compilation = compilation;
        _cancellationToken = cancellationToken;
        _controllerBase = compilation.GetTypeByMetadataName($"{WebRouteGenerator.MvcNamespace}.KazoControllerBase");
        _apiController = compilation.GetTypeByMetadataName($"{WebRouteGenerator.MvcNamespace}.KazoApiController");
        _webAppDefinition = compilation.GetTypeByMetadataName("Kazo.Os.AppSdk.Web.Hosting.WebAppDefinition");
        _filterMetadata = compilation.GetTypeByMetadataName($"{WebRouteGenerator.MvcNamespace}.IKazoFilterMetadata");
        _httpMethodAttribute = compilation.GetTypeByMetadataName($"{WebRouteGenerator.MvcNamespace}.HttpMethodAttribute");
        _validationAttribute = compilation.GetTypeByMetadataName("System.ComponentModel.DataAnnotations.ValidationAttribute");
        _parsable = compilation.GetTypeByMetadataName("System.IParsable`1");
        _task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        _taskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        _valueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
        _valueTaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
        _cancellationTokenType = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
        _disposable = compilation.GetTypeByMetadataName("System.IDisposable");
        _actionResult = compilation.GetTypeByMetadataName($"{WebRouteGenerator.MvcNamespace}.IKazoActionResult");
        _frameworkAssembly = _controllerBase?.ContainingAssembly;
    }

    public GenerationResult Analyze() => Analyze([]);

    /// <summary>Analyzes the compilation; <paramref name="views"/> are the views found in the Razor files.</summary>
    public GenerationResult Analyze(ImmutableArray<ViewModel> views)
    {
        if (_controllerBase is null || _frameworkAssembly is null)
        {
            // The assembly does not use ASP.Kazo: nothing to generate.
            return GenerationResult.Empty;
        }

        List<INamedTypeSymbol> types = [];
        CollectTypes(_compilation.Assembly.GlobalNamespace, types);

        List<ActionModel> actions = [];
        foreach (INamedTypeSymbol type in types.Where(IsController).OrderBy(type => type.ToDisplayString(), StringComparer.Ordinal))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            actions.AddRange(AnalyzeController(type));
        }

        CheckConflicts(actions);
        foreach (IGrouping<string, ViewModel> duplicate in views.GroupBy(view => view.Path, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            Report(WebDiagnostics.DuplicateView, Location.None, duplicate.Key);
        }


        List<KeyValuePair<string, string>> sources = [new($"{WebRouteGenerator.GeneratedClass}.g.cs", WriteRouteTable(actions, views))];
        foreach (INamedTypeSymbol definition in types.Where(IsWebAppDefinition).OrderBy(type => type.ToDisplayString(), StringComparer.Ordinal))
        {
            if (WriteEntryPoint(definition) is string entryPoint)
            {
                sources.Add(new($"{definition.ToDisplayString().Replace('<', '_').Replace('>', '_')}.WebHost.g.cs", entryPoint));
            }
        }

        return new GenerationResult([.. _diagnostics], [.. sources]);
    }

    // ------------------------------------------------------------------------------------------
    // Discovery
    // ------------------------------------------------------------------------------------------

    private static void CollectTypes(INamespaceSymbol ns, List<INamedTypeSymbol> types)
    {
        foreach (INamespaceOrTypeSymbol member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
            {
                CollectTypes(child, types);
            }
            else if (member is INamedTypeSymbol type)
            {
                AddWithNested(type, types);
            }
        }
    }

    private static void AddWithNested(INamedTypeSymbol type, List<INamedTypeSymbol> types)
    {
        types.Add(type);
        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
        {
            AddWithNested(nested, types);
        }
    }

    private bool IsController(INamedTypeSymbol type) =>
        type.TypeKind == TypeKind.Class
        && !type.IsAbstract
        && !type.IsGenericType
        && IsPubliclyVisible(type)
        && DerivesFrom(type, _controllerBase!)
        && !HasAttributeInChain(type, "NonControllerAttribute");

    private bool IsWebAppDefinition(INamedTypeSymbol type) =>
        _webAppDefinition is not null && type.TypeKind == TypeKind.Class && !type.IsAbstract && DerivesFrom(type, _webAppDefinition);

    private static bool IsPubliclyVisible(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }

    private static bool DerivesFrom(ITypeSymbol type, INamedTypeSymbol baseType)
    {
        for (ITypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsFrameworkType(INamedTypeSymbol type) =>
        SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, _frameworkAssembly) || type.SpecialType == SpecialType.System_Object;

    private bool HasAttributeInChain(INamedTypeSymbol type, string attributeName)
    {
        for (INamedTypeSymbol? current = type; current is not null && !IsFrameworkType(current); current = current.BaseType)
        {
            if (current.GetAttributes().Any(attribute => IsMvcAttribute(attribute, attributeName)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMvcAttribute(AttributeData attribute, string name) =>
        attribute.AttributeClass is INamedTypeSymbol attributeClass
        && attributeClass.Name == name
        && attributeClass.ContainingNamespace?.ToDisplayString() == WebRouteGenerator.MvcNamespace;

    // ------------------------------------------------------------------------------------------
    // Controllers and actions
    // ------------------------------------------------------------------------------------------

    private IEnumerable<ActionModel> AnalyzeController(INamedTypeSymbol controller)
    {
        string controllerName = controller.Name.EndsWith("Controller", StringComparison.Ordinal) && controller.Name.Length > "Controller".Length
            ? controller.Name.Substring(0, controller.Name.Length - "Controller".Length)
            : controller.Name;
        bool isApi = _apiController is not null && DerivesFrom(controller, _apiController);
        string? factory = WriteControllerFactory(controller);
        if (factory is null)
        {
            yield break;
        }

        List<RoutePrefix> prefixes = ControllerRoutes(controller);
        List<string> controllerFilters = Filters(ControllerChain(controller).Reverse().SelectMany(type => type.GetAttributes()));

        HashSet<IMethodSymbol> overridden = new(SymbolEqualityComparer.Default);
        foreach (INamedTypeSymbol type in ControllerChain(controller))
        {
            foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>().OrderBy(method => method.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0))
            {
                if (overridden.Contains(method))
                {
                    continue;
                }

                for (IMethodSymbol? baseMethod = method.OverriddenMethod; baseMethod is not null; baseMethod = baseMethod.OverriddenMethod)
                {
                    overridden.Add(baseMethod);
                }

                if (!IsAction(method, controller))
                {
                    continue;
                }

                if (AnalyzeAction(controller, controllerName, isApi, factory, prefixes, controllerFilters, method) is ActionModel action)
                {
                    yield return action;
                }
            }
        }
    }

    /// <summary>The controller and its base classes, most derived first, without the framework's.</summary>
    private IEnumerable<INamedTypeSymbol> ControllerChain(INamedTypeSymbol controller)
    {
        for (INamedTypeSymbol? current = controller; current is not null && !IsFrameworkType(current); current = current.BaseType)
        {
            yield return current;
        }
    }

    private bool IsAction(IMethodSymbol method, INamedTypeSymbol controller)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.DeclaredAccessibility != Accessibility.Public
            || method.IsStatic || method.IsGenericMethod || method.IsAbstract)
        {
            return false;
        }

        for (IMethodSymbol? current = method; current is not null; current = current.OverriddenMethod)
        {
            if (current.GetAttributes().Any(attribute => IsMvcAttribute(attribute, "NonActionAttribute")))
            {
                return false;
            }

            if (current.ContainingType is INamedTypeSymbol origin && IsFrameworkType(origin))
            {
                // Overrides of the framework's own members (OnActionExecuting, Ok...) and of object's are not actions.
                return false;
            }
        }

        // Dispose() of a disposable controller is not an action (ASP.NET skips it too).
        if (method.Name == "Dispose" && method.Parameters.Length == 0 && _disposable is not null
            && controller.AllInterfaces.Contains(_disposable, SymbolEqualityComparer.Default))
        {
            return false;
        }

        return true;
    }

    private ActionModel? AnalyzeAction(
        INamedTypeSymbol controller,
        string controllerName,
        bool isApi,
        string factory,
        List<RoutePrefix> prefixes,
        List<string> controllerFilters,
        IMethodSymbol method)
    {
        Location location = method.Locations.FirstOrDefault() ?? Location.None;
        string display = $"{controller.Name}.{method.Name}";
        int id = _helperIndex++;
        List<AttributeData> attributes = [.. MethodAttributes(method)];
        string actionName = attributes.FirstOrDefault(attribute => IsMvcAttribute(attribute, "ActionNameAttribute")) is AttributeData actionNameAttribute
            && actionNameAttribute.ConstructorArguments.Length == 1 && actionNameAttribute.ConstructorArguments[0].Value is string explicitName
            ? explicitName
            : method.Name.EndsWith("Async", StringComparison.Ordinal) && method.Name.Length > "Async".Length
                ? method.Name.Substring(0, method.Name.Length - "Async".Length)
                : method.Name;

        // Routes: [HttpX("t")] gives a route for X; [Route("t")] a route for the methods of the template-less [HttpX].
        List<string> templateLessMethods = [];
        List<RouteModel> actionRoutes = [];
        bool valid = true;
        foreach (AttributeData attribute in attributes)
        {
            if (attribute.AttributeClass is not INamedTypeSymbol attributeClass)
            {
                continue;
            }

            if (_httpMethodAttribute is not null && DerivesFrom(attributeClass, _httpMethodAttribute))
            {
                if (!HttpMethodAttributes.TryGetValue(attributeClass.Name, out string? httpMethod)
                    || attributeClass.ContainingNamespace?.ToDisplayString() != WebRouteGenerator.MvcNamespace)
                {
                    Report(WebDiagnostics.UnsupportedAction, location, display, $"'{attributeClass.Name}' is not one of the HTTP method attributes of ASP.Kazo");
                    valid = false;
                    continue;
                }

                string? template = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null;
                if (template is null)
                {
                    templateLessMethods.Add(httpMethod);
                }
                else
                {
                    actionRoutes.Add(new RouteModel(template, [httpMethod], NamedInt(attribute, "Order"), NamedString(attribute, "Name")));
                }
            }
            else if (IsMvcAttribute(attribute, "RouteAttribute") && attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is string routeTemplate)
            {
                actionRoutes.Add(new RouteModel(routeTemplate, null, NamedInt(attribute, "Order"), NamedString(attribute, "Name")));
            }
        }

        List<RouteModel> routes = [];
        if (actionRoutes.Count > 0)
        {
            foreach (RouteModel route in actionRoutes)
            {
                List<string> methods = route.Methods ?? templateLessMethods;
                bool absolute = route.Template.StartsWith("/", StringComparison.Ordinal) || route.Template.StartsWith("~/", StringComparison.Ordinal);
                if (absolute || prefixes.Count == 0)
                {
                    routes.Add(route with { Methods = methods });
                    continue;
                }

                foreach (RoutePrefix prefix in prefixes)
                {
                    string combined = route.Template.Length == 0 ? prefix.Template
                        : prefix.Template.Length == 0 ? route.Template
                        : prefix.Template.TrimEnd('/') + "/" + route.Template;
                    routes.Add(route with { Template = combined, Methods = methods, Order = route.Order != 0 ? route.Order : prefix.Order });
                }
            }
        }
        else
        {
            foreach (RoutePrefix prefix in prefixes)
            {
                routes.Add(new RouteModel(prefix.Template, templateLessMethods, prefix.Order, null));
            }
        }

        List<RouteModel> checkedRoutes = [];
        foreach (RouteModel route in routes)
        {
            string? replaced = ReplaceTokens(route.Template, controllerName, actionName, out string? tokenError);
            if (replaced is null)
            {
                Report(WebDiagnostics.InvalidTemplate, location, display, tokenError!);
                valid = false;
                continue;
            }

            try
            {
                Kazo.Os.AppSdk.Web.Routing.KazoRouteTemplate parsed = Kazo.Os.AppSdk.Web.Routing.KazoRouteTemplate.Parse(replaced);
                checkedRoutes.Add(route with { Template = parsed.Text, ConflictKey = parsed.GetConflictKey() });
            }
            catch (FormatException exception)
            {
                Report(WebDiagnostics.InvalidTemplate, location, display, exception.Message);
                valid = false;
            }
        }

        List<string> parameterDescriptors = [];
        if (isApi && routes.Count == 0)
        {
            Report(WebDiagnostics.UnsupportedAction, location, display,
                "an action of an API controller must be attribute routed ([Route] on the controller or the action, or [HttpGet(\"...\")]), as with [ApiController]");
            valid = false;
        }

        string? binder = WriteBinder(id, method, display, isApi, location, parameterDescriptors);
        string? invoker = WriteInvoker(id, controller, method, display, location);
        if (!valid || binder is null || invoker is null)
        {
            return null;
        }

        List<string> filters = [.. controllerFilters, .. Filters(attributes)];
        return new ActionModel(
            controller,
            controllerName,
            actionName,
            method.Name,
            isApi,
            routes.Count == 0,
            checkedRoutes,
            templateLessMethods,
            parameterDescriptors,
            filters,
            factory,
            binder,
            invoker,
            DeclaredResultType(method.ReturnType),
            location,
            display);
    }

    /// <summary>
    /// The type an action declares it returns, task unwrapped, for JSON serialization (ASP.NET's
    /// <c>ObjectResult.DeclaredType</c>): a list built with <c>[.. items]</c> has a compiler-internal
    /// runtime type no source-generated JSON context knows, its declared <c>IReadOnlyList&lt;Note&gt;</c> is known.
    /// </summary>
    /// <returns><c>typeof(...)</c>, or <c>null</c> for <c>void</c>, a task, <c>object</c> or an action result.</returns>
    private string DeclaredResultType(ITypeSymbol returnType)
    {
        if (returnType is INamedTypeSymbol { IsGenericType: true } generic
            && (IsType(generic.OriginalDefinition, _taskOfT) || IsType(generic.OriginalDefinition, _valueTaskOfT)))
        {
            returnType = generic.TypeArguments[0];
        }

        if (returnType.SpecialType is SpecialType.System_Void or SpecialType.System_Object
            || IsType(returnType, _task) || IsType(returnType, _valueTask)
            || returnType.TypeKind is TypeKind.TypeParameter or TypeKind.Pointer or TypeKind.Error
            || returnType.IsRefLikeType
            || (_actionResult is not null && (IsType(returnType, _actionResult) || returnType.AllInterfaces.Contains(_actionResult, SymbolEqualityComparer.Default))))
        {
            return "null";
        }

        // typeof() does not accept nullable reference annotations.
        return $"typeof({Fq(returnType.WithNullableAnnotation(NullableAnnotation.NotAnnotated))})";
    }

    private IEnumerable<AttributeData> MethodAttributes(IMethodSymbol method)
    {
        // Attributes of an override include those of the user base method it overrides (Inherited = true).
        for (IMethodSymbol? current = method; current is not null && !IsFrameworkType(current.ContainingType); current = current.OverriddenMethod)
        {
            foreach (AttributeData attribute in current.GetAttributes())
            {
                yield return attribute;
            }
        }
    }

    private List<RoutePrefix> ControllerRoutes(INamedTypeSymbol controller)
    {
        foreach (INamedTypeSymbol type in ControllerChain(controller))
        {
            List<RoutePrefix> prefixes = [.. type.GetAttributes()
                .Where(attribute => IsMvcAttribute(attribute, "RouteAttribute") && attribute.ConstructorArguments.Length == 1)
                .Select(attribute => new RoutePrefix(attribute.ConstructorArguments[0].Value as string ?? string.Empty, NamedInt(attribute, "Order")))];
            if (prefixes.Count > 0)
            {
                return prefixes;
            }
        }

        return [];
    }

    private static string? ReplaceTokens(string template, string controller, string action, out string? error)
    {
        StringBuilder builder = new();
        error = null;
        for (int index = 0; index < template.Length; index++)
        {
            char c = template[index];
            if ((c == '[' || c == ']') && index + 1 < template.Length && template[index + 1] == c)
            {
                builder.Append(c);
                index++;
                continue;
            }

            if (c == ']')
            {
                error = $"'{template}' has an unmatched ']'";
                return null;
            }

            if (c != '[')
            {
                builder.Append(c);
                continue;
            }

            int close = template.IndexOf(']', index + 1);
            if (close < 0)
            {
                error = $"'{template}' has an unmatched '['";
                return null;
            }

            string token = template.Substring(index + 1, close - index - 1);
            switch (token.ToLowerInvariant())
            {
                case "controller":
                    builder.Append(controller);
                    break;
                case "action":
                    builder.Append(action);
                    break;
                case "area":
                    break;
                default:
                    error = $"'{template}' uses the unknown token '[{token}]' (known: [controller], [action], [area])";
                    return null;
            }

            index = close;
        }

        return builder.ToString();
    }

    private List<string> Filters(IEnumerable<AttributeData> attributes)
    {
        List<string> filters = [];
        if (_filterMetadata is null)
        {
            return filters;
        }

        foreach (AttributeData attribute in attributes)
        {
            if (attribute.AttributeClass is INamedTypeSymbol attributeClass
                && attributeClass.AllInterfaces.Contains(_filterMetadata, SymbolEqualityComparer.Default)
                && NewAttribute(attribute) is string creation)
            {
                filters.Add(creation);
            }
        }

        return filters;
    }

    // ------------------------------------------------------------------------------------------
    // Conflicts (KOSWEB0001, KOSWEB0003)
    // ------------------------------------------------------------------------------------------

    private void CheckConflicts(List<ActionModel> actions)
    {
        List<(ActionModel Action, RouteModel Route)> attributeRoutes = [.. actions.SelectMany(action => action.Routes.Select(route => (action, route)))];
        for (int first = 0; first < attributeRoutes.Count; first++)
        {
            for (int second = first + 1; second < attributeRoutes.Count; second++)
            {
                (ActionModel a, RouteModel ra) = attributeRoutes[first];
                (ActionModel b, RouteModel rb) = attributeRoutes[second];
                if (ra.Order == rb.Order && ra.ConflictKey == rb.ConflictKey && Overlap(ra.Methods!, rb.Methods!) is string methods)
                {
                    Report(WebDiagnostics.RouteConflict, b.Location, "/" + rb.Template, b.Display, a.Display, methods);
                }
            }
        }

        Dictionary<string, (ActionModel Action, RouteModel Route)> names = new(StringComparer.OrdinalIgnoreCase);
        foreach ((ActionModel action, RouteModel route) in attributeRoutes.Where(item => item.Route.Name is not null))
        {
            if (names.TryGetValue(route.Name!, out (ActionModel Action, RouteModel Route) existing)
                && !string.Equals(existing.Route.Template, route.Template, StringComparison.OrdinalIgnoreCase))
            {
                Report(WebDiagnostics.DuplicateRouteName, action.Location, route.Name!, existing.Action.Display, existing.Route.Template, action.Display, route.Template);
            }
            else
            {
                names[route.Name!] = (action, route);
            }
        }

        // Conventionally routed actions: one action per controller, name and method.
        List<ActionModel> conventional = [.. actions.Where(action => action.IsConventional)];
        for (int first = 0; first < conventional.Count; first++)
        {
            for (int second = first + 1; second < conventional.Count; second++)
            {
                ActionModel a = conventional[first];
                ActionModel b = conventional[second];
                if (string.Equals(a.ControllerName, b.ControllerName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.ActionName, b.ActionName, StringComparison.OrdinalIgnoreCase)
                    && Overlap(a.TemplateLessMethods, b.TemplateLessMethods) is string methods)
                {
                    Report(WebDiagnostics.RouteConflict, b.Location, $"{{controller}}/{{action}} = {a.ControllerName}/{a.ActionName}", b.Display, a.Display, methods);
                }
            }
        }
    }

    private static string? Overlap(List<string> first, List<string> second)
    {
        if (first.Count == 0 && second.Count == 0)
        {
            return "every method";
        }

        if (first.Count == 0)
        {
            return string.Join(", ", second);
        }

        if (second.Count == 0)
        {
            return string.Join(", ", first);
        }

        List<string> common = [.. first.Intersect(second, StringComparer.Ordinal)];
        return common.Count == 0 ? null : string.Join(", ", common);
    }

    // ------------------------------------------------------------------------------------------
    // Controller factory, binder and invoker
    // ------------------------------------------------------------------------------------------

    private string? WriteControllerFactory(INamedTypeSymbol controller)
    {
        IMethodSymbol[] constructors = [.. controller.InstanceConstructors.Where(constructor => constructor.DeclaredAccessibility == Accessibility.Public)];
        if (constructors.Length == 0)
        {
            Report(WebDiagnostics.ControllerNotCreatable, controller.Locations.FirstOrDefault() ?? Location.None, controller.Name, "it has no public constructor");
            return null;
        }

        // ActivatorUtilities' rule: the constructor with the most parameters.
        IMethodSymbol constructor = constructors.OrderByDescending(candidate => candidate.Parameters.Length).First();
        const string extensions = "global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions";
        IEnumerable<string> arguments = constructor.Parameters.Select(parameter =>
            parameter.Type.IsValueType
                ? parameter.HasExplicitDefaultValue ? DefaultValue(parameter) : $"{extensions}.GetRequiredService<{Fq(parameter.Type)}>(services)"
                : parameter.HasExplicitDefaultValue || parameter.NullableAnnotation == NullableAnnotation.Annotated
                    ? $"({extensions}.GetService<{Fq(parameter.Type)}>(services) ?? {DefaultValue(parameter)})"
                    : $"{extensions}.GetRequiredService<{Fq(parameter.Type)}>(services)");
        return $"static services => new {Fq(controller)}({string.Join(", ", arguments)})";
    }

    private string? WriteBinder(int id, IMethodSymbol method, string display, bool isApi, Location location, List<string> descriptors)
    {
        StringBuilder body = new();
        string name = $"Bind{id}";
        body.AppendLine($"        private static object?[] {name}(global::{WebRouteGenerator.MvcNamespace}.KazoModelBinder binder)");
        body.AppendLine("        {");
        body.AppendLine($"            object?[] arguments = new object?[{method.Parameters.Length}];");
        bool valid = true;
        int bodyParameters = 0;
        for (int index = 0; index < method.Parameters.Length; index++)
        {
            IParameterSymbol parameter = method.Parameters[index];
            string local = $"a{index}";
            if (BindParameter(parameter, isApi, local, body, out string source, out string? error))
            {
                if (source == "Body")
                {
                    bodyParameters++;
                }

                descriptors.Add($"new global::{WebRouteGenerator.MvcNamespace}.KazoActionParameterDescriptor({Literal(parameter.Name)}, typeof({Fq(TypeForTypeof(parameter.Type))}), global::{WebRouteGenerator.MvcNamespace}.KazoBindingSource.{source})");
                body.AppendLine($"            arguments[{index}] = {local};");
            }
            else
            {
                Report(WebDiagnostics.UnbindableParameter, parameter.Locations.FirstOrDefault() ?? location, parameter.Name, display, error!);
                valid = false;
            }
        }

        if (bodyParameters > 1)
        {
            Report(WebDiagnostics.UnbindableParameter, location, "(several)", display, "only one parameter can come from the body ([FromBody] or a complex type of an API controller)");
            valid = false;
        }

        body.AppendLine("            return arguments;");
        body.AppendLine("        }");
        if (!valid)
        {
            return null;
        }

        _helpers.AppendLine().Append(body);
        return $"static binder => {name}(binder)";
    }

    private bool BindParameter(IParameterSymbol parameter, bool isApi, string local, StringBuilder body, out string source, out string? error)
    {
        error = null;
        source = "Value";
        ITypeSymbol type = parameter.Type;
        string fq = Fq(type);
        if (parameter.RefKind != RefKind.None)
        {
            error = "ref, out and in parameters are not supported";
            return false;
        }

        if (type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.TypeParameter or TypeKind.Dynamic)
        {
            error = "its type cannot come from a request";
            return false;
        }

        List<AttributeData> attributes = [.. parameter.GetAttributes()];
        AttributeData? from = attributes.FirstOrDefault(attribute => attribute.AttributeClass is { } c
            && c.ContainingNamespace?.ToDisplayString() == WebRouteGenerator.MvcNamespace && c.Name.StartsWith("From", StringComparison.Ordinal));
        string fieldName = (from is null ? null : NamedString(from, "Name")) ?? parameter.Name;
        string fromName = from?.AttributeClass?.Name ?? string.Empty;

        // Framework-supplied values.
        string? special = SpecialValue(type);
        if (special is not null && from is null)
        {
            source = "Special";
            body.AppendLine($"            {fq} {local} = {special};");
            return true;
        }

        if (fromName == "FromServicesAttribute")
        {
            source = "Services";
            body.AppendLine($"            {fq} {local} = binder.BindService<{fq}>();");
            return true;
        }

        bool isNullable = type.NullableAnnotation == NullableAnnotation.Annotated || IsNullableValueType(type);
        if (fromName == "FromBodyAttribute" || (from is null && isApi && IsComplex(type)))
        {
            if (!IsComplex(type) && !IsSimple(type, out _) && !IsArrayOfSimple(type, out _, out _))
            {
                error = "the body is read with System.Text.Json into a concrete type";
                return false;
            }

            source = "Body";
            bool required = !isNullable && !parameter.HasExplicitDefaultValue;
            body.AppendLine($"            {fq} {local} = binder.BindBody<{fq}>({Literal(parameter.Name)}, {Bool(required)})!;");
            AppendValidation(parameter, local, body, validateModel: IsComplex(type));
            return true;
        }

        string sources = fromName switch
        {
            "FromRouteAttribute" => "Route",
            "FromQueryAttribute" => "Query",
            "FromFormAttribute" => "Form",
            "FromHeaderAttribute" => "Header",
            _ => isApi ? "Route | global::Kazo.Os.AppSdk.Web.Mvc.KazoValueSources.Query" : "Default"
        };
        string sourcesExpression = $"global::{WebRouteGenerator.MvcNamespace}.KazoValueSources.{sources}";
        source = fromName switch
        {
            "FromRouteAttribute" => "Route",
            "FromQueryAttribute" => "Query",
            "FromFormAttribute" => "Form",
            "FromHeaderAttribute" => "Header",
            _ => "Value"
        };

        if (IsSimple(type, out string parser))
        {
            bool required = !type.IsValueType && type.NullableAnnotation == NullableAnnotation.NotAnnotated && !parameter.HasExplicitDefaultValue;
            body.AppendLine($"            {fq} {local} = binder.BindValue<{fq}>({Literal(fieldName)}, {sourcesExpression}, {parser}, {Bool(required)}, {DefaultValue(parameter)});");
            AppendValidation(parameter, local, body, validateModel: false);
            return true;
        }

        if (IsArrayOfSimple(type, out ITypeSymbol? element, out string elementParser))
        {
            body.AppendLine($"            {fq} {local} = binder.BindArray<{Fq(element!)}>({Literal(fieldName)}, {sourcesExpression}, {elementParser});");
            AppendValidation(parameter, local, body, validateModel: false);
            return true;
        }

        if (IsComplex(type) && fromName != "FromHeaderAttribute")
        {
            if (ModelBinder(type, out error) is not string binderMethod)
            {
                return false;
            }

            source = "Model";
            body.AppendLine($"            {fq} {local} = {binderMethod}(binder, {sourcesExpression});");
            AppendValidation(parameter, local, body, validateModel: true);
            return true;
        }

        error = $"'{type.ToDisplayString()}' is neither a simple type (string, number, Guid, date, enum, IParsable) nor a concrete class; use [FromServices] or [FromBody]";
        return false;
    }

    private string? SpecialValue(ITypeSymbol type)
    {
        if (_cancellationTokenType is not null && SymbolEqualityComparer.Default.Equals(type, _cancellationTokenType))
        {
            return "binder.HttpContext.RequestAborted";
        }

        string name = type.ToDisplayString();
        return name switch
        {
            "Kazo.Os.AppSdk.Web.Http.KazoHttpContext" => "binder.HttpContext",
            "Kazo.Os.AppSdk.Web.Http.KazoHttpRequest" => "binder.HttpContext.Request",
            "Kazo.Os.AppSdk.Web.Http.KazoHttpResponse" => "binder.HttpContext.Response",
            "Kazo.Os.AppSdk.Web.Mvc.KazoModelStateDictionary" => "binder.ModelState",
            _ => null
        };
    }

    private void AppendValidation(IParameterSymbol parameter, string local, StringBuilder body, bool validateModel)
    {
        List<string> attributes = [];
        if (_validationAttribute is not null)
        {
            foreach (AttributeData attribute in parameter.GetAttributes())
            {
                if (attribute.AttributeClass is INamedTypeSymbol attributeClass && DerivesFrom(attributeClass, _validationAttribute)
                    && NewAttribute(attribute) is string creation)
                {
                    attributes.Add(creation);
                }
            }
        }

        if (attributes.Count > 0)
        {
            body.AppendLine($"            binder.ValidateValue({Literal(parameter.Name)}, {local}, {string.Join(", ", attributes)});");
        }

        if (validateModel)
        {
            body.AppendLine($"            binder.Validate<{Fq(parameter.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated))}>({local});");
        }
    }

    private string? ModelBinder(ITypeSymbol type, out string? error)
    {
        error = null;
        string key = Fq(type);
        if (_modelBinders.TryGetValue(key, out string? existing))
        {
            return existing;
        }

        if (type is not INamedTypeSymbol named)
        {
            error = "its type cannot be built from fields";
            return null;
        }

        IMethodSymbol[] constructors = [.. named.InstanceConstructors.Where(constructor => constructor.DeclaredAccessibility == Accessibility.Public)];
        IMethodSymbol? constructor = constructors.FirstOrDefault(candidate => candidate.Parameters.Length == 0)
            ?? (constructors.Length == 1 ? constructors[0] : null);
        if (constructor is null && !named.IsValueType)
        {
            error = $"'{type.ToDisplayString()}' needs a public parameterless constructor or a single public constructor to be built from form fields";
            return null;
        }

        string name = $"BindModel{_modelBinders.Count}";
        _modelBinders[key] = name;
        StringBuilder body = new();
        string typeName = Fq(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
        body.AppendLine();
        body.AppendLine($"        private static {typeName} {name}(global::{WebRouteGenerator.MvcNamespace}.KazoModelBinder binder, global::{WebRouteGenerator.MvcNamespace}.KazoValueSources sources)");
        body.AppendLine("        {");
        HashSet<string> covered = new(StringComparer.OrdinalIgnoreCase);
        List<string> constructorArguments = [];
        foreach (IParameterSymbol parameter in constructor?.Parameters ?? [])
        {
            covered.Add(parameter.Name);
            if (IsSimple(parameter.Type, out string parser))
            {
                constructorArguments.Add($"binder.BindValue<{Fq(parameter.Type)}>({Literal(parameter.Name)}, sources, {parser}, false, {DefaultValue(parameter)})");
            }
            else if (IsArrayOfSimple(parameter.Type, out ITypeSymbol? element, out string elementParser))
            {
                constructorArguments.Add($"binder.BindArray<{Fq(element!)}>({Literal(parameter.Name)}, sources, {elementParser})");
            }
            else
            {
                constructorArguments.Add(DefaultValue(parameter));
            }
        }

        List<string> initOnly = [];
        List<string> setters = [];
        int index = 0;
        foreach (IPropertySymbol property in AllProperties(named))
        {
            if (covered.Contains(property.Name) || property.SetMethod is not { DeclaredAccessibility: Accessibility.Public } setter
                || property.IsIndexer || property.IsStatic)
            {
                continue;
            }

            string propertyType = Fq(property.Type);
            string field = Literal(property.Name);
            string target = EscapeIdentifier(property.Name);
            if (IsSimple(property.Type, out string propertyParser))
            {
                if (setter.IsInitOnly)
                {
                    // An init-only property is set in the initializer: the bound value, or default when absent.
                    initOnly.Add($"{target} = binder.BindValue<{propertyType}>({field}, sources, {propertyParser}, false, default!)");
                }
                else
                {
                    setters.Add($"            if (binder.TryBindProperty<{propertyType}>({field}, sources, {propertyParser}, out {propertyType} v{index})) model.{target} = v{index};");
                }
            }
            else if (IsArrayOfSimple(property.Type, out ITypeSymbol? element, out string elementParser))
            {
                string bind = $"binder.BindArray<{Fq(element!)}>({field}, sources, {elementParser})";
                if (setter.IsInitOnly)
                {
                    initOnly.Add($"{target} = {bind}");
                }
                else
                {
                    setters.Add($"            if (binder.TryGetRawValue({field}, sources, out _)) model.{target} = {bind};");
                }
            }

            index++;
        }

        string creation = constructor is null || constructor.Parameters.Length == 0 && !named.IsValueType
            ? $"new {typeName}()"
            : $"new {typeName}({string.Join(", ", constructorArguments)})";
        body.AppendLine($"            {typeName} model = {creation}{(initOnly.Count > 0 ? " { " + string.Join(", ", initOnly) + " }" : string.Empty)};");
        foreach (string setter in setters)
        {
            body.AppendLine(setter);
        }

        body.AppendLine("            return model;");
        body.AppendLine("        }");
        _helpers.Append(body);
        return name;
    }

    private static IEnumerable<IPropertySymbol> AllProperties(INamedTypeSymbol type)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (INamedTypeSymbol? current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (IPropertySymbol property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.DeclaredAccessibility == Accessibility.Public && seen.Add(property.Name))
                {
                    yield return property;
                }
            }
        }
    }

    private string? WriteInvoker(int id, INamedTypeSymbol controller, IMethodSymbol method, string display, Location location)
    {
        string name = $"Invoke{id}";
        string arguments = string.Join(", ", method.Parameters.Select((parameter, index) => $"({Fq(parameter.Type)})arguments[{index}]!"));
        string call = $"(({Fq(controller)})controller).{EscapeIdentifier(method.Name)}({arguments})";
        ITypeSymbol returnType = method.ReturnType;
        StringBuilder body = new();
        body.AppendLine();
        string signature = $"global::System.Threading.Tasks.ValueTask<object?> {name}(global::{WebRouteGenerator.MvcNamespace}.KazoControllerBase controller, object?[] arguments)";
        string empty = $"global::{WebRouteGenerator.MvcNamespace}.KazoEmptyResult.Instance";
        if (method.ReturnsVoid)
        {
            body.AppendLine($"        private static {signature}");
            body.AppendLine("        {");
            body.AppendLine($"            {call};");
            body.AppendLine($"            return new global::System.Threading.Tasks.ValueTask<object?>({empty});");
            body.AppendLine("        }");
        }
        else if (IsType(returnType, _task) || IsType(returnType, _valueTask))
        {
            body.AppendLine($"        private static async {signature}");
            body.AppendLine("        {");
            body.AppendLine($"            await {call}.ConfigureAwait(false);");
            body.AppendLine($"            return {empty};");
            body.AppendLine("        }");
        }
        else if (returnType is INamedTypeSymbol { IsGenericType: true } generic
            && (IsType(generic.OriginalDefinition, _taskOfT) || IsType(generic.OriginalDefinition, _valueTaskOfT)))
        {
            body.AppendLine($"        private static async {signature}");
            body.AppendLine("        {");
            body.AppendLine($"            return await {call}.ConfigureAwait(false);");
            body.AppendLine("        }");
        }
        else if (returnType.IsRefLikeType || returnType.TypeKind == TypeKind.Pointer)
        {
            Report(WebDiagnostics.UnsupportedAction, location, display, $"its return type '{returnType.ToDisplayString()}' cannot be boxed");
            return null;
        }
        else
        {
            body.AppendLine($"        private static {signature} => new global::System.Threading.Tasks.ValueTask<object?>({call});");
        }

        _helpers.Append(body);
        return $"static (controller, arguments) => {name}(controller, arguments)";
    }

    private static bool IsType(ITypeSymbol type, INamedTypeSymbol? expected) =>
        expected is not null && SymbolEqualityComparer.Default.Equals(type, expected);

    // ------------------------------------------------------------------------------------------
    // Type classification
    // ------------------------------------------------------------------------------------------

    private bool IsSimple(ITypeSymbol type, out string parser)
    {
        const string parsers = "global::" + WebRouteGenerator.MvcNamespace + ".KazoValueParsers";
        if (type.SpecialType == SpecialType.System_String)
        {
            parser = $"{parsers}.String";
            return true;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            parser = $"{parsers}.Enum<{Fq(type)}>()";
            return true;
        }

        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            && IsSimple(nullable.TypeArguments[0], out string inner))
        {
            parser = $"{parsers}.Nullable<{Fq(nullable.TypeArguments[0])}>({inner})";
            return true;
        }

        if (_parsable is not null && type.AllInterfaces.Any(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, _parsable)
                && SymbolEqualityComparer.Default.Equals(candidate.TypeArguments[0], type)))
        {
            parser = $"{parsers}.Parsable<{Fq(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated))}>()";
            return true;
        }

        parser = string.Empty;
        return false;
    }

    private bool IsArrayOfSimple(ITypeSymbol type, out ITypeSymbol? element, out string parser)
    {
        element = (type as IArrayTypeSymbol)?.ElementType;
        parser = string.Empty;
        return element is not null && ((IArrayTypeSymbol)type).Rank == 1 && IsSimple(element, out parser);
    }

    private bool IsComplex(ITypeSymbol type) =>
        type is INamedTypeSymbol named
        && (named.TypeKind == TypeKind.Class || named.TypeKind == TypeKind.Struct)
        && !named.IsAbstract
        && !IsSimple(type, out _)
        && SpecialValue(type) is null
        && named.SpecialType == SpecialType.None
        && !IsType(named.OriginalDefinition, _taskOfT);

    private static bool IsNullableValueType(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private static ITypeSymbol TypeForTypeof(ITypeSymbol type) =>
        type.IsReferenceType ? type.WithNullableAnnotation(NullableAnnotation.NotAnnotated) : type;

    // ------------------------------------------------------------------------------------------
    // Output
    // ------------------------------------------------------------------------------------------

    private string WriteRouteTable(List<ActionModel> actions, ImmutableArray<ViewModel> views)
    {
        StringBuilder code = new();
        code.AppendLine("// <auto-generated/>");
        code.AppendLine("// ASP.Kazo route table (ADR 0082), written by Kazo.Os.Tools.WebRouteGenerator. Do not edit.");
        code.AppendLine("#nullable enable");
        code.AppendLine("#pragma warning disable CS0612, CS0618, CS1591, CS8600, CS8601, CS8602, CS8603, CS8604, CS8619, CS8625");
        code.AppendLine($"namespace {WebRouteGenerator.GeneratedNamespace}");
        code.AppendLine("{");
        code.AppendLine("    /// <summary>The controller actions of this assembly, for <c>KazoWebApplication.MapControllers</c>.</summary>");
        code.AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"Kazo.Os.Tools.WebRouteGenerator\", \"1.0.0\")]");
        code.AppendLine($"    internal static class {WebRouteGenerator.GeneratedClass}");
        code.AppendLine("    {");
        code.AppendLine("        /// <summary>Gets the route table.</summary>");
        code.AppendLine($"        public static global::{WebRouteGenerator.MvcNamespace}.KazoControllerRouteTable Table {{ get; }} = new(new global::{WebRouteGenerator.MvcNamespace}.KazoControllerActionDescriptor[]");
        code.AppendLine("        {");
        foreach (ActionModel action in actions)
        {
            code.AppendLine($"            new global::{WebRouteGenerator.MvcNamespace}.KazoControllerActionDescriptor");
            code.AppendLine("            {");
            code.AppendLine($"                ControllerType = typeof({Fq(action.Controller)}),");
            code.AppendLine($"                ControllerName = {Literal(action.ControllerName)},");
            code.AppendLine($"                ActionName = {Literal(action.ActionName)},");
            code.AppendLine($"                MethodName = {Literal(action.MethodName)},");
            code.AppendLine($"                IsApiController = {Bool(action.IsApi)},");
            code.AppendLine($"                AttributeRoutes = new global::{WebRouteGenerator.MvcNamespace}.KazoAttributeRouteInfo[] {{ {string.Join(", ", action.Routes.Select(route => $"new({Literal(route.Template)}, {Strings(route.Methods!)}, {route.Order}, {(route.Name is null ? "null" : Literal(route.Name))})"))} }},");
            code.AppendLine($"                HttpMethods = {Strings(action.TemplateLessMethods)},");
            code.AppendLine($"                Parameters = new global::{WebRouteGenerator.MvcNamespace}.KazoActionParameterDescriptor[] {{ {string.Join(", ", action.Parameters)} }},");
            code.AppendLine($"                Filters = new global::{WebRouteGenerator.MvcNamespace}.IKazoFilterMetadata[] {{ {string.Join(", ", action.Filters)} }},");
            code.AppendLine($"                CreateController = {action.Factory},");
            code.AppendLine($"                BindArguments = {action.Binder},");
            code.AppendLine($"                Invoke = {action.Invoker},");
            code.AppendLine($"                DeclaredResultType = {action.DeclaredResultType},");
            code.AppendLine("            },");
        }

        code.AppendLine("        });");
        code.AppendLine();
        code.AppendLine("        /// <summary>Gets the views: the Razor components and pages under <c>Views/</c>, by path without extension (ADR 0083, ADR 0086).</summary>");
        code.AppendLine("        public static global::Kazo.Os.AppSdk.Web.Views.KazoViewTable Views { get; } = new(new global::Kazo.Os.AppSdk.Web.Views.KazoViewDescriptor[]");
        code.AppendLine("        {");
        foreach (ViewModel view in views)
        {
            code.AppendLine($"            new({Literal(view.Path)}, typeof(global::{view.TypeName})),");
        }

        code.AppendLine("        });");
        code.Append(_helpers);
        code.AppendLine("    }");
        code.AppendLine("}");
        return code.ToString();
    }

    private string? WriteEntryPoint(INamedTypeSymbol definition)
    {
        if (definition.GetMembers("EntryPoint").Any(member => member is IPropertySymbol { IsOverride: true }))
        {
            return null;
        }

        bool isPartial = definition.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax(_cancellationToken))
            .OfType<ClassDeclarationSyntax>()
            .Any(syntax => syntax.Modifiers.Any(SyntaxKind.PartialKeyword));
        if (!isPartial || definition.ContainingType is not null)
        {
            Report(WebDiagnostics.DefinitionNotPartial, definition.Locations.FirstOrDefault() ?? Location.None, definition.Name);
            return null;
        }

        string hostName = definition.Name + "WebHost";
        string accessibility = definition.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        string? ns = definition.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : null;
        string indent = ns is null ? string.Empty : "    ";
        StringBuilder code = new();
        code.AppendLine("// <auto-generated/>");
        code.AppendLine("// Entry point of an ASP.Kazo site (ADR 0082), written by Kazo.Os.Tools.WebRouteGenerator. Do not edit.");
        code.AppendLine("#nullable enable");
        if (ns is not null)
        {
            code.AppendLine($"namespace {ns}");
            code.AppendLine("{");
        }

        code.AppendLine($"{indent}partial class {EscapeIdentifier(definition.Name)}");
        code.AppendLine($"{indent}{{");
        code.AppendLine($"{indent}    /// <summary>Gets the site's process class, <see cref=\"{hostName}\"/>.</summary>");
        code.AppendLine($"{indent}    public override global::System.Type EntryPoint => typeof({hostName});");
        code.AppendLine($"{indent}}}");
        code.AppendLine();
        code.AppendLine($"{indent}/// <summary>The process of the site <see cref=\"{EscapeIdentifier(definition.Name)}\"/>: listens on its URLs and serves its controllers.</summary>");
        code.AppendLine($"{indent}[global::System.CodeDom.Compiler.GeneratedCode(\"Kazo.Os.Tools.WebRouteGenerator\", \"1.0.0\")]");
        code.AppendLine($"{indent}{accessibility} sealed class {hostName} : global::Kazo.Os.AppSdk.Web.Hosting.KazoWebHost");
        code.AppendLine($"{indent}{{");
        code.AppendLine($"{indent}    /// <summary>Initializes the site process with its manifest.</summary>");
        code.AppendLine($"{indent}    public {hostName}(global::Kazo.Os.App.Abstractions.AppManifest manifest)");
        code.AppendLine($"{indent}        : base(manifest, new {Fq(definition)}(), global::{WebRouteGenerator.GeneratedNamespace}.{WebRouteGenerator.GeneratedClass}.Table, global::{WebRouteGenerator.GeneratedNamespace}.{WebRouteGenerator.GeneratedClass}.Views)");
        code.AppendLine($"{indent}    {{");
        code.AppendLine($"{indent}    }}");
        code.AppendLine($"{indent}}}");
        if (ns is not null)
        {
            code.AppendLine("}");
        }

        return code.ToString();
    }

    // ------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------

    private void Report(DiagnosticDescriptor descriptor, Location location, params object[] arguments) =>
        _diagnostics.Add(Diagnostic.Create(descriptor, location, arguments));

    private static string Fq(ITypeSymbol type) => type.ToDisplayString(FullyQualified);

    private static string Literal(string text) => SymbolDisplay.FormatLiteral(text, quote: true);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Strings(IEnumerable<string> values) => $"new string[] {{ {string.Join(", ", values.Select(Literal))} }}";

    private static string EscapeIdentifier(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private static int NamedInt(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name).Value.Value is int value ? value : 0;

    private static string? NamedString(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name).Value.Value as string;

    private static string DefaultValue(IParameterSymbol parameter)
    {
        if (!parameter.HasExplicitDefaultValue || parameter.ExplicitDefaultValue is null)
        {
            return "default!";
        }

        object value = parameter.ExplicitDefaultValue;
        return value switch
        {
            string text => Literal(text),
            bool flag => Bool(flag),
            char character => SymbolDisplay.FormatLiteral(character, quote: true),
            _ => $"({Fq(parameter.Type)})({SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false)})"
        };
    }

    private static string? NewAttribute(AttributeData attribute)
    {
        if (attribute.AttributeClass is not INamedTypeSymbol attributeClass || attribute.AttributeConstructor is null)
        {
            return null;
        }

        string arguments = string.Join(", ", attribute.ConstructorArguments.Select(ToCSharp));
        string named = string.Join(", ", attribute.NamedArguments.Select(argument => $"{argument.Key} = {ToCSharp(argument.Value)}"));
        return $"new {Fq(attributeClass)}({arguments}){(named.Length > 0 ? " { " + named + " }" : string.Empty)}";
    }

    private static string ToCSharp(TypedConstant constant)
    {
        if (constant.IsNull)
        {
            return constant.Type is null ? "null" : $"({Fq(constant.Type)})null";
        }

        if (constant.Kind == TypedConstantKind.Array)
        {
            return $"new {Fq(constant.Type!)} {{ {string.Join(", ", constant.Values.Select(ToCSharp))} }}";
        }

        if (constant.Kind == TypedConstantKind.Type && constant.Value is ITypeSymbol typeValue)
        {
            return $"typeof({Fq(typeValue)})";
        }

        if (constant.Kind == TypedConstantKind.Enum && constant.Type is not null)
        {
            return $"({Fq(constant.Type)})({SymbolDisplay.FormatPrimitive(constant.Value!, quoteStrings: true, useHexadecimalNumbers: false)})";
        }

        return constant.ToCSharpString();
    }
}

/// <summary>A controller's <c>[Route]</c> prefix.</summary>
internal sealed record RoutePrefix(string Template, int Order);

/// <summary>A route of an action; <see cref="Methods"/> is filled once combined.</summary>
internal sealed record RouteModel(string Template, List<string>? Methods, int Order, string? Name)
{
    public string ConflictKey { get; init; } = string.Empty;
}

/// <summary>Everything the table needs about one action.</summary>
internal sealed record ActionModel(
    INamedTypeSymbol Controller,
    string ControllerName,
    string ActionName,
    string MethodName,
    bool IsApi,
    bool IsConventional,
    List<RouteModel> Routes,
    List<string> TemplateLessMethods,
    List<string> Parameters,
    List<string> Filters,
    string Factory,
    string Binder,
    string Invoker,
    string DeclaredResultType,
    Location Location,
    string Display);
