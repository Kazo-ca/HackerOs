using System.Diagnostics.CodeAnalysis;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// How a child window or dialog of an app was closed (ADR 0234 §3), as WinForms <c>DialogResult</c>.
/// </summary>
public enum KazoDialogResultKind
{
    /// <summary>The window closed without a result: its app stopped, or its owner closed.</summary>
    None,

    /// <summary>The user accepted (OK).</summary>
    Ok,

    /// <summary>The user cancelled, or closed the window with its Close button or Alt+F4.</summary>
    Cancel,

    /// <summary>The user aborted the operation.</summary>
    Abort,

    /// <summary>The user asked to try again.</summary>
    Retry,

    /// <summary>The user chose to ignore the problem.</summary>
    Ignore,

    /// <summary>The user answered Yes.</summary>
    Yes,

    /// <summary>The user answered No.</summary>
    No,
}

/// <summary>
/// The result a child window or dialog gives back when it closes (ADR 0234 §3): its kind and an
/// optional value, such as the settings a dialog edited.
/// </summary>
/// <param name="Kind">How the window was closed.</param>
/// <param name="Value">The value the window gives back, or <see langword="null"/>.</param>
public sealed record KazoDialogResult(KazoDialogResultKind Kind, object? Value = null)
{
    /// <summary>Gets the result of a window closed without one.</summary>
    public static KazoDialogResult None { get; } = new(KazoDialogResultKind.None);

    /// <summary>Gets the result of a window the user cancelled or closed.</summary>
    public static KazoDialogResult Cancel { get; } = new(KazoDialogResultKind.Cancel);

    /// <summary>Creates an accepted result carrying <paramref name="value"/>.</summary>
    /// <param name="value">The value the dialog gives back.</param>
    /// <returns>A result of kind <see cref="KazoDialogResultKind.Ok"/>.</returns>
    public static KazoDialogResult Ok(object? value = null) => new(KazoDialogResultKind.Ok, value);

    /// <summary>Gets whether the user accepted (<see cref="KazoDialogResultKind.Ok"/> or <see cref="KazoDialogResultKind.Yes"/>).</summary>
    public bool IsAccepted => Kind is KazoDialogResultKind.Ok or KazoDialogResultKind.Yes;

    /// <summary>Gets <see cref="Value"/> as <typeparamref name="T"/>, or the default when it is not one.</summary>
    /// <typeparam name="T">The expected type.</typeparam>
    /// <returns>The value, or <see langword="default"/>.</returns>
    public T? ValueAs<T>() => Value is T value ? value : default;
}

/// <summary>
/// What an app asks for when it opens a child window (ADR 0234 §3): its title, its sizes, style and
/// switches, and the parameters of its component, as a WinForms form's properties before
/// <c>Show</c> or <c>ShowDialog</c>.
/// </summary>
public sealed record KazoWindowRequest
{
    /// <summary>Gets the window's title; <see langword="null"/> or blank gives the app's name.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets the window's sizes, style and switches (<see cref="WindowOptions.Style"/>,
    /// <see cref="WindowOptions.Modal"/>…); <see langword="null"/> gives <see cref="WindowOptions.Default"/>.
    /// </summary>
    public WindowOptions? Options { get; init; }

    /// <summary>
    /// Gets the parameters given to the window's component, by property name, as
    /// <c>[Parameter]</c> values in Razor markup.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>
/// A child window an app opened with <see cref="WindowAppBase.ShowWindowAsync{TComponent}"/> (ADR 0234 §3).
/// </summary>
public interface IKazoWindow
{
    /// <summary>Gets the window manager's id of the window, for logs and tests.</summary>
    string Id { get; }

    /// <summary>Gets whether the window is still open.</summary>
    bool IsOpen { get; }

    /// <summary>Gets a task completed with the window's result once it closes.</summary>
    Task<KazoDialogResult> Closed { get; }

    /// <summary>Closes the window with <paramref name="result"/>, as <c>Form.Close</c>.</summary>
    /// <param name="result">The result to give back; <see langword="null"/> gives <see cref="KazoDialogResult.Cancel"/>.</param>
    /// <param name="cancellationToken">Cancels the request before it is applied.</param>
    /// <returns><see langword="true"/> when this call closed the window.</returns>
    ValueTask<bool> CloseAsync(KazoDialogResult? result = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Opens child windows for one app window (ADR 0234 §3). The platform implements it next to
/// <see cref="IAppWindowGateway"/>; apps call <see cref="WindowAppBase.ShowWindowAsync{TComponent}"/> and
/// <see cref="WindowAppBase.ShowDialogAsync{TComponent}"/>.
/// </summary>
public interface IAppChildWindowGateway
{
    /// <summary>
    /// Opens a window that shows <paramref name="componentType"/>, owned by this gateway's window and
    /// belonging to the same app instance.
    /// </summary>
    /// <param name="componentType">The window's component, deriving from <see cref="KazoWindowBase"/>.</param>
    /// <param name="request">Title, options and parameters.</param>
    /// <param name="cancellationToken">Cancels the request before the window opens.</param>
    /// <returns>The new window, or <see langword="null"/> when this window cannot own one (closed, detached).</returns>
    ValueTask<IKazoWindow?> OpenWindowAsync(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type componentType,
        KazoWindowRequest request,
        CancellationToken cancellationToken = default);
}
