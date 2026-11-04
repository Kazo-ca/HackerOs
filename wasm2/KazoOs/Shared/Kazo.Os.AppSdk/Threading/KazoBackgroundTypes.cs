using System.Globalization;

namespace Kazo.Os.AppSdk.Threading;

/// <summary>
/// One progress report of a background task (ADR 0128, section 4), delivered to the caller's
/// <see cref="IProgress{T}"/> in its synchronization context, at most 10 times a second.
/// </summary>
/// <param name="Fraction">Done so far, from <c>0</c> to <c>1</c>.</param>
/// <param name="Message">What the task is doing, or <see langword="null"/>.</param>
public readonly record struct KazoBackgroundProgress(double Fraction, string? Message = null)
{
    /// <inheritdoc />
    public override string ToString()
    {
        // "42%" as progress bars and wget print it, whatever the culture.
        string percent = (Fraction * 100d).ToString("0", CultureInfo.InvariantCulture) + "%";
        return Message is null ? percent : $"{percent} {Message}";
    }
}

/// <summary>Options of one <see cref="KazoBackground"/> task (ADR 0128, section 1).</summary>
public sealed record KazoBackgroundOptions
{
    /// <summary>
    /// Gets the task's name, shown after the app's command in <c>ps</c>, <c>top</c> and the task
    /// manager (<c>hackpaint:blur</c>); the method or class name when <see langword="null"/>.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the task's <c>nice</c> value, from <c>-20</c> (first) to <c>19</c> (last), like
    /// <c>nice(1)</c> (ADR 0127); <c>0</c> by default. A value below <c>0</c> is raised to <c>0</c>
    /// for a user that is not an administrator, as Linux refuses a negative nice to a regular user.
    /// </summary>
    public int Nice { get; init; }
}

/// <summary>
/// A background task written as a class (ADR 0128, section 1): an instance is created in the process
/// that runs it (a Web Worker, or the main thread), so it never shares fields with the window that
/// started it.
/// </summary>
/// <typeparam name="TInput">The data handed to the task, serializable by the app's <c>KazoWireContext</c>.</typeparam>
/// <typeparam name="TResult">The result, serializable by the app's <c>KazoWireContext</c>.</typeparam>
/// <example>
/// <code>
/// public sealed class CountWordsJob : KazoBackgroundJob&lt;string, int&gt;
/// {
///     public override Task&lt;int&gt; RunAsync(string text, KazoBackgroundContext context) =&gt;
///         Task.FromResult(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
/// }
///
/// int words = await KazoBackground.RunAsync&lt;CountWordsJob, string, int&gt;(text);
/// </code>
/// </example>
public abstract class KazoBackgroundJob<TInput, TResult> : IHackBackgroundJobInvoker
{
    /// <summary>Runs the task.</summary>
    /// <param name="input">The data the caller handed over (a copy: it crossed as JSON).</param>
    /// <param name="context">Cancellation, progress and checkpoints of the task's process.</param>
    /// <returns>The result, sent back to the caller as JSON.</returns>
    public abstract Task<TResult> RunAsync(TInput input, KazoBackgroundContext context);

    Type IHackBackgroundJobInvoker.InputType => typeof(TInput);

    Type IHackBackgroundJobInvoker.ResultType => typeof(TResult);

    async Task<object?> IHackBackgroundJobInvoker.RunAsync(object? input, KazoBackgroundContext context) =>
        await RunAsync((TInput)input!, context).ConfigureAwait(true);
}

/// <summary>
/// The untyped bridge of <see cref="KazoBackgroundJob{TInput, TResult}"/>: the process that runs a task
/// only knows its class by name, so it calls it through this interface, without reflection on
/// <see cref="Task{TResult}"/>.
/// </summary>
internal interface IHackBackgroundJobInvoker
{
    Type InputType { get; }

    Type ResultType { get; }

    Task<object?> RunAsync(object? input, KazoBackgroundContext context);
}

/// <summary>
/// An exception escaped a background task (ADR 0128, section 4). The original exception object lived
/// in another process, possibly another .NET runtime: what crosses is its type, message and stack.
/// </summary>
public sealed class KazoBackgroundJobException : Exception
{
    /// <summary>Initializes the exception from what the task's process reported.</summary>
    /// <param name="originalTypeName">Full name of the exception type thrown by the task.</param>
    /// <param name="message">Its message.</param>
    /// <param name="originalStackTrace">Its stack trace, or <see langword="null"/>.</param>
    public KazoBackgroundJobException(string originalTypeName, string message, string? originalStackTrace)
        : base(message)
    {
        OriginalTypeName = originalTypeName ?? throw new ArgumentNullException(nameof(originalTypeName));
        OriginalStackTrace = originalStackTrace;
    }

    /// <summary>Gets the full name of the exception type the task threw (<c>System.IO.InvalidDataException</c>).</summary>
    public string OriginalTypeName { get; }

    /// <summary>Gets the stack trace of the original exception, in the task's process.</summary>
    public string? OriginalStackTrace { get; }

    /// <inheritdoc />
    public override string ToString() =>
        OriginalStackTrace is null
            ? $"{GetType().FullName}: {OriginalTypeName}: {Message}"
            : $"{GetType().FullName}: {OriginalTypeName}: {Message}{System.Environment.NewLine}{OriginalStackTrace}";
}
