using Kazo.Os.AppSdk.Web.Mvc;
using Kazo.Os.Samples.Intranet.Services;

namespace Kazo.Os.Samples.Intranet.Controllers;

/// <summary>
/// The staff directory page (<c>.cshtml</c>). The server renders the result of <c>?q=</c>, so the page
/// works without script; <c>wwwroot/js/people.js</c> then filters as the user types by calling
/// <c>/api/people</c> with <c>fetch</c>.
/// </summary>
public sealed class PeopleController(StaffDirectory directory) : KazoController
{
    /// <summary><c>GET /People?q=</c>.</summary>
    /// <param name="q">The search text.</param>
    public IHackActionResult Index(string? q)
    {
        ViewData["Query"] = q;
        return View(directory.Search(q));
    }
}
