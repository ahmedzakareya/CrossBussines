using System.Globalization;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
    // Platform Kernel slice 1 — the read endpoint behind the business-event timeline partial.
    //
    // It is a thin shell on purpose: entity metadata comes from IEntityRegistry, the context from
    // IBusinessContextAccessor, and every isolation / permission / visibility decision from
    // ITimelineProjectionService. The controller adds no filtering of its own, so there is no second place
    // where an authorization rule could be forgotten.
    [SessionValidation]
    public class PlatformTimelineController : Controller
    {
        private readonly ITimelineProjectionService _timeline;
        private readonly IEntityRegistry _registry;
        private readonly IBusinessContextAccessor _context;

        public PlatformTimelineController(
            ITimelineProjectionService timeline, IEntityRegistry registry, IBusinessContextAccessor context)
        { _timeline = timeline; _registry = registry; _context = context; }

        [HttpGet]
        public async Task<IActionResult> List(string entityType, int entityId, int take = 50, CancellationToken cancellationToken = default)
        {
            // Free-text entity types are rejected at the edge: an unregistered code is a bad request, and a
            // registered code whose timeline is not yet enabled is "nothing here", not an error.
            if (!_registry.TryGetDefinition(entityType, out var definition)) return BadRequest();
            if (!definition!.SupportsTimeline) return Json(Array.Empty<object>());
            if (entityId <= 0) return BadRequest();

            var context = await _context.GetCurrentAsync(cancellationToken);
            if (!context.IsAuthenticated) return Unauthorized();

            IReadOnlyList<TimelineItemViewModel> items;
            try
            {
                items = await _timeline.GetAsync(definition.Code, entityId, context, take, cancellationToken);
            }
            catch (PlatformAccessDeniedException)
            {
                // "You may not view this record" is distinct from "this record has no history".
                return Forbid();
            }

            bool isArabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
            return Json(items.Select(i => new
            {
                eventUid = i.EventUid,
                eventType = i.EventType,
                title = isArabic ? i.TitleAr : i.TitleEn,
                description = isArabic ? i.DescriptionAr : i.DescriptionEn,
                actorName = i.ActorDisplayName,
                // The model carried the actor's ID all along; only the NAME was being sent, so a
                // caller could say who acted but never show their face. A UI that wants an avatar
                // has nothing to resolve it from without this.
                actorEmployeeId = i.ActorEmployeeId,
                icon = i.Icon,
                color = i.Color,
                createdAt = i.CreatedAt,
                visibility = i.Visibility,
                legacy = i.Source == TimelineItemSource.Legacy,
            }));
        }
    }
}
