using Kazo.Os.AppSdk.Web.Antiforgery;
using Kazo.Os.AppSdk.Web.Mvc;
using Kazo.Os.Samples.Intranet.Models;
using Kazo.Os.Samples.Intranet.Services;

namespace Kazo.Os.Samples.Intranet.Controllers;

/// <summary>
/// The home page and the visitor's profile, on the default conventional route
/// (<c>{controller=Home}/{action=Index}/{id?}</c>). Its views are <c>.cshtml</c> files
/// (<c>Views/Home/</c>, ADR 0086) in the <c>.razor</c> layout of the site.
/// </summary>
public sealed class HomeController(AnnouncementFeed feed, NoteStore notes, TaskBoard board) : KazoController
{
    /// <summary><c>GET /</c>: news, counters and the visitor's name.</summary>
    /// <param name="cancellationToken">Aborted with the request.</param>
    public async Task<IKazoActionResult> Index(CancellationToken cancellationToken) =>
        View(new HomeViewModel(feed.Latest, await notes.CountAsync(cancellationToken), board.Count, IntranetSession.DisplayName(HttpContext)));

    /// <summary><c>GET /Home/Profile</c>: the form that chooses a display name.</summary>
    [HttpGet]
    public IKazoActionResult Profile() =>
        View(new ProfileForm { Name = IntranetSession.DisplayName(HttpContext) ?? string.Empty });

    /// <summary>
    /// <c>POST /Home/Profile</c>: keeps the name in the session, then back home (post/redirect/get).
    /// A form without a valid anti-forgery token is refused with <c>400</c>; an invalid name shows
    /// the form again with its errors.
    /// </summary>
    /// <param name="form">The posted form.</param>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IKazoActionResult Profile(ProfileForm form)
    {
        if (!ModelState.IsValid)
        {
            return View(form);
        }

        HttpContext.Session.SetString(IntranetSession.DisplayNameKey, form.Name.Trim());
        return RedirectToAction(nameof(Index));
    }

    /// <summary><c>POST /Home/SignOut</c>: forgets the session's values.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IKazoActionResult SignOut()
    {
        HttpContext.Session.Clear();
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>The model of <c>Views/Home/Index.cshtml</c>.</summary>
/// <param name="News">The news items.</param>
/// <param name="NoteCount">How many notes the wiki holds.</param>
/// <param name="TaskCount">How many cards the board holds.</param>
/// <param name="DisplayName">The visitor's name, or <see langword="null"/>.</param>
public sealed record HomeViewModel(IReadOnlyList<Announcement> News, int NoteCount, int TaskCount, string? DisplayName);
