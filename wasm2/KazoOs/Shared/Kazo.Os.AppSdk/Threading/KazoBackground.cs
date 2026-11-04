using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Kazo.Os.AppSdk.Diagnostics;

namespace Kazo.Os.AppSdk.Threading;

/// <summary>
/// Runs heavy work off the window's thread (ADR 0128): the reverse of WinForms'
/// <c>Control.Invoke</c>. A window app runs on the main thread, which also paints the desktop; a
/// filter, a search or a compression there freezes everything. <see cref="RunAsync{TInput, TResult}(Func{TInput, KazoBackgroundContext, Task{TResult}}, TInput, IProgress{KazoBackgroundProgress}?, KazoBackgroundOptions?, CancellationToken)"/>
/// hands it to a child process in a Web Worker of the pool, and awaits its result.
/// </summary>
/// <remarks>
/// <para>
/// A worker is another .NET runtime with its own memory: a lambda that captures the window's fields
/// cannot travel there. The task is therefore a <b>static method</b> (or a
/// <see cref="KazoBackgroundJob{TInput, TResult}"/> class) of the app's assembly, and its input and
/// result are <b>serializable</b> by a <c>JsonSerializerContext</c> the app declares with
/// <see cref="KazoWireContextAttribute"/>. The Kazo OS analyzer refuses anything else at build time
/// (<c>HOSBG0001</c> and following); the OS checks again at run time.
/// </para>
/// <para>
/// Each task is a process: it appears in <c>ps</c>, <c>top</c> and the task manager under the app
/// (<c>hackpaint:Blur</c>), can be killed there without closing the window, ends when the app
/// closes, and is subject to the limits of ADR 0125. Without workers (tests, the server host), it
/// runs on the main thread with the same serialization: <see cref="KazoBackgroundContext.CheckpointAsync"/>
/// keeps the desktop alive there.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [assembly: KazoWireContext(typeof(PaintJsonContext))]
///
/// [JsonSerializable(typeof(BlurInput))]
/// [JsonSerializable(typeof(byte[]))]
/// internal sealed partial class PaintJsonContext : JsonSerializerContext;
///
/// public sealed record BlurInput(byte[] Pixels, int Width, int Height, int Radius);
///
/// public static class ImageJobs
/// {
///     public static async Task&lt;byte[]&gt; Blur(BlurInput input, KazoBackgroundContext context)
///     {
///         for (int row = 0; row &lt; input.Height; row++)
///         {
///             // ... blur one row ...
///             context.Report((row + 1d) / input.Height, "Blur");
///             await context.CheckpointAsync();
///         }
///         return input.Pixels;
///     }
/// }
///
/// byte[] blurred = await KazoBackground.RunAsync(ImageJobs.Blur, new BlurInput(pixels, w, h, 3), progress, cancellationToken: ct);
/// </code>
/// </example>
public static class KazoBackground
{
    /// <summary>Runs an asynchronous static method with a result as a background task.</summary>
    /// <typeparam name="TInput">The input type, declared by the app's <c>KazoWireContext</c>.</typeparam>
    /// <typeparam name="TResult">The result type, declared by the app's <c>KazoWireContext</c>.</typeparam>
    /// <param name="job">A static method group of the app's assembly.</param>
    /// <param name="input">The input; the task receives a copy.</param>
    /// <param name="progress">Receives the task's reports in the caller's synchronization context, or <see langword="null"/>.</param>
    /// <param name="options">Name and <c>nice</c> value, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the task (it receives the cancellation on its token).</param>
    /// <returns>The task's result (a copy: it crossed as JSON).</returns>
    /// <exception cref="ArgumentException">The task or its types break the rules of ADR 0128, section 2, or the input exceeds 16 MiB.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="KazoProcessKilledException">The task's process was killed (<c>kill -9</c>, task manager, a limit).</exception>
    /// <exception cref="KazoBackgroundJobException">An exception escaped the task.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static async Task<TResult> RunAsync<TInput, TResult>(
        Func<TInput, KazoBackgroundContext, Task<TResult>> job,
        TInput input,
        IProgress<KazoBackgroundProgress>? progress = null,
        KazoBackgroundOptions? options = null,
        CancellationToken cancellationToken = default) =>
        (TResult)(await RunCoreAsync(job, typeof(TInput), typeof(TResult), input, progress, options, cancellationToken).ConfigureAwait(true))!;

    /// <summary>Runs a synchronous static method with a result as a background task.</summary>
    /// <inheritdoc cref="RunAsync{TInput, TResult}(Func{TInput, KazoBackgroundContext, Task{TResult}}, TInput, IProgress{KazoBackgroundProgress}?, KazoBackgroundOptions?, CancellationToken)"/>
    public static async Task<TResult> RunAsync<TInput, TResult>(
        Func<TInput, KazoBackgroundContext, TResult> job,
        TInput input,
        IProgress<KazoBackgroundProgress>? progress = null,
        KazoBackgroundOptions? options = null,
        CancellationToken cancellationToken = default) =>
        (TResult)(await RunCoreAsync(job, typeof(TInput), typeof(TResult), input, progress, options, cancellationToken).ConfigureAwait(true))!;

    /// <summary>Runs an asynchronous static method without a result as a background task.</summary>
    /// <typeparam name="TInput">The input type, declared by the app's <c>KazoWireContext</c>.</typeparam>
    /// <param name="job">A static method group of the app's assembly.</param>
    /// <param name="input">The input; the task receives a copy.</param>
    /// <param name="progress">Receives the task's reports in the caller's synchronization context, or <see langword="null"/>.</param>
    /// <param name="options">Name and <c>nice</c> value, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the task.</param>
    /// <returns>A task that ends with the background task.</returns>
    /// <inheritdoc cref="RunAsync{TInput, TResult}(Func{TInput, KazoBackgroundContext, Task{TResult}}, TInput, IProgress{KazoBackgroundProgress}?, KazoBackgroundOptions?, CancellationToken)" path="/exception"/>
    public static Task RunAsync<TInput>(
        Func<TInput, KazoBackgroundContext, Task> job,
        TInput input,
        IProgress<KazoBackgroundProgress>? progress = null,
        KazoBackgroundOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(job, typeof(TInput), resultType: null, input, progress, options, cancellationToken);

    /// <summary>Runs a synchronous static method without a result as a background task.</summary>
    /// <inheritdoc cref="RunAsync{TInput}(Func{TInput, KazoBackgroundContext, Task}, TInput, IProgress{KazoBackgroundProgress}?, KazoBackgroundOptions?, CancellationToken)"/>
    public static Task RunAsync<TInput>(
        Action<TInput, KazoBackgroundContext> job,
        TInput input,
        IProgress<KazoBackgroundProgress>? progress = null,
        KazoBackgroundOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(job, typeof(TInput), resultType: null, input, progress, options, cancellationToken);

    /// <summary>Runs a <see cref="KazoBackgroundJob{TInput, TResult}"/> class as a background task: an instance is created in its process.</summary>
    /// <typeparam name="TJob">The job class, in the app's assembly.</typeparam>
    /// <typeparam name="TInput">The input type, declared by the app's <c>KazoWireContext</c>.</typeparam>
    /// <typeparam name="TResult">The result type, declared by the app's <c>KazoWireContext</c>.</typeparam>
    /// <param name="input">The input; the task receives a copy.</param>
    /// <param name="progress">Receives the task's reports in the caller's synchronization context, or <see langword="null"/>.</param>
    /// <param name="options">Name and <c>nice</c> value, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the task.</param>
    /// <returns>The task's result.</returns>
    /// <inheritdoc cref="RunAsync{TInput, TResult}(Func{TInput, KazoBackgroundContext, Task{TResult}}, TInput, IProgress{KazoBackgroundProgress}?, KazoBackgroundOptions?, CancellationToken)" path="/exception"/>
    public static async Task<TResult> RunAsync<TJob, TInput, TResult>(
        TInput input,
        IProgress<KazoBackgroundProgress>? progress = null,
        KazoBackgroundOptions? options = null,
        CancellationToken cancellationToken = default)
        where TJob : KazoBackgroundJob<TInput, TResult>, new()
    {
        Type jobType = typeof(TJob);
        if (jobType.IsGenericType)
        {
            throw new ArgumentException($"'{jobType.Name}' is generic: a background task must be found by name (ADR 0128, section 2).", nameof(TJob));
        }

        KazoBackgroundJobTarget target = KazoBackgroundJobRunner.FromJobType(jobType, typeof(TInput));
        return (TResult)(await SendAsync(target, jobType.Assembly, typeof(TInput), typeof(TResult), input, progress, options, cancellationToken)
            .ConfigureAwait(true))!;
    }

    private static async Task<object?> RunCoreAsync(
        Delegate job,
        Type inputType,
        Type? resultType,
        object? input,
        IProgress<KazoBackgroundProgress>? progress,
        KazoBackgroundOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        KazoBackgroundJobTarget target = KazoBackgroundJobRunner.FromDelegate(job, inputType);
        return await SendAsync(target, job.Method.DeclaringType!.Assembly, inputType, resultType, input, progress, options, cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>Serializes the input, hands the request to the OS, then reads the result.</summary>
    private static async Task<object?> SendAsync(
        KazoBackgroundJobTarget target,
        Assembly assembly,
        Type inputType,
        Type? resultType,
        object? input,
        IProgress<KazoBackgroundProgress>? progress,
        KazoBackgroundOptions? options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IAppExecutionContext context = KazoExecutionContext.Current;

        // ADR 0128, section 2: both types are checked before anything runs, on the main thread too.
        JsonTypeInfo inputInfo = KazoBackgroundWire.Require(inputType, assembly, "input");
        JsonTypeInfo? resultInfo = resultType is null ? null : KazoBackgroundWire.Require(resultType, assembly, "result");
        string inputJson = KazoBackgroundWire.Serialize(inputInfo, input, static detail => new ArgumentException("The input is too large: " + detail, "input"));

        KazoBackgroundJobRequest request = new(target, inputJson, options?.Name, options?.Nice ?? 0);
        string? resultJson = await context.Background
            .RunAsync(request, Relay(progress), cancellationToken)
            .ConfigureAwait(true);
        return resultInfo is null || resultJson is null ? null : KazoBackgroundWire.Deserialize(resultInfo, resultJson);
    }

    /// <summary>
    /// Delivers each report in the synchronization context of the caller (the window's renderer), as
    /// <c>Control.Invoke</c> would, whatever thread the OS received it on.
    /// </summary>
    private static Action<KazoBackgroundProgress>? Relay(IProgress<KazoBackgroundProgress>? progress)
    {
        if (progress is null)
        {
            return null;
        }

        SynchronizationContext? caller = SynchronizationContext.Current;
        return caller is null
            ? progress.Report
            : report => caller.Post(static state =>
            {
                (IProgress<KazoBackgroundProgress> target, KazoBackgroundProgress value) = ((IProgress<KazoBackgroundProgress>, KazoBackgroundProgress))state!;
                target.Report(value);
            }, (progress, report));
    }
}

/// <summary>
/// The default <see cref="IAppBackgroundGateway"/> of a context the OS did not build (an app's unit
/// test): the task runs at once on the calling thread, in the caller's process, with the same
/// serialization and the same errors as under the OS.
/// </summary>
internal sealed class InlineAppBackgroundGateway(IAppExecutionContext context) : IAppBackgroundGateway
{
    /// <inheritdoc />
    public async Task<string?> RunAsync(KazoBackgroundJobRequest request, Action<KazoBackgroundProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, request.Target.AssemblyName, StringComparison.Ordinal))
            ?? throw new ArgumentException($"The assembly '{request.Target.AssemblyName}' is not loaded.", nameof(request));
        KazoBackgroundJobRunner.PreparedJob job = KazoBackgroundJobRunner.Prepare(assembly, request.Target, request.InputJson);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, cancellationToken);
        KazoBackgroundContext jobContext = new(context.ProcessId.Value, linked.Token, progress);
        return await KazoBackgroundFaults.RunAsync(job, jobContext).ConfigureAwait(true);
    }
}

/// <summary>Maps how a task ended to what its caller sees (ADR 0128, section 4).</summary>
internal static class KazoBackgroundFaults
{
    /// <summary>
    /// Runs a prepared task: a cancellation stays one, a kill stays one, anything else that escapes
    /// becomes a <see cref="KazoBackgroundJobException"/> carrying the original's type, message and stack.
    /// </summary>
    public static async Task<string?> RunAsync(KazoBackgroundJobRunner.PreparedJob job, KazoBackgroundContext context)
    {
        try
        {
            return await job.RunAsync(context).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw ToFault(exception);
        }
    }

    /// <summary>The exception the caller of a task sees for what escaped it.</summary>
    public static KazoBackgroundJobException ToFault(Exception exception) =>
        exception as KazoBackgroundJobException
        ?? new KazoBackgroundJobException(exception.GetType().FullName ?? exception.GetType().Name, exception.Message, exception.StackTrace);
}
