namespace CrossBuy.BL.Construction;

// These are persisted-writer identities, never browser claims or matches by amount/description.
public sealed record ConstructionRealizationLineage(
    int CompanyId, int ProjectId, int OrderId, int OrderLineId,
    int InvoiceId, int InvoiceLineId, int JournalEntryId, int JournalLineId);

public sealed record ConstructionRealizationLineageCheck(bool Ok, string Code);

// Necessary identity checks only. A valid graph does not authorize release, value an expense,
// establish currency equivalence, or prove that the source has not subsequently been reversed.
public static class ConstructionRealizationLineageRules
{
    public static ConstructionRealizationLineageCheck Validate(
        int companyId, int projectId, int orderId, int invoiceId, int journalEntryId,
        IReadOnlyCollection<int> orderLineIds, IReadOnlyCollection<int> invoiceLineIds,
        IReadOnlyCollection<int> invoiceNetJournalLineIds,
        IReadOnlyCollection<ConstructionRealizationLineage> links)
    {
        if (companyId <= 0 || projectId <= 0 || orderId <= 0 || invoiceId <= 0 || journalEntryId <= 0)
            return new(false, "InvalidSourceIdentity");
        if (!ValidIds(orderLineIds) || !ValidIds(invoiceLineIds) || !ValidIds(invoiceNetJournalLineIds))
            return new(false, "InvalidSourceLineIdentities");
        if (links.Count == 0) return new(false, "WriterLineageRequired");
        if (links.Any(x => x.CompanyId != companyId || x.ProjectId != projectId ||
            x.OrderId != orderId || x.InvoiceId != invoiceId || x.JournalEntryId != journalEntryId))
            return new(false, "SourceScopeMismatch");
        // Initial contract supports the writer's complete one-to-one conversion only.
        // Partial invoices and grouped ledger postings require their own explicit contracts.
        if (links.Select(x => x.OrderLineId).Distinct().Count() != links.Count ||
            links.Select(x => x.InvoiceLineId).Distinct().Count() != links.Count ||
            links.Select(x => x.JournalLineId).Distinct().Count() != links.Count)
            return new(false, "AmbiguousSourceLineage");
        if (!orderLineIds.ToHashSet().SetEquals(links.Select(x => x.OrderLineId)) ||
            !invoiceLineIds.ToHashSet().SetEquals(links.Select(x => x.InvoiceLineId)) ||
            !invoiceNetJournalLineIds.ToHashSet().SetEquals(links.Select(x => x.JournalLineId)))
            return new(false, "IncompleteSourceLineage");
        return new(true, "ValidIdentityGraphOnly");
    }

    private static bool ValidIds(IReadOnlyCollection<int> ids) =>
        ids.Count > 0 && ids.All(x => x > 0) && ids.Distinct().Count() == ids.Count;
}
