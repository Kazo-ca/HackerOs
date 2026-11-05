using System.Text.Json.Serialization;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Tests.IO;
using Kazo.Os.AppSdk.Tests.Threading;
using Kazo.Os.AppSdk.Threading;
using Kazo.Os.Tests.Support;

[assembly: KazoWireContext(typeof(BackgroundTestJsonContext))]

namespace Kazo.Os.AppSdk.Tests.Threading;

/// <summary>A test input.</summary>
public sealed record Numbers(int[] Values);

/// <summary>A test result.</summary>
public sealed record Stats(int Sum, int Count);

/// <summary>A type no context declares.</summary>
public sealed record NotDeclared(int N);

[JsonSerializable(typeof(Numbers))]
[JsonSerializable(typeof(Stats))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
internal sealed partial class BackgroundTestJsonContext : JsonSerializerContext;

/// <summary>
/// ADR 0128: the SDK side of <see cref="KazoBackground"/>. A context the OS did not build runs the
/// task inline, with the same serialization and the same errors as under the OS.
/// </summary>
public sealed class KazoBackgroundTests
{
    [Fact]
    public async Task A_static_method_runs_and_its_result_crosses_as_json()
    {
        using IDisposable scope = KazoExecutionContext.Enter(KazoTestHost.CreateContext(new FakeAppFileSystemGateway()));

        Stats stats = await KazoBackground.RunAsync<Numbers, Stats>(Compute, new Numbers([1, 2, 3, 4]));

        Assert.Equal(new Stats(10, 4), stats);
    }

    [Fact]
    public async Task A_task_class_runs_like_a_static_method()
    {
        using IDisposable scope = KazoExecutionContext.Enter(KazoTestHost.CreateContext(new FakeAppFileSystemGateway()));

        int length = await KazoBackground.RunAsync<LengthJob, string, int>("abcdef");

        Assert.Equal(6, length);
    }

    [Fact]
    public async Task A_lambda_is_refused_even_without_captures()
    {
        using IDisposable scope = KazoExecutionContext.Enter(KazoTestHost.CreateContext(new FakeAppFileSystemGateway()));
        int offset = 3;

        ArgumentException capturing = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => KazoBackground.RunAsync<int, int>((value, _) => value + offset, 1));
        ArgumentException plain = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => KazoBackground.RunAsync<int, int>(static (value, _) => value + 1, 1));

        Assert.Contains("static method", capturing.Message, StringComparison.Ordinal);
        Assert.Contains("static method", plain.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_undeclared_type_is_refused_before_the_task_runs()
    {
        using IDisposable scope = KazoExecutionContext.Enter(KazoTestHost.CreateContext(new FakeAppFileSystemGateway()));
        s_ranUndeclared = false;

        ArgumentException refused = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => KazoBackground.RunAsync<NotDeclared, int>(Undeclared, new NotDeclared(1)));

        Assert.Contains("JsonSerializable", refused.Message, StringComparison.Ordinal);
        Assert.False(s_ranUndeclared);
    }

    [Fact]
    public async Task An_exception_of_the_task_becomes_a_background_job_exception()
    {
        using IDisposable scope = KazoExecutionContext.Enter(KazoTestHost.CreateContext(new FakeAppFileSystemGateway()));

        KazoBackgroundJobException fault = await Assert.ThrowsAsync<KazoBackgroundJobException>(
            () => KazoBackground.RunAsync<int>(Explode, 1));

        Assert.Equal(typeof(FormatException).FullName, fault.OriginalTypeName);
        Assert.Equal("bad", fault.Message);
        Assert.Contains(nameof(Explode), fault.OriginalStackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_the_token_cancels_the_task()
    {
        using IDisposable scope = KazoExecutionContext.Enter(KazoTestHost.CreateContext(new FakeAppFileSystemGateway()));
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => KazoBackground.RunAsync<int>(Forever, 0, cancellationToken: cancel.Token));
    }

    [Fact]
    public void Reports_closer_than_100_ms_are_merged_and_the_last_one_is_delivered_at_the_end()
    {
        List<KazoBackgroundProgress> received = [];
        KazoBackgroundContext context = new(7, CancellationToken.None, report => { lock (received) { received.Add(report); } });

        for (int i = 1; i <= 1000; i++)
        {
            context.Report(i / 1000d, i == 1000 ? "done" : null);
        }

        context.Close();

        lock (received)
        {
            Assert.InRange(received.Count, 2, 5);
            Assert.Equal(new KazoBackgroundProgress(0.001), received[0]);
            Assert.Equal(new KazoBackgroundProgress(1, "done"), received[^1]);
        }
    }

    [Fact]
    public void A_report_is_clamped_and_not_a_number_is_refused()
    {
        List<KazoBackgroundProgress> received = [];
        KazoBackgroundContext context = new(7, CancellationToken.None, received.Add);

        context.Report(4);

        Assert.Equal(1, received.Single().Fraction);
        Assert.Throws<ArgumentOutOfRangeException>(() => context.Report(double.NaN));
        Assert.Equal("50% copying", new KazoBackgroundProgress(0.5, "copying").ToString());
    }

    private static volatile bool s_ranUndeclared;

    private static Stats Compute(Numbers input, KazoBackgroundContext context) => new(input.Values.Sum(), input.Values.Length);

    private static int Undeclared(NotDeclared input, KazoBackgroundContext context)
    {
        s_ranUndeclared = true;
        return input.N;
    }

    private static void Explode(int input, KazoBackgroundContext context) => throw new FormatException("bad");

    private static async Task Forever(int input, KazoBackgroundContext context)
    {
        while (true)
        {
            await Task.Delay(5, context.CancellationToken);
        }
    }

    private sealed class LengthJob : KazoBackgroundJob<string, int>
    {
        public override Task<int> RunAsync(string input, KazoBackgroundContext context) => Task.FromResult(input.Length);
    }
}
