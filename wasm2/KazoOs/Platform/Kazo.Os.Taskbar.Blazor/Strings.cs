namespace Kazo.Os.Taskbar.Blazor;

/// <summary>
/// Names the taskbar's texts (ADR 0104): <c>Resources/Strings.resx</c> in English and
/// <c>Resources/Strings.fr.resx</c> in French, read through
/// <see cref="Kazo.Os.Theming.Blazor.Localization.PackageStringLocalizer"/> so a host without
/// localization still renders the English texts.
/// </summary>
internal sealed class Strings
{
    private Strings()
    {
    }
}
