using Microsoft.CodeAnalysis;

namespace Kazo.Os.Tools.BackgroundAnalyzer;

/// <summary>
/// Build errors of <c>HackBackground.RunAsync</c> (ADR 0128 section 2, ADR 0140). Every one is an
/// error: a task the build cannot prove runnable in another .NET runtime never reaches the user.
/// The OS checks the same rules again at run time, for a call made by reflection.
/// </summary>
internal static class BackgroundDiagnostics
{
    private const string Category = "Kazo.Os.Background";

    /// <summary>The task is a lambda, an anonymous method, an instance method, a non-static local function or a delegate value.</summary>
    public static readonly DiagnosticDescriptor NotStaticMethodGroup = new(
        "HOSBG0001",
        "A background task must be a static method group",
        "The background task {0} is not a static method group: it runs in another process and cannot capture the caller's objects. Pass a static method directly (MyJobs.Compress), or mark a local function 'static'.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>An input or result type is not declared by a <c>HackWireContext</c> of the app.</summary>
    public static readonly DiagnosticDescriptor UndeclaredType = new(
        "HOSBG0002",
        "A background task's type must be serializable by the app's HackWireContext",
        "The {0} type '{1}' of the background task is not in a JsonSerializerContext the assembly declares with [assembly: HackWireContext(...)]: add [JsonSerializable(typeof({1}))] to that context",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>The task's method (or task class) is in another assembly than the calling app.</summary>
    public static readonly DiagnosticDescriptor ForeignAssembly = new(
        "HOSBG0003",
        "A background task must live in the app's own assembly",
        "The background task '{0}' is in the assembly '{1}', not in '{2}': a task runs code of the calling app only",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>The task's method is generic or lives in a generic type: the task's process could not find it by name.</summary>
    public static readonly DiagnosticDescriptor GenericTask = new(
        "HOSBG0004",
        "A background task cannot be generic",
        "The background task '{0}' is generic or belongs to a generic type: the task's process finds it by name, so it must be a non-generic method of a non-generic type",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
