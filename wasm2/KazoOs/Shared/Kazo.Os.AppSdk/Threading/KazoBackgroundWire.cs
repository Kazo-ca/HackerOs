using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Kazo.Os.AppSdk.Threading;

/// <summary>
/// The data of a background task crosses as JSON written by the app's own
/// <see cref="KazoWireContextAttribute"/> contexts (ADR 0128, section 2; ADR 0124, section 2), on the
/// main thread as in a worker: an app that works in its tests works in a worker.
/// </summary>
internal static class KazoBackgroundWire
{
    /// <summary>The largest input or result: the largest file (16 MiB, ADR 0094), counted in UTF-8 bytes of JSON.</summary>
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    private static readonly ConcurrentDictionary<Type, JsonSerializerContext?> s_contexts = new();

    /// <summary>Finds the metadata of <paramref name="type"/> in a context declared by <paramref name="assembly"/>.</summary>
    /// <returns>The metadata, or <see langword="null"/> when no declared context knows the type.</returns>
    public static JsonTypeInfo? Find(Type type, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (KazoWireContextAttribute declared in assembly.GetCustomAttributes<KazoWireContextAttribute>())
        {
            if (Context(declared) is { } context && context.GetTypeInfo(type) is { } info)
            {
                return info;
            }
        }

        return null;
    }

    /// <summary>Like <see cref="Find"/>, but a type no context declares is the caller's error (ADR 0128, section 2).</summary>
    /// <param name="type">The input or result type.</param>
    /// <param name="assembly">The assembly of the task.</param>
    /// <param name="role"><c>input</c> or <c>result</c>, for the message.</param>
    /// <exception cref="ArgumentException">No <see cref="KazoWireContextAttribute"/> context of the assembly declares the type.</exception>
    public static JsonTypeInfo Require(Type type, Assembly assembly, string role) =>
        Find(type, assembly) ?? throw new ArgumentException(
            $"The {role} type '{type.FullName}' of a background task cannot cross to its process: no JsonSerializerContext " +
            $"named by [assembly: KazoWireContext(...)] in '{assembly.GetName().Name}' declares it. Add " +
            $"[JsonSerializable(typeof({type.Name}))] to the app's context (ADR 0128, section 2).",
            role);

    /// <summary>Writes a value, refusing one above <see cref="MaxPayloadBytes"/>.</summary>
    /// <param name="info">Its metadata.</param>
    /// <param name="value">The value.</param>
    /// <param name="tooLarge">Builds the exception for a value too large, from its size.</param>
    public static string Serialize(JsonTypeInfo info, object? value, Func<string, Exception> tooLarge)
    {
        string json = JsonSerializer.Serialize(value, info);
        int bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxPayloadBytes)
        {
            throw tooLarge(
                $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes of JSON, more than the 16 MiB a background task may receive " +
                "or return (ADR 0128, section 4): pass a file path and read the file in the task instead.");
        }

        return json;
    }

    /// <summary>Rebuilds a value written by <see cref="Serialize"/>.</summary>
    public static object? Deserialize(JsonTypeInfo info, string json) => JsonSerializer.Deserialize(json, info);

    private static JsonSerializerContext? Context(KazoWireContextAttribute declared) =>
        s_contexts.GetOrAdd(declared.ContextType, _ =>
            typeof(JsonSerializerContext).IsAssignableFrom(declared.ContextType) && !declared.ContextType.IsAbstract
                ? Activator.CreateInstance(declared.ContextType) as JsonSerializerContext
                : null);
}

