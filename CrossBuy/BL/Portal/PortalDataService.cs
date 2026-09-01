using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Portal;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Portal
{
    // =============================================================================================
    // CLIENT PORTAL — the authorized read surface.
    //
    // EVERY query in this file carries the SAME two predicates, in the WHERE clause, from the
    // resolved portal context:
    //
    //     CompanyID  == ctx.CompanyId
    //     CustomerId == ctx.CustomerId
    //
    // Both, always. Company alone is not enough — two customers of one company must not see each
    // other, and that is the mistake a "tenant-scoped" portal makes on its first day. They are in the
    // query rather than checked afterwards, so a foreign row is never materialised and "belongs to
    // someone else" is indistinguishable from "does not exist".
    //
    // NOTHING HERE TAKES AN ID FROM THE CALLER AS AUTHORITY. Where a detail method accepts an id it
    // is a FILTER applied on top of the scope, never a lookup key on its own: passing another
    // customer's quotation id returns nothing, because the scope already excluded it.
    //
    // THE DTOs ARE NOT INTERNAL DTOs. They are hand-written projections that name every field a
    // customer may see, so adding an internal column to an entity cannot leak it here by accident.
    // What is deliberately absent is listed against each one — absence by decision, not by oversight.
    // =============================================================================================

    /// A quotation as the customer sees it. No Notes (internal), no CreatedBy (staff identity), no
    /// WarehouseId, no SalesOrderId (internal conversion plumbing), no exchange rate mechanics.
    public sealed record PortalQuotation(
        int Id, string? Number, DateTime Date, DateTime? ValidUntil,
        decimal Total, string Status, bool AwaitingResponse);

    /// A project as the customer sees it. NO Budget, NO ContractValue, NO AdvancePercent, NO
    /// RetentionPercent, NO CostCenterId — those are internal commercial terms and costing, and §7
    /// forbids them. Name, dates, location and status are what a client legitimately follows.
    public sealed record PortalProject(
        int Id, string Code, string Name, string? Location,
        string? Status, DateTime? StartDate, DateTime? EndDate);

    /// An invoice as the customer sees it. No GL account, no journal entry id, no internal notes, no
    /// base-currency mechanics — and never another customer's balance.
    /// NO IsOverdue. SalesInvoice carries no due date, and deriving one from the customer's payment
    /// terms would invent a commercial fact the product does not record — telling a client they are
    /// late on the strength of a guess. Reported as a gap rather than fabricated.
    public sealed record PortalInvoice(
        int Id, string? Number, DateTime Date, decimal Total,
        decimal Paid, decimal Outstanding, string Status);

    public sealed record PortalHome(
        string CustomerName,
        int QuotationsAwaitingResponse,
        int ActiveProjects,
        int OutstandingInvoices,
        decimal OutstandingAmount,
        IReadOnlyList<PortalQuotation> RecentQuotations,
        IReadOnlyList<PortalInvoice> RecentInvoices);

    public interface IPortalDataService
    {
        Task<PortalHome?> HomeAsync(CancellationToken ct = default);
        Task<IReadOnlyList<PortalQuotation>> QuotationsAsync(CancellationToken ct = default);
        Task<IReadOnlyList<PortalProject>> ProjectsAsync(CancellationToken ct = default);
        Task<IReadOnlyList<PortalInvoice>> InvoicesAsync(CancellationToken ct = default);
    }

    public sealed class PortalDataService : IPortalDataService
    {
        private readonly CrossDbContext _db;
        private readonly IPortalContextAccessor _contexts;

        public PortalDataService(CrossDbContext db, IPortalContextAccessor contexts)
        { _db = db; _contexts = contexts; }

        /// A quotation is only the customer's business once it has been SENT to them. Draft is an
        /// internal working state, and showing it would let a client watch staff compose an offer.
        private static readonly string[] CustomerVisibleQuotationStatuses =
            { "Sent", "Accepted", "Rejected", "Converted", "Expired" };

        public async Task<IReadOnlyList<PortalQuotation>> QuotationsAsync(CancellationToken ct = default)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx == null || !ctx.Can(PortalCapabilities.ViewQuotations))
                return Array.Empty<PortalQuotation>();

            var rows = await _db.Quotations.AsNoTracking()
                .Where(q => q.CompanyID == ctx.CompanyId
                            && q.CustomerId == ctx.CustomerId
                            && CustomerVisibleQuotationStatuses.Contains(q.Status))
                .OrderByDescending(q => q.QuoteDate).ThenByDescending(q => q.ID)
                .Take(200)
                .Select(q => new { q.ID, q.QuoteNo, q.QuoteDate, q.ValidUntil, q.GrandTotal, q.Status })
                .ToListAsync(ct);

            var today = DateTime.UtcNow.Date;
            return rows.Select(q => new PortalQuotation(
                q.ID, q.QuoteNo, q.QuoteDate, q.ValidUntil, q.GrandTotal, q.Status,
                // "Awaiting response" is derived, not stored: sent, still in date, not yet answered.
                AwaitingResponse: string.Equals(q.Status, "Sent", StringComparison.Ordinal)
                                  && (q.ValidUntil == null || q.ValidUntil.Value.Date >= today))).ToList();
        }

        public async Task<IReadOnlyList<PortalProject>> ProjectsAsync(CancellationToken ct = default)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx == null || !ctx.Can(PortalCapabilities.ViewProjects))
                return Array.Empty<PortalProject>();

            // Project.CustomerId is nullable — an internal cost-centre project has none. The equality
            // predicate excludes those rows on its own, so an unassigned project is never a client's.
            return await _db.Projects.AsNoTracking()
                .Where(p => p.CompanyID == ctx.CompanyId && p.CustomerId == ctx.CustomerId)
                .OrderByDescending(p => p.StartDate ?? DateTime.MinValue).ThenBy(p => p.Name)
                .Take(200)
                .Select(p => new PortalProject(p.ID, p.Code, p.Name, p.Location, p.Status, p.StartDate, p.EndDate))
                .ToListAsync(ct);
        }

        public async Task<IReadOnlyList<PortalInvoice>> InvoicesAsync(CancellationToken ct = default)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx == null || !ctx.Can(PortalCapabilities.ViewInvoices))
                return Array.Empty<PortalInvoice>();

            // Only POSTED invoices. A draft is not yet a claim on the customer, and a cancelled one is
            // no longer one; showing either would be telling a client they owe something they do not.
            var rows = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.CompanyID == ctx.CompanyId
                            && i.CustomerId == ctx.CustomerId
                            && i.Status == "Posted")
                .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.ID)
                .Take(200)
                .Select(i => new { i.ID, i.InvoiceNo, i.InvoiceDate, i.GrandTotal, i.Status })
                .ToListAsync(ct);

            var ids = rows.Select(r => r.ID).ToList();

            // Settlement comes from the allocations actually recorded against these invoices — and the
            // allocation query is scoped to THIS customer's invoice ids, so it can never sum somebody
            // else's receipts into this client's balance.
            var paid = await _db.ReceiptAllocations.AsNoTracking()
                .Where(a => a.CompanyID == ctx.CompanyId && ids.Contains(a.SalesInvoiceId))
                .GroupBy(a => a.SalesInvoiceId)
                // ForeignAmount is the settlement in the INVOICE's own currency, which is the figure
                // the customer's invoice is denominated in. ArBase is the functional-currency ledger
                // effect and is internal accounting, so it stays out of a client's view.
                .Select(g => new { InvoiceId = g.Key, Amount = g.Sum(x => x.ForeignAmount) })
                .ToDictionaryAsync(x => x.InvoiceId, x => x.Amount, ct);

            return rows.Select(i =>
            {
                var settled = paid.TryGetValue(i.ID, out var p) ? p : 0m;
                return new PortalInvoice(
                    i.ID, i.InvoiceNo, i.InvoiceDate, i.GrandTotal, settled, i.GrandTotal - settled, i.Status);
            }).ToList();
        }

        public async Task<PortalHome?> HomeAsync(CancellationToken ct = default)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx == null) return null;

            // The customer's own name is read through the same scope as everything else: company AND
            // id. A portal that trusted only the id would show the right name for the wrong tenant.
            var name = await _db.Customers.AsNoTracking()
                .Where(c => c.ID == ctx.CustomerId && c.CompanyID == ctx.CompanyId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct);
            if (name == null) return null;      // a link pointing at nothing is not a session

            var quotations = await QuotationsAsync(ct);
            var projects = await ProjectsAsync(ct);
            var invoices = await InvoicesAsync(ct);

            var outstanding = invoices.Where(i => i.Outstanding > 0).ToList();

            return new PortalHome(
                CustomerName: name,
                QuotationsAwaitingResponse: quotations.Count(q => q.AwaitingResponse),
                ActiveProjects: projects.Count(p => !string.Equals(p.Status, "Completed", StringComparison.Ordinal)
                                                 && !string.Equals(p.Status, "Cancelled", StringComparison.Ordinal)),
                OutstandingInvoices: outstanding.Count,
                OutstandingAmount: outstanding.Sum(i => i.Outstanding),
                RecentQuotations: quotations.Take(5).ToList(),
                RecentInvoices: invoices.Take(5).ToList());
        }
    }
}
