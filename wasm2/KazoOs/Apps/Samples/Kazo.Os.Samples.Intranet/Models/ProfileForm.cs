using System.ComponentModel.DataAnnotations;

namespace Kazo.Os.Samples.Intranet.Models;

/// <summary>
/// The form of <c>/Home/Profile</c>: the display name kept in the cookie session and written as the
/// author of the notes. Rendered with <c>Html.TextBoxFor</c> and validated by <c>DataAnnotations</c>.
/// </summary>
public sealed class ProfileForm
{
    /// <summary>Gets or sets the display name (2 to 32 characters).</summary>
    [Required]
    [StringLength(32, MinimumLength = 2)]
    [Display(Name = "Display name")]
    public string Name { get; set; } = string.Empty;
}
