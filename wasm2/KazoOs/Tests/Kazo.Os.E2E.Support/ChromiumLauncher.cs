using Microsoft.Playwright;

namespace Kazo.Os.E2E.Support;

/// <summary>
/// Launches Chromium for the Playwright suites, falling back to a preinstalled browser when the
/// one Playwright expects is missing.
/// </summary>
/// <remarks>
/// <para>
/// Each <c>Microsoft.Playwright</c> release expects one exact Chromium revision under
/// <c>PLAYWRIGHT_BROWSERS_PATH</c> (or the Google Chrome install for <c>Channel = "chrome"</c>).
/// Cloud containers ship a single, older revision under <c>/opt/pw-browsers</c> with a stable
/// <c>/opt/pw-browsers/chromium</c> link to its binary, and must never run
/// <c>playwright install</c>. Without a fallback every suite fails before the first page loads.
/// </para>
/// <para>
/// Order: <c>KAZOOS_CHROMIUM_EXECUTABLE</c> when set (explicit choice, no retry); otherwise
/// the options as given; if that launch fails and a fallback executable exists
/// (<c>$PLAYWRIGHT_BROWSERS_PATH/chromium</c>, then <c>/opt/pw-browsers/chromium</c>), the same
/// options are retried with <see cref="BrowserTypeLaunchOptions.ExecutablePath"/> set to it and
/// <see cref="BrowserTypeLaunchOptions.Channel"/> cleared. A workstation with the expected
/// browser never reaches the fallback.
/// </para>
/// </remarks>
public static class ChromiumLauncher
{
    /// <summary>Environment variable that forces a Chromium executable for every suite.</summary>
    public const string ExecutableVariable = "KAZOOS_CHROMIUM_EXECUTABLE";

    /// <summary>The cloud-container browser link used when nothing else is configured.</summary>
    public const string ContainerExecutable = "/opt/pw-browsers/chromium";

    /// <summary>
    /// Launches Chromium with <paramref name="options"/>, retrying once on the fallback
    /// executable when the expected browser is missing. <paramref name="options"/> is updated
    /// in place when the fallback is used.
    /// </summary>
    /// <param name="playwright">The Playwright instance of the test.</param>
    /// <param name="options">Launch options; a fresh instance per call.</param>
    /// <returns>The launched browser.</returns>
    public static async Task<IBrowser> LaunchAsync(IPlaywright playwright, BrowserTypeLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(playwright);
        ArgumentNullException.ThrowIfNull(options);

        string? forced = Environment.GetEnvironmentVariable(ExecutableVariable);
        if (!string.IsNullOrWhiteSpace(forced))
        {
            options.Channel = null;
            options.ExecutablePath = forced;
            return await playwright.Chromium.LaunchAsync(options);
        }

        try
        {
            return await playwright.Chromium.LaunchAsync(options);
        }
        catch (PlaywrightException expectedMissing) when (options.ExecutablePath is null && FindFallbackExecutable() is not null)
        {
            string fallback = FindFallbackExecutable()!;
            Console.WriteLine(
                $"[chromium] expected browser unavailable ({FirstLine(expectedMissing.Message)}); using {fallback}.");
            options.Channel = null;
            options.ExecutablePath = fallback;
            try
            {
                return await playwright.Chromium.LaunchAsync(options);
            }
            catch (PlaywrightException fallbackFailure)
            {
                throw new AggregateException(
                    $"Chromium could not be launched, neither as configured nor from '{fallback}'.",
                    expectedMissing,
                    fallbackFailure);
            }
        }
    }

    /// <summary>Returns the first existing fallback executable, or <see langword="null"/>.</summary>
    public static string? FindFallbackExecutable()
    {
        string? browsersPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (!string.IsNullOrWhiteSpace(browsersPath))
        {
            string candidate = Path.Combine(browsersPath, "chromium");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return File.Exists(ContainerExecutable) ? ContainerExecutable : null;
    }

    private static string FirstLine(string message)
    {
        int end = message.IndexOf('\n');
        return end < 0 ? message : message[..end].TrimEnd();
    }
}
