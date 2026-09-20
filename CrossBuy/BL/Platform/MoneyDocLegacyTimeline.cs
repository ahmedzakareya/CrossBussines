using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // Read-time history for the two money documents (ADR-005), shared by both adapters for the same reason
    // LegacyReturnTimeline is shared: the uid drives the projection's deduplication and the UI's row
    // identity, so it must be derived one way, not two.
    //
    // A receipt's life is genuinely richer than a return's, and all of it is already recorded:
    //   1. RECORDED — CreatedAt (falling back to the document date) + number + amount + method + party.
    //   2. ALLOCATED — one item per Receipt/PaymentAllocation row. This is the fact the money document
    //      exists for: cash arriving is bookkeeping, cash SETTLING a named invoice is the business event.
    //      It has never been visible anywhere except as a figure inside the settlements table.
    //   3. RE-POSTED — each journal entry after the first under SourceType='Receipt'/'Payment'.
    //   4. REVERSED — JournalEntry.ReversedByEntryId, for the same reason as on a return: the reversed
    //      figures are still on the screen.
    //
    // The allocation rows carry no timestamp of their own, so item 2 is stamped with the document's own
    // date rather than inventing one — an allocation is written in the same transaction as the receipt.
    internal static class MoneyDocLegacyTimeline
    {
        private const int PayloadVersion = 1;

        public sealed class Allocation
        {
            public int DocId { get; init; }
            public string DocNo { get; init; } = "";
            public decimal Amount { get; init; }
        }

        public static async Task<IReadOnlyList<TimelineItemViewModel>> BuildAsync(
            CrossDbContext db, IEntityRegistry registry, BusinessContext context,
            string entityCode, string sourceType, int entityId,
            string reference, DateTime recordedAt, decimal amount, string? methodText,
            string? partyName, string partyPrefixAr, string partyPrefixEn,
            IReadOnlyList<Allocation> allocations,
            string createdType, string allocatedType, string updatedType, string reversedType,
            string titleCreatedAr, string titleCreatedEn,
            string titleAllocatedAr, string titleAllocatedEn,
            string titleUpdatedAr, string titleUpdatedEn,
            string icon, string color, CancellationToken cancellationToken)
        {
            var url = registry.BuildUrl(entityCode, entityId);
            var partyAr = string.IsNullOrWhiteSpace(partyName) ? "" : $" {partyPrefixAr} {partyName}";
            var partyEn = string.IsNullOrWhiteSpace(partyName) ? "" : $" {partyPrefixEn} {partyName}";
            var methodAr = string.IsNullOrWhiteSpace(methodText) ? "" : $" ({methodText})";
            var methodEn = string.IsNullOrWhiteSpace(methodText) ? "" : $" ({methodText})";

            var items = new List<TimelineItemViewModel>
            {
                LegacyTimelineSupport.Item(
                    entityCode, entityId, createdType, recordedAt,
                    titleAr: titleCreatedAr, titleEn: titleCreatedEn,
                    descriptionAr: $"سُجّل السند {reference}{partyAr} بمبلغ {amount:N2}{methodAr}{LegacyTimelineSupport.HistoricalAr}",
                    descriptionEn: $"Voucher {reference}{partyEn} was recorded for {amount:N2}{methodEn}{LegacyTimelineSupport.HistoricalEn}",
                    icon: icon, color: color, url: url, payloadVersion: PayloadVersion),
            };

            // ---- 2. what it settled. One line per allocation, naming the document and the amount.
            foreach (var a in allocations)
                items.Add(LegacyTimelineSupport.Item(
                    entityCode, entityId, allocatedType, recordedAt,
                    titleAr: titleAllocatedAr, titleEn: titleAllocatedEn,
                    descriptionAr: $"خُصّص مبلغ {a.Amount:N2} للفاتورة {a.DocNo}{LegacyTimelineSupport.HistoricalAr}",
                    descriptionEn: $"{a.Amount:N2} was allocated to invoice {a.DocNo}{LegacyTimelineSupport.HistoricalEn}",
                    icon: "ki-outline ki-check-circle", color: "primary", url: url,
                    payloadVersion: PayloadVersion,
                    uidDiscriminator: a.DocId.ToString()));

            var postings = await db.JournalEntries.AsNoTracking()
                .Where(e => e.CompanyID == context.CompanyId && e.SourceType == sourceType && e.SourceId == entityId)
                .OrderBy(e => e.ID)
                .Select(e => new { e.ID, e.EntryNo, e.CreatedAt, e.EntryDate, e.ReversedByEntryId, e.PostedBy })
                .ToListAsync(cancellationToken);

            // ---- 3. index 0 IS the original posting, already described by item 1.
            for (int i = 1; i < postings.Count; i++)
            {
                var posting = postings[i];
                items.Add(LegacyTimelineSupport.Item(
                    entityCode, entityId, updatedType, posting.CreatedAt ?? posting.EntryDate,
                    titleAr: titleUpdatedAr, titleEn: titleUpdatedEn,
                    descriptionAr: $"أُعيد ترحيل السند بالقيد {posting.EntryNo}{LegacyTimelineSupport.HistoricalAr}",
                    descriptionEn: $"Voucher was re-posted under entry {posting.EntryNo}{LegacyTimelineSupport.HistoricalEn}",
                    icon: "ki-outline ki-pencil", color: "primary", url: url,
                    payloadVersion: PayloadVersion,
                    uidDiscriminator: posting.ID.ToString(),
                    actorEmployeeId: posting.PostedBy));
            }

            // ---- 4. reversals.
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
                    if (rev == null) continue;
                    items.Add(LegacyTimelineSupport.Item(
                        entityCode, entityId, reversedType, rev.CreatedAt ?? rev.EntryDate,
                        titleAr: "عكس قيد السند", titleEn: "Voucher entry reversed",
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
