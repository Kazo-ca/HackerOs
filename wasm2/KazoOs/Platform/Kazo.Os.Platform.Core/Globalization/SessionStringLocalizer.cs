using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Platform.Core.Globalization;

/// <summary>
/// The <see cref="IStringLocalizer{T}"/> platform components receive through dependency injection
/// (<c>@inject IStringLocalizer&lt;T&gt;</c>): <typeparamref name="TResource"/>'s <c>.resx</c> texts in
/// the session language (<see cref="SessionLocaleService.Culture"/>), read again at every lookup.
/// </summary>
/// <typeparam name="TResource">The type naming the resources (<c>Resources/&lt;Type&gt;.resx</c>).</typeparam>
public sealed class SessionStringLocalizer<TResource> : KazoStringLocalizer, IStringLocalizer<TResource>
{
    /// <summary>Creates the localizer.</summary>
    /// <param name="locale">The session language.</param>
    public SessionStringLocalizer(SessionLocaleService locale)
        : base(KazoLocalization.GetResourceManager(typeof(TResource)), () => locale.Culture)
    {
        ArgumentNullException.ThrowIfNull(locale);
    }
}

/// <summary>Registers the platform side of ADR 0104.</summary>
public static class PlatformLocalizationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SessionLocaleService"/> and makes every injected
    /// <see cref="IStringLocalizer{T}"/> a <see cref="SessionStringLocalizer{TResource}"/>. Apps never
    /// use these: they ask their own context (<c>AppContext.GetLocalizer&lt;T&gt;()</c>).
    /// </summary>
    /// <param name="services">The host's services; <see cref="Processes.LoginEnvironmentProvider"/> must be registered.</param>
    public static IServiceCollection AddHackerOsPlatformLocalization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<SessionLocaleService>();
        // Replace, not TryAdd: a library calling AddLocalization() must not put the tab-wide
        // CurrentUICulture back in charge of the platform's texts.
        services.Replace(ServiceDescriptor.Scoped(typeof(IStringLocalizer<>), typeof(SessionStringLocalizer<>)));
        return services;
    }
}
