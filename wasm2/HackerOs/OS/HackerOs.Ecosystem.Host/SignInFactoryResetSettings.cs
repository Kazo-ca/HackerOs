using HackerOs.Platform.Core.Power;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HackerOs.Ecosystem;

/// <summary>
/// The deployment switch of the sign-in screen's « Forgot password? » (ADR 0226):
/// <c>HackerOs:SignIn:FactoryReset:Enabled</c> in <c>wwwroot/appsettings.json</c>.
/// </summary>
/// <param name="Enabled">
/// Whether the sign-in screen offers to erase the machine when its password is forgotten.
/// <see langword="false"/> hides the link: only an administrator's reset (Settings ▸ System) is left.
/// </param>
public sealed record SignInFactoryResetSettings(bool Enabled)
{
    /// <summary>Configuration section read by <see cref="Read"/>.</summary>
    public const string SectionName = "HackerOs:SignIn:FactoryReset";

    /// <summary>On, the default of every host (ADR 0226, question 2).</summary>
    public static SignInFactoryResetSettings Default { get; } = new(true);

    /// <summary>Reads <c>HackerOs:SignIn:FactoryReset:Enabled</c>; absent or unreadable reads as <see langword="true"/>.</summary>
    /// <param name="configuration">The host configuration; <see langword="null"/> reads as an absent section.</param>
    /// <returns>The settings.</returns>
    public static SignInFactoryResetSettings Read(IConfiguration? configuration)
    {
        string? enabled = configuration?.GetSection(SectionName)["Enabled"];
        return bool.TryParse(enabled, out bool value) ? new SignInFactoryResetSettings(value) : Default;
    }
}

/// <summary>
/// Runs the reset asked from the sign-in screen (ADR 0226): the same erase and restart as
/// Settings ▸ System ▸ Reset (ADR 0056, section 11), without an administrator.
/// </summary>
public static class SignInFactoryReset
{
    /// <summary>Whether the host's deployment offers the reset; a host that registered nothing offers it.</summary>
    /// <param name="services">The host's services.</param>
    public static bool IsOffered(IServiceProvider services) =>
        (services.GetService<SignInFactoryResetSettings>() ?? SignInFactoryResetSettings.Default).Enabled;

    /// <summary>
    /// Erases the OS storage and restarts the machine. Returns only when no power device could do it
    /// (a host without one), and then nothing was erased.
    /// </summary>
    /// <param name="services">The host's services: the power service, or at least the power device.</param>
    public static async Task RunAsync(IServiceProvider services)
    {
        // Checked again here, not only by the markup: a disabled deployment never erases.
        if (!IsOffered(services))
        {
            return;
        }

        if (services.GetService<SystemPowerService>() is { } power)
        {
            // Flushes the logs, ends what is left of a session, deletes the database, reloads.
            await power.FactoryResetFromSignInAsync();
        }
        else if (services.GetService<ISystemPowerDevice>() is { } device)
        {
            await device.EraseStorageAsync();
            await device.RestartAsync();
        }
    }
}
