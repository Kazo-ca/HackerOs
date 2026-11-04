using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Sessions;

public sealed class LoginProgressTrackerTests
{
    [Fact]
    public void Disposable_step_scope_updates_progress_snapshot_and_history()
    {
        LoginProgressTracker tracker = new();
        List<LoginProgressSnapshot> events = [];
        tracker.ProgressChanged += events.Add;

        tracker.SetTotalSteps(3);

        using (tracker.BeginStep("Loading Profile"))
        {
            Assert.Equal("Loading Profile", tracker.CurrentSnapshot.CurrentStepName);
            Assert.Equal(0, tracker.CurrentSnapshot.CompletedSteps);
            Assert.Equal(0, tracker.CurrentSnapshot.Percentage);
        }

        Assert.Equal(1, tracker.CurrentSnapshot.CompletedSteps);
        Assert.Equal(33, tracker.CurrentSnapshot.Percentage);
        Assert.Equal(["Loading Profile"], tracker.CurrentSnapshot.StepHistory);

        using (tracker.BeginStep("Ensure data integrity"))
        {
            Assert.Equal("Ensure data integrity", tracker.CurrentSnapshot.CurrentStepName);
        }

        Assert.Equal(2, tracker.CurrentSnapshot.CompletedSteps);
        Assert.Equal(67, tracker.CurrentSnapshot.Percentage);
        Assert.Equal(["Loading Profile", "Ensure data integrity"], tracker.CurrentSnapshot.StepHistory);

        using (tracker.BeginStep("Starting Session"))
        {
        }

        Assert.Equal(3, tracker.CurrentSnapshot.CompletedSteps);
        Assert.Equal(100, tracker.CurrentSnapshot.Percentage);
        Assert.Equal("Session Ready", tracker.CurrentSnapshot.CurrentStepName);
        Assert.Equal(["Loading Profile", "Ensure data integrity", "Starting Session"], tracker.CurrentSnapshot.StepHistory);
        Assert.NotEmpty(events);
    }

    [Fact]
    public void Reset_clears_snapshot_history()
    {
        LoginProgressTracker tracker = new();
        using (tracker.BeginStep("Step 1"))
        {
        }

        Assert.NotEmpty(tracker.CurrentSnapshot.StepHistory);

        tracker.Reset();

        Assert.Equal(0, tracker.CurrentSnapshot.CompletedSteps);
        Assert.Empty(tracker.CurrentSnapshot.StepHistory);
        Assert.Equal("Initializing", tracker.CurrentSnapshot.CurrentStepName);
    }
}
