using Kazo.Os.AppSdk.Web.Mvc;
using Kazo.Os.Samples.Intranet.Models;
using Kazo.Os.Samples.Intranet.Services;

namespace Kazo.Os.Samples.Intranet.Controllers.Api;

/// <summary>
/// The staff directory as JSON: the search box of <c>/People</c> (<c>wwwroot/js/people.js</c>) calls
/// it with <c>fetch</c> on every key stroke, through the browser's bridge and the simulated network.
/// </summary>
[Route("api/people")]
public sealed class PeopleApiController(StaffDirectory directory) : KazoApiController
{
    /// <summary><c>GET /api/people?q=</c>: the matching people, sorted by name.</summary>
    /// <param name="q">The search text.</param>
    [HttpGet]
    public IReadOnlyList<Person> Search([FromQuery] string? q = null) => directory.Search(q);

    /// <summary><c>GET /api/people/{id}</c>.</summary>
    /// <param name="id">The identifier.</param>
    [HttpGet("{id:int}")]
    public IKazoActionResult Get(int id) => directory.Find(id) is Person person ? Ok(person) : NotFound();
}
