using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // The reconstruction shared by the two return adapters.
    //
    // It is ONE method rather than the same forty lines copied twice because the deduplication the projection
    // performs and the row identity the UI uses both derive from the uid, and a uid built two slightly
    // different ways is a defect that shows up only as duplicated history months later. The invoice pair was
    // kept identical by discipline; this pair is kept identical by construction.
    internal static class LegacyReturnTimeline
    {
        // Reconstructed history carries no payload of its own; version 1 names the shape, nothing reads it.
        private const int PayloadVersion = 1;

        public static async Task<IReadOnlyList<TimelineItemViewModel>> BuildAsync(
            CrossDbContext db, IEntityRegistry registry, BusinessContext context,
            string entityCode, string sourceType, int entityId,
            string reference, DateTime recordedAt, decimal total,
            string? partyName, string partyPrefixAr, string partyPrefixEn, string? againstNo,
            string createdType, string updatedType, string reversedType,
            string titleCreatedAr, string titleCreatedEn,
            string titleUpdatedAr, string titleUpdatedEn,
            string icon, CancellationToken cancellationToken)
        {
            var url = registry.BuildUrl(entityCode, entityId);
            var partyAr = string.IsNullOrWhiteSpace(partyName) ? "" : $" {partyPrefixAr} {partyName}";
            var partyEn = string.IsNullOrWhiteSpace(partyName) ? "" : $" {partyPrefixEn} {partyName}";
            var againstAr = string.IsNullOrWhiteSpace(againstNo) ? "" : $" مقابل الفاتورة {againstNo}";
            var againstEn = string.IsNullOrWhiteSpace(againstNo) ? "" : $" against invoice {againstNo}";

            var items = new List<TimelineItemViewModel>();

            // ---- 1. the return was recorded. Creation IS the posting for these documents: neither
            // ReceivableService nor PayableService has a draft-then-post step for a return.
            items.Add(LegacyTimelineSupport.Item(
                entityCode, entityId, createdType, recordedAt,
                titleAr: titleCreatedAr, titleEn: titleCreatedEn,
                descriptionAr: $"سُجّل المرتجع {reference}{partyAr}{againstAr} بإجمالي {total:N2}{LegacyTimelineSupport.HistoricalAr}",
                descriptionEn: $"Return {reference}{partyEn}{againstEn} was recorded totalling {total:N2}{LegacyTimelineSupport.HistoricalEn}",
                icon: icon, color: "success", url: url, payloadVersion: PayloadVersion));

            var postings = await db.JournalEntries.AsNoTracking()
                .Where(e => e.CompanyID == context.CompanyId && e.SourceType == sourceType && e.SourceId == entityId)
                .OrderBy(e => e.ID)
                .Select(e => new { e.ID, e.EntryNo, e.CreatedAt, e.EntryDate, e.ReversedByEntryId, e.PostedBy })
                .ToListAsync(cancellationToken);

            // ---- 2. one item per RE-post. Index 0 is the original posting, already described by item 1.
            for (int i = 1; i < postings.Count; i++)
            {
                var posting = postings[i];
                items.Add(LegacyTimelineSupport.Item(
                    entityCode, entityId, updatedType, posting.CreatedAt ?? posting.EntryDate,
                    titleAr: titleUpdatedAr, titleEn: titleUpdatedEn,
                    descriptionAr: $"أُعيد ترحيل المرتجع بالقيد {posting.EntryNo}{LegacyTimelineSupport.HistoricalAr}",
                    descriptionEn: $"Return was re-posted under entry {posting.EntryNo}{LegacyTimelineSupport.HistoricalEn}",
                    icon: "ki-outline ki-pencil", color: "primary", url: url,
                    payloadVersion: PayloadVersion,
                    uidDiscriminator: posting.ID.ToString(),
                    actorEmployeeId: posting.PostedBy));
            }

            // ---- 3. every reversal. NOT decoration: a reversed entry's figures are still on the screen, and
            // a reader who quotes them without knowing they were reversed quotes a number that no longer
            // stands. The invoice adapters omit this; the data has always recorded it.
            var reversedIds = postings.Where(p => p.ReversedByEntryId != null)
                                      .Select(p => p.ReversedByEntryId!.Value).Distinct().ToList();
            if (reversedIds.Count > 0)
            {
                var reversals = await db.JournalEntries.AsNoTracking()
                    .Where(e => reversedIds.Contains(e.ID) && e.CompanyID == context.CompanyId)
                    .Select(e => new { e.ID, e.EntryNo, e.CreatedAt, e.EntryDate, e.PostedBy })
                    .ToListAsync(cancellationToken);

                foreach (var p in postings.Where(p => p.ReversedByEntryId != null))
                {
                    var rev = reversals.FirstOrDefault(r => r.ID == p.ReversedByEntryId!.Value);
                    if (rev == null) continue;   // the mirror entry is gone; say nothing rather than guess
                    items.Add(LegacyTimelineSupport.Item(
                        entityCode, entityId, reversedType, rev.CreatedAt ?? rev.EntryDate,
                        titleAr: "عكس قيد المرتجع", titleEn: "Return entry reversed",
                        descriptionAr: $"عُكس القيد {p.EntryNo} بالقيد {rev.EntryNo}{LegacyTimelineSupport.HistoricalAr}",
                        descriptionEn: $"Entry {p.EntryNo} was reversed by entry {rev.EntryNo}{LegacyTimelineSupport.HistoricalEn}",
                        icon: "ki-outline ki-arrows-circle", color: "danger", url: url,
                        payloadVersion: PayloadVersion,
                        uidDiscriminator: rev.ID.ToString(),
                        actorEmployeeId: rev.PostedBy));
                }
            }

            return items;
        }
    }
}
