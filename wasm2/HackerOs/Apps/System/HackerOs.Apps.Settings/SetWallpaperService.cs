using System.Globalization;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Files;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.IO;
using HackerOs.AppSdk.Settings;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Theming.Abstractions;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Settings;

/// <summary>
/// The entry point of « Set as wallpaper » (ADR 0218 §1): started by a click in the menu of an
/// image, it checks the image as the session user, writes its path to the user layer of
/// <c>appearance.config</c> (<c>wallpaper</c>, or <c>wallpaperScreen&lt;n&gt;</c> for one secondary
/// screen), tells the user with a notification, then ends. The shell follows the file and shows the
/// new wallpaper at once (ADR 0202 §4).
/// </summary>
public sealed class SetWallpaperService : ServiceAppBase
{
    /// <summary>The context of the current run, kept for an intent delivered while it runs.</summary>
    private IAppExecutionContext? _runContext;

    /// <summary>Initializes the service the way the OS creates an app's entry point, with its manifest only.</summary>
    /// <param name="manifest">The app's manifest.</param>
    public SetWallpaperService(AppManifest manifest)
        : base(manifest)
    {
    }

    /// <summary>The outcome of one click.</summary>
    internal enum Outcome
    {
        /// <summary>The key was written.</summary>
        Changed,

        /// <summary>The intent carried no image path: nothing was done.</summary>
        NoPath,

        /// <summary>The file is not a PNG, JPEG or WebP image, or is too large.</summary>
        NotImage,

        /// <summary>The file cannot be read by the session user.</summary>
        Unreadable,

        /// <summary>The administrator locked the key, or the user's file cannot be written.</summary>
        NotSaved
    }

    /// <summary>
    /// Gets the screen an entry is for: its number for <c>set-wallpaper-screen-&lt;n&gt;</c>, 1
    /// (screen 1 and every screen without its own wallpaper) for the other entries and for a payload
    /// without the entry's name.
    /// </summary>
    /// <param name="entry">The entry's name, or <see langword="null"/>.</param>
    internal static int ScreenOf(string? entry) =>
        entry is not null
        && entry.StartsWith(SetWallpaperApp.ScreenEntryPrefix, StringComparison.Ordinal)
        && int.TryParse(entry.AsSpan(SetWallpaperApp.ScreenEntryPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int screen)
        && screen >= AppContextMenus.MinScreenNumber
        && screen <= AppContextMenus.MaxScreenNumber
            ? screen
            : 1;

    /// <summary>Gets the <c>appearance.config</c> key of a screen's wallpaper.</summary>
    /// <param name="screen">The screen number, 1 to 9.</param>
    internal static string KeyOf(int screen) => screen == 1 ? WallpaperPanel.WallpaperKey : WallpaperPanel.ScreenKey(screen);

    /// <inheritdoc />
    protected override Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
    {
        _runContext = context;
        return context.Intent is { IntentId: SetWallpaperApp.SetWallpaperIntent } intent
            ? ApplyAsync(context, intent, sessionCancellationToken)
            : Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>Only reached while a previous click is still running: the new click is applied too.</remarks>
    protected override Task OnIntentReceivedAsync(AppIntentDelivery intent, CancellationToken cancellationToken) =>
        intent is { IntentId: SetWallpaperApp.SetWallpaperIntent } && _runContext is { } context
            ? ApplyAsync(context, intent, cancellationToken)
            : Task.CompletedTask;

    /// <summary>Applies one click and posts its notification.</summary>
    /// <param name="context">The service's context, under whose user the image is read.</param>
    /// <param name="intent">The intent of the menu entry.</param>
    /// <param name="cancellationToken">Cancels the reads and the write.</param>
    /// <returns>What happened.</returns>
    internal static async Task<Outcome> ApplyAsync(IAppExecutionContext context, AppIntentDelivery intent, CancellationToken cancellationToken)
    {
        string? path = HackFileMenuIntent.ReadPaths(intent.PayloadJson).FirstOrDefault();
        if (path is null)
        {
            return Outcome.NoPath;
        }

        int screen = ScreenOf(HackFileMenuIntent.ReadEntry(intent.PayloadJson));
        IStringLocalizer<Strings> l = context.GetLocalizer<Strings>();
        string name = HackPath.GetFileName(path);
        (Outcome outcome, string? error) = await WriteAsync(context, path, screen, l, cancellationToken);
        if (outcome == Outcome.Changed)
        {
            Post(context, NotificationSeverity.Success, l["SetWallpaper.Done.Title"],
                screen == 1 ? l["SetWallpaper.Done.Message", name] : l["SetWallpaper.Done.MessageScreen", name, screen]);
        }
        else
        {
            Post(context, NotificationSeverity.Error, l["SetWallpaper.Error.Title"], error ?? string.Empty);
        }

        return outcome;
    }

    /// <summary>Checks the image as the session user, then writes the key of the screen.</summary>
    private static async Task<(Outcome Outcome, string? Error)> WriteAsync(
        IAppExecutionContext context,
        string path,
        int screen,
        IStringLocalizer<Strings> l,
        CancellationToken cancellationToken)
    {
        using IDisposable scope = HackExecutionContext.Enter(context);
        string name = HackPath.GetFileName(path);
        try
        {
            // The same rules as the shell (ADR 0202 §3): the type is read from the bytes, never the name.
            byte[] content = await HackFile.ReadAllBytesAsync(path, cancellationToken);
            if (content.LongLength > WallpaperCatalog.MaxImageBytes || !WallpaperCatalog.TryDetectMediaType(content, out _))
            {
                return (Outcome.NotImage, l["SetWallpaper.Error.NotImage", name]);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (Outcome.Unreadable, l["SetWallpaper.Error.Unreadable", name, exception.Message]);
        }

        try
        {
            await using HackSettingsDocument appearance = await HackSettings.OpenSystemAsync(context, SettingsWindow.AppearanceDocumentId, cancellationToken);
            await appearance.SetAsync(KeyOf(screen), path, SettingLayer.User, cancellationToken);
            return (Outcome.Changed, null);
        }
        catch (HackSettingLockedException exception)
        {
            return (Outcome.NotSaved, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return (Outcome.NotSaved, l["SetWallpaper.Error.NotSaved", exception.Message]);
        }
    }

    /// <summary>Posts the outcome; a host without notifications only loses the message, not the change.</summary>
    private static void Post(IAppExecutionContext context, NotificationSeverity severity, string title, string message)
    {
        try
        {
            context.Notifications.Post(severity, title, message);
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or AppGatewayAccessDeniedException)
        {
            // No notification center: the wallpaper itself shows the change.
        }
    }
}
