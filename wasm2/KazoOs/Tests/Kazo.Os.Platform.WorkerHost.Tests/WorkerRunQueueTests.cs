using Kazo.Os.Platform.WorkerHost.Host;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>ADR 0127 §1: niceness only orders the commands waiting for a worker.</summary>
public sealed class WorkerRunQueueTests
{
    [Fact]
    public void Commands_of_equal_niceness_are_served_in_arrival_order()
    {
        WorkerRunQueue queue = new();
        WorkerRunQueue.Entry first = queue.Enqueue(0);
        WorkerRunQueue.Entry second = queue.Enqueue(0);

        Assert.True(queue.IsHead(first));
        Assert.False(queue.IsHead(second));

        queue.Remove(first);
        Assert.True(queue.IsHead(second));
    }

    [Fact]
    public void The_lowest_niceness_is_served_first_whatever_its_arrival()
    {
        WorkerRunQueue queue = new();
        WorkerRunQueue.Entry background = queue.Enqueue(10);
        WorkerRunQueue.Entry normal = queue.Enqueue(0);
        WorkerRunQueue.Entry urgent = queue.Enqueue(-5);

        Assert.True(queue.IsHead(urgent));
        queue.Remove(urgent);
        Assert.True(queue.IsHead(normal));
        queue.Remove(normal);
        Assert.True(queue.IsHead(background));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Niceness_is_clamped_when_queued()
    {
        WorkerRunQueue queue = new();

        Assert.Equal(19, queue.Enqueue(50).Nice);
        Assert.Equal(-20, queue.Enqueue(-50).Nice);
    }

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(-5, true, true)]
    [InlineData(5, false, true)]
    [InlineData(5, true, false)]
    public void A_positive_niceness_leaves_the_ready_worker_while_another_worker_warms_up(int nice, bool warming, bool expected)
    {
        Assert.Equal(expected, WorkerRunQueue.MayTakeReadyWorker(nice, warming));
    }
}