/// <summary>
/// Resolves and runs a <see cref="KazoBackgroundJobTarget"/> in the process that runs the task: on
/// the OS's main thread, or in a Web Worker that loaded the app's assembly (ADR 0128, sections 3 and 5).
/// </summary>
internal static class KazoBackgroundJobRunner
{
    /// <summary>Builds the target of a static method group, checking the rules of ADR 0128, section 2.</summary>
    /// <param name="job">The delegate the app passed.</param>
    /// <param name="inputType">The task's input type.</param>
    /// <exception cref="ArgumentException">The delegate is not a static method of a non-generic type.</exception>
    public static KazoBackgroundJobTarget FromDelegate(Delegate job, Type inputType)
    {
        ArgumentNullException.ThrowIfNull(job);
        MethodInfo method = job.Method;
        if (job.Target is not null || !method.IsStatic)
        {
            throw new ArgumentException(
                $"'{method.Name}' is not a static method: a background task runs in another process and cannot capture the " +
                "caller's objects. Pass a static method group (MyJobs.Compress), not a lambda or an instance method (ADR 0128, section 2).",
                nameof(job));
        }

        if (method.DeclaringType is not { } declaring || declaring.ContainsGenericParameters || declaring.IsGenericType || method.IsGenericMethod)
        {
            throw new ArgumentException(
                $"'{method.Name}' must be a non-generic static method of a non-generic type, so the task's process can find it by name (ADR 0128, section 2).",
                nameof(job));
        }

        return new KazoBackgroundJobTarget(
            declaring.Assembly.GetName().Name!, declaring.FullName!, method.Name, inputType.FullName ?? inputType.Name);
    }

    /// <summary>Builds the target of a job class.</summary>
    public static KazoBackgroundJobTarget FromJobType(Type jobType, Type inputType) =>
        new(jobType.Assembly.GetName().Name!, jobType.FullName!, MethodName: null, inputType.FullName ?? inputType.Name);

    /// <summary>
    /// Prepares the task: finds it and reads its input. Every error here is the caller's
    /// (<see cref="ArgumentException"/>), reported as such, not as a fault of the task.
    /// </summary>
    /// <param name="assembly">The app's assembly, already loaded in this runtime.</param>
    /// <param name="target">The task.</param>
    /// <param name="inputJson">Its input.</param>
    /// <exception cref="ArgumentException">The target does not name a valid task of <paramref name="assembly"/>, or the input does not read.</exception>
    public static PreparedJob Prepare(Assembly assembly, KazoBackgroundJobTarget target, string inputJson)
    {
        Binding binding = Bind(assembly, target);
        JsonTypeInfo inputInfo = KazoBackgroundWire.Require(binding.InputType, assembly, "input");
        JsonTypeInfo? resultInfo = binding.ResultType is { } resultType ? KazoBackgroundWire.Require(resultType, assembly, "result") : null;
        object? input;
        try
        {
            input = KazoBackgroundWire.Deserialize(inputInfo, inputJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"The input of '{target.DisplayName}' does not read as '{binding.InputType.FullName}': {exception.Message}", nameof(inputJson), exception);
        }

        return new PreparedJob(binding, input, resultInfo);
    }

    /// <summary>A task found, with its input read, ready to run.</summary>
    internal sealed class PreparedJob(Binding binding, object? input, JsonTypeInfo? resultInfo)
    {
        /// <summary>
        /// Runs the task and writes its result. Exceptions of the task escape as they are; the host
        /// reports them (ADR 0128, section 4). The context is closed (last report sent) first.
        /// </summary>
        /// <returns>The result as JSON, or <see langword="null"/> for a task without a result.</returns>
        public async Task<string?> RunAsync(KazoBackgroundContext context)
        {
            object? result;
            try
            {
                result = await binding.InvokeAsync(input, context).ConfigureAwait(true);
            }
            finally
            {
                context.Close();
            }

            return resultInfo is null
                ? null
                : KazoBackgroundWire.Serialize(resultInfo, result, static detail => new InvalidOperationException("The result is too large: " + detail));
        }
    }

