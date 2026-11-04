using HackerOs.AppSdk.Globalization;
using HackerOs.Platform.Core.Events;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Platform.Core.Globalization;
using HackerOs.Platform.Core.Policy;
using HackerOs.Platform.Core.Processes;
using HackerOs.Platform.Core.Time;
using HackerOs.Simulation.Abstractions.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace HackerOs.Platform.Blazor.Tests.Localization;

/// <summary>
/// Localization for component tests (ADR 0104): English texts like a fresh Kazo OS, or the
/// session language of a real <see cref="SessionLocaleService"/> set with <c>Apply</c>.
/// </summary>
internal static class TestLocalization
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Makes every injected <see cref="IStringLocalizer{T}"/> answer in English.</summary>
    public static IServiceCollection AddEnglishLocalizer(this IServiceCollection services) =>
        services.AddSingleton(typeof(IStringLocalizer<>), typeof(EnglishStringLocalizer<>));

    /// <summary>
    /// Registers <paramref name="locale"/> and binds every injected <see cref="IStringLocalizer{T}"/>
    /// to it, as <c>AddHackerOsPlatformLocalization</c> does in the hosts.
    /// </summary>
    public static IServiceCollection AddSessionLocalizer(this IServiceCollection services, SessionLocaleService locale)
    {
        services.AddSingleton(locale);
        return services.AddSingleton(typeof(IStringLocalizer<>), typeof(SessionStringLocalizer<>));
    }

    /// <summary>
    /// Creates a session language over an empty in-memory filesystem and applies
    /// <c>LANG=<paramref name="lang"/></c>, like a user whose <c>~/.config/locale.conf</c> says so.
    /// </summary>
    public static SessionLocaleService CreateSessionLocale(string lang)
    {
        int entryId = 1;
        int transactionId = 100;
        InMemoryFileSystemRepository repository = new(
            () => FileSystemEntryId.FromGuid(new Guid(entryId++, 0, 0, new byte[8])),
            () => new Guid(transactionId++, 0, 0, new byte[8]),
            new FixedTimeProvider(Now));
        FileSystemMountRouter router = new(repository);
        InMemoryTopicMessageBus topicBus = new(new CapabilityGrantRepository(() => Now));
        FileSystemService fileSystem = new(
            router, new FileSystemPathResolver(router), new FileSystemAuthorizer(), topicBus,
            () => new Guid(transactionId++, 0, 0, new byte[8]));
        ManualSimulationClock clock = new(Now, TimeSpan.FromSeconds(1));

        SessionLocaleService locale = new(new LoginEnvironmentProvider(fileSystem, clock), fileSystem, clock);
        locale.Apply(new Dictionary<string, string> { [HackLocale.LangVariable] = lang });
        return locale;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
