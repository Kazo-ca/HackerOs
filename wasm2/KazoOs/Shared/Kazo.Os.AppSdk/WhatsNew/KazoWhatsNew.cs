using System.Globalization;

namespace Kazo.Os.AppSdk.WhatsNew;

/// <summary>
/// What changed in Kazo OS since the user's last visit (ADR 0186): the change list grouped by
/// version, with the changes the session user has not seen yet. Settings ▸ What's new shows it.
/// </summary>
/// <example>
/// <code>
/// KazoChangeList list = await KazoWhatsNew.GetAsync();
/// // … show it, then when the page closes:
/// await KazoWhatsNew.MarkSeenAsync(list.Groups.SelectMany(g => g.Entries).Where(e => e.IsNew).Select(e => e.Id).ToList());
/// </code>
/// </example>
public static class KazoWhatsNew
{
    /// <summary>Gets whether the host serves a change list.</summary>
    public static bool IsSupported => IsSupportedFor(KazoExecutionContext.Current);

    /// <summary>Gets whether the host of <paramref name="context"/> serves a change list.</summary>
    /// <param name="context">The app's execution context.</param>
    public static bool IsSupportedFor(IAppExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.WhatsNew.IsSupported;
    }

    /// <summary>Reads the change list in the app's language (<see cref="IAppExecutionContext.Culture"/>, ADR 0104).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The list; <see cref="KazoChangeList.Empty"/> when the host has none or cannot read it.</returns>
    public static Task<KazoChangeList> GetAsync(CancellationToken cancellationToken = default) =>
        GetAsync(KazoExecutionContext.Current, cancellationToken);

    /// <inheritdoc cref="GetAsync(CancellationToken)"/>
    /// <param name="context">The app's execution context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<KazoChangeList> GetAsync(IAppExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.WhatsNew.IsSupported
            ? await context.WhatsNew.GetAsync(context.Culture, cancellationToken).ConfigureAwait(false)
            : KazoChangeList.Empty;
    }

    /// <summary>Marks changes as seen: they lose their <b>New</b> badge, here and in the Start menu.</summary>
    /// <param name="ids">The ids of the changes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static Task MarkSeenAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default) =>
        MarkSeenAsync(KazoExecutionContext.Current, ids, cancellationToken);

    /// <inheritdoc cref="MarkSeenAsync(IReadOnlyCollection{string}, CancellationToken)"/>
    /// <param name="context">The app's execution context.</param>
    /// <param name="ids">The ids of the changes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async Task MarkSeenAsync(IAppExecutionContext context, IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count > 0 && context.WhatsNew.IsSupported)
        {
            await context.WhatsNew.MarkSeenAsync(ids, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Calls <paramref name="onChanged"/> each time the changes seen by the session user change.</summary>
    /// <param name="onChanged">The callback.</param>
    /// <param name="cancellationToken">Cancels the subscription.</param>
    /// <returns>Disposing it ends the subscription.</returns>
    public static Task<IAsyncDisposable> WatchAsync(Func<ValueTask> onChanged, CancellationToken cancellationToken = default) =>
        WatchAsync(KazoExecutionContext.Current, onChanged, cancellationToken);

    /// <inheritdoc cref="WatchAsync(Func{ValueTask}, CancellationToken)"/>
    /// <param name="context">The app's execution context.</param>
    /// <param name="onChanged">The callback.</param>
    /// <param name="cancellationToken">Cancels the subscription.</param>
    public static async Task<IAsyncDisposable> WatchAsync(
        IAppExecutionContext context, Func<ValueTask> onChanged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(onChanged);
        return await context.WhatsNew.WatchAsync(onChanged, cancellationToken).ConfigureAwait(false);
    }
}