    /// <summary>Finds what <paramref name="target"/> names (ADR 0128, section 2: the OS checks again at run time).</summary>
    /// <exception cref="ArgumentException">Nothing valid is found.</exception>
    [UnconditionalSuppressMessage(
        "Trimming", "IL2026",
        Justification = "The task lives in an app assembly the host lists explicitly; a trimmed host keeps such an assembly whole (ADR 0113).")]
    [UnconditionalSuppressMessage(
        "Trimming", "IL2072",
        Justification = "Same: the job class is a type of a whole-kept app assembly, with the public parameterless constructor new() requires.")]
    [UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification = "Same: the methods of a type of a whole-kept app assembly are all there.")]
    internal static Binding Bind(Assembly assembly, KazoBackgroundJobTarget target)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(target);
        if (!string.Equals(assembly.GetName().Name, target.AssemblyName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The background task '{target.TypeName}' is in '{target.AssemblyName}', not in the app's assembly " +
                $"'{assembly.GetName().Name}' (ADR 0128, section 2).", nameof(target));
        }

        Type type = assembly.GetType(target.TypeName, throwOnError: false)
            ?? throw new ArgumentException($"'{target.TypeName}' is not a type of '{target.AssemblyName}'.", nameof(target));
        if (type.IsGenericType)
        {
            throw new ArgumentException($"'{target.TypeName}' is generic: a background task must be found by name (ADR 0128, section 2).", nameof(target));
        }

        if (target.MethodName is null)
        {
            if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null
                || Activator.CreateInstance(type) is not IHackBackgroundJobInvoker job)
            {
                throw new ArgumentException(
                    $"'{target.TypeName}' is not a non-abstract KazoBackgroundJob<TInput, TResult> with a public parameterless constructor.",
                    nameof(target));
            }

            return new Binding(job.InputType, job.ResultType, (input, context) => job.RunAsync(input, context));
        }

        MethodInfo[] candidates =
        [
            .. type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(method => string.Equals(method.Name, target.MethodName, StringComparison.Ordinal)
                    && !method.IsGenericMethodDefinition
                    && method.GetParameters() is [{ } first, { } second]
                    && second.ParameterType == typeof(KazoBackgroundContext)
                    && string.Equals(first.ParameterType.FullName ?? first.ParameterType.Name, target.InputTypeName, StringComparison.Ordinal))
        ];
        if (candidates is not [MethodInfo method])
        {
            throw new ArgumentException(
                $"'{target.TypeName}.{target.MethodName}' is not a static method ({target.InputTypeName}, KazoBackgroundContext) of " +
                $"'{target.AssemblyName}': a background task must be a static method group, not a lambda or an instance method (ADR 0128, section 2).",
                nameof(target));
        }

        return BindMethod(method);
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(Task<>))]
    private static Binding BindMethod(MethodInfo method)
    {
        Type returnType = method.ReturnType;
        Type? resultType;
        bool awaits;
        if (returnType == typeof(void) || returnType == typeof(Task))
        {
            resultType = null;
            awaits = returnType == typeof(Task);
        }
        else if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            resultType = returnType.GetGenericArguments()[0];
            awaits = true;
        }
        else
        {
            resultType = returnType;
            awaits = false;
        }

        Type inputType = method.GetParameters()[0].ParameterType;
        return new Binding(inputType, resultType, async (input, context) =>
        {
            object? returned = method.Invoke(null, BindingFlags.DoNotWrapExceptions, binder: null, [input, context], culture: null);
            if (!awaits)
            {
                return returned;
            }

            Task task = (Task?)returned ?? throw new InvalidOperationException($"'{method.Name}' returned a null task.");
            await task.ConfigureAwait(true);
            return resultType is null ? null : ResultOf(task);
        });
    }

    [UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification = "Task<T>.Result is kept by the DynamicDependency on BindMethod.")]
    private static object? ResultOf(Task task) => task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task);

    /// <summary>What a target resolved to: its types and how to call it.</summary>
    internal sealed record Binding(Type InputType, Type? ResultType, Func<object?, KazoBackgroundContext, Task<object?>> InvokeAsync);
}
