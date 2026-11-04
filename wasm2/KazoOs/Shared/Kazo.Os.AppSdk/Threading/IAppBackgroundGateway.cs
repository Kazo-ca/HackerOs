namespace Kazo.Os.AppSdk.Threading;

/// <summary>
/// One app instance's access to background tasks (ADR 0128), behind <see cref="KazoBackground"/>.
/// The OS runs each request as a child process of the caller, in a Web Worker of the pool when the
/// host has some, otherwise on the main thread, with the same serialization in both cases.
/// </summary>
/// <remarks>
/// The request names the task (assembly, type, method) and carries its input as JSON: the process that
/// runs it may be another .NET runtime, which only shares the app's code, not its objects.
/// </remarks>
public interface IAppBackgroundGateway
{
    /// <summary>Runs one task until it ends and returns its result as JSON.</summary>
    /// <param name="request">What to run, with what, under which name.</param>
    /// <param name="progress">Receives the task's reports (already throttled), or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the task (its token, then its process).</param>
    /// <returns>The result as JSON, or <see langword="null"/> for a task without a result.</returns>
    /// <exception cref="ArgumentException">The task or its data break the rules of ADR 0128, section 2.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled or the task honored a stop request.</exception>
    /// <exception cref="Diagnostics.KazoProcessKilledException">The task's process was killed.</exception>
    /// <exception cref="KazoBackgroundJobException">An exception escaped the task.</exception>
    Task<string?> RunAsync(KazoBackgroundJobRequest request, Action<KazoBackgroundProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Names one background task (ADR 0128, section 2): a static method or a job class of the app.</summary>
/// <param name="AssemblyName">Simple name of the assembly that declares it (the app's own).</param>
/// <param name="TypeName">Full name of the declaring type (with <c>+</c> for a nested type), or of the job class.</param>
/// <param name="MethodName">The static method, or <see langword="null"/> for a <see cref="KazoBackgroundJob{TInput, TResult}"/> class.</param>
/// <param name="InputTypeName">Full name of the input type, which tells overloads of <paramref name="MethodName"/> apart.</param>
public sealed record KazoBackgroundJobTarget(string AssemblyName, string TypeName, string? MethodName, string InputTypeName)
{
    /// <summary>Gets the name shown after the app's command when the caller gave none: the method, or the class.</summary>
    public string DisplayName => MethodName ?? TypeName[(Math.Max(TypeName.LastIndexOf('.'), TypeName.LastIndexOf('+')) + 1)..];
}

/// <summary>One request to <see cref="IAppBackgroundGateway.RunAsync"/>.</summary>
/// <param name="Target">The task.</param>
/// <param name="InputJson">Its input, serialized by the app's <c>KazoWireContext</c>.</param>
/// <param name="Name">Its name in <c>ps</c>, or <see langword="null"/> for <see cref="KazoBackgroundJobTarget.DisplayName"/>.</param>
/// <param name="Nice">Its <c>nice</c> value (ADR 0127).</param>
public sealed record KazoBackgroundJobRequest(KazoBackgroundJobTarget Target, string InputJson, string? Name = null, int Nice = 0);
