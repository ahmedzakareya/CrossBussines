using Microsoft.AspNetCore.Mvc;
using CrossBuy.Models;

namespace CrossBuy.Controllers
{
    // Retired legacy P5 comments API. Current screens use the record-authorized Communication Platform;
    // keeping a company-wide DocComments reader here would bypass each record's authorization policy.
    [SessionValidation]
    public class CommentsController : Controller
    {
        [HttpGet]
        public IActionResult List(string? entityType, int entityId)
        {
            return StatusCode(410, new { code = "legacy_comment_reads_retired" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Add(string entityType, int entityId, string body, string? mentionedIds)
        {
            return StatusCode(410, new { code = "legacy_comment_writes_retired" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {
            return StatusCode(410, new { code = "legacy_comment_writes_retired" });
        }
    }
}
