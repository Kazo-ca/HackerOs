using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Sessions;

/// <summary>Default thread-safe implementation of <see cref="ILoginProgressTracker"/>.</summary>
public sealed class LoginProgressTracker : ILoginProgressTracker
{
    private readonly Lock _gate = new();
    private readonly List<string> _history = [];
    private string _currentStepName = "Initializing";
    private int _completedSteps;
    private int _totalSteps = 3;

    /// <summary>
    /// Translates a step name of <see cref="LoginProgressSnapshot"/> for the login screen (ADR 0104).
    /// Step names stay stable English identifiers (they also land in logs and tests); the screen
    /// shows them through this method in the platform's language. An unknown step is returned as is.
    /// </summary>
    /// <param name="stepName">A <see cref="LoginProgressSnapshot.CurrentStepName"/> or history entry.</param>
    /// <param name="culture">The platform's language (<c>SessionLocaleService.Culture</c>).</param>
    public static string DescribeStep(string stepName, System.Globalization.CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(stepName);
        string? key = stepName switch
        {
            "Initializing" => "LoginStep.Initializing",
            "Loading Profile" => "LoginStep.LoadingProfile",
            "Ensure data integrity" => "LoginStep.EnsureDataIntegrity",
            "Starting Session" => "LoginStep.StartingSession",
            "Session Ready" => "LoginStep.SessionReady",
            _ => null
        };
        return key is null ? stepName : Strings.For(culture)[key];
    }

    /// <inheritdoc />
    public event Action<LoginProgressSnapshot>? ProgressChanged;

    /// <inheritdoc />
    public LoginProgressSnapshot CurrentSnapshot
    {
        get
        {
            lock (_gate)
            {
                int percentage = _totalSteps > 0
                    ? Math.Min(100, (int)Math.Round((double)_completedSteps / _totalSteps * 100))
                    : 0;

                return new LoginProgressSnapshot(
                    _currentStepName,
                    _completedSteps,
                    _totalSteps,
                    percentage,
                    [.. _history]);
            }
        }
    }

    /// <inheritdoc />
    public void SetTotalSteps(int totalSteps)
    {
        lock (_gate)
        {
            _totalSteps = Math.Max(1, totalSteps);
        }

        NotifyProgressChanged();
    }

    /// <inheritdoc />
    public IDisposable BeginStep(string stepName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);

        lock (_gate)
        {
            _currentStepName = stepName;
        }

        NotifyProgressChanged();
        return new StepScope(this, stepName);
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_gate)
        {
            _currentStepName = "Initializing";
            _completedSteps = 0;
            _history.Clear();
        }

        NotifyProgressChanged();
    }

    private void CompleteStep(string stepName)
    {
        lock (_gate)
        {
            _completedSteps++;
            _history.Add(stepName);
            int percentage = (int)Math.Round((double)_completedSteps / _totalSteps * 100);
            if (percentage >= 100)
            {
                _currentStepName = "Session Ready";
            }
        }

        NotifyProgressChanged();
    }

    private void NotifyProgressChanged()
    {
        ProgressChanged?.Invoke(CurrentSnapshot);
    }

    private sealed class StepScope(LoginProgressTracker tracker, string stepName) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            tracker.CompleteStep(stepName);
        }
    }
}
