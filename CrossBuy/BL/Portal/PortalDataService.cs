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
    /// CanRespond is the SERVER'S answer to "may this customer act on this row", carried to the view
    /// so the screen does not have to reason about capability or state itself. It is a rendering hint
    /// and NOT the control: RespondToQuotationAsync re-decides every one of these questions on the
    /// POST, because a hidden button is not an authorization mechanism.
    public sealed record PortalQuotation(
        int Id, string? Number, DateTime Date, DateTime? ValidUntil,
        decimal Total, string Status, bool AwaitingResponse,
        bool CanRespond = false, bool CanRequestContact = false);

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

    /// What the shell needs to name the account: who they are, and the three numbers Demo 30 puts in
    /// the hero band. One small read rather than four screens each fetching their own.
    public sealed record PortalIdentityCard(
        string CustomerName, string Initials,
        int QuotationsAwaitingResponse, int ActiveProjects, decimal OutstandingAmount);

    /// The outcome of a customer decision. A CODE, never a sentence: the portal must be able to tell
    /// "you may not" from "too late" from "already answered" without a translated string, and the
    /// refusals that could reveal the existence of somebody else's quotation all collapse to one code.
    public sealed record PortalDecisionResult(bool Ok, string Code, string? Status = null)
    {
        /// The ONE refusal used for absent, another company's, another customer's, and not-yet-sent.
        /// They are indistinguishable on purpose: any difference between them is an oracle that lets a
        /// customer probe quotation ids for existence.
        public const string NotFound = "not_found";
        public const string NotPermitted = "not_permitted";
        public const string AlreadyDecided = "already_decided";
        public const string Expired = "expired";
        public const string Unavailable = "unavailable";

        /// The customer already has an open request for this. Reported rather than silently written
        /// twice: a second row would double the work staff see without telling them anything new, and
        /// silently discarding it would leave the customer unsure whether they were heard.
        public const string AlreadyRequested = "already_requested";

        public static PortalDecisionResult Refused(string code) => new(false, code);
        public static PortalDecisionResult Accepted(string status) => new(true, "ok", status);
    }

    public interface IPortalDataService
    {
        Task<PortalHome?> HomeAsync(CancellationToken ct = default);
        Task<IReadOnlyList<PortalQuotation>> QuotationsAsync(CancellationToken ct = default);
        Task<IReadOnlyList<PortalProject>> ProjectsAsync(CancellationToken ct = default);
        Task<IReadOnlyList<PortalInvoice>> InvoicesAsync(CancellationToken ct = default);

        /// The customer answers a quotation. `accept` false is a rejection - one method, because the
        /// authorization, ownership and state rules are identical and a second copy of them is how the
        /// reject path eventually stops matching the accept path.
        Task<PortalDecisionResult> RespondToQuotationAsync(
            int quotationId, bool accept, CancellationToken ct = default);

        /// The customer's own name, for the shell. Null when there is no portal identity - the layout
        /// then shows nothing rather than a blank where a name should be.
        Task<PortalIdentityCard?> IdentityAsync(CancellationToken ct = default);

        /// The customer asks to be contacted. `quotationId` is OPTIONAL and, when given, is an
        /// identifier whose ownership is checked - never a grant. The message is the customer's own
        /// words and is the only free text an outsider controls anywhere in this service.
        Task<PortalDecisionResult> RequestContactAsync(
            int? quotationId, string? message, CancellationToken ct = default);
    }

    public sealed class PortalDataService : IPortalDataService
    {
        private readonly CrossDbContext _db;
        private readonly IPortalContextAccessor _contexts;

        /// The module that owns the quotation transition. OPTIONAL so that the read-only portal keeps
        /// working in a host that has not wired selling: every read path above predates this and must
        /// not start depending on it. A decision with no selling service refuses honestly rather than
        /// writing the status itself - the portal authorizes, the module transitions.
        private readonly CrossBuy.BL.ISellingService? _selling;

        public PortalDataService(CrossDbContext db, IPortalContextAccessor contexts,
            CrossBuy.BL.ISellingService? selling = null)
        { _db = db; _contexts = contexts; _selling = selling; }

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
            var mayRespond = ctx.Can(PortalCapabilities.RespondToQuotations);
            var mayAsk = ctx.Can(PortalCapabilities.RequestContact);
            return rows.Select(q =>
            {
                // "Awaiting response" is derived, not stored: sent, still in date, not yet answered.
                var awaiting = string.Equals(q.Status, "Sent", StringComparison.Ordinal)
                               && (q.ValidUntil == null || q.ValidUntil.Value.Date >= today);
                return new PortalQuotation(
                    q.ID, q.QuoteNo, q.QuoteDate, q.ValidUntil, q.GrandTotal, q.Status,
                    AwaitingResponse: awaiting,
                    // The same two conditions the decision path checks first. A customer without the
                    // capability still SEES the offer - they simply are not the one who answers it.
                    CanRespond: awaiting && mayRespond,
                    // Asking a human about it is NOT limited to offers still awaiting an answer: the
                    // most likely moment to want a conversation is about one that just expired, or one
                    // somebody else on your side already rejected.
                    CanRequestContact: mayAsk);
            }).ToList();
        }

        // =========================================================================================
        // THE CUSTOMER DECISION, and the transition matrix that is the whole point of this method.
        //
        // WHY THE PORTAL OWNS A MATRIX AT ALL. ISellingService.SetQuotationStatusAsync accepts Draft,
        // Sent, Accepted, Rejected and Expired and refuses only a Converted source - it is the STAFF
        // API, and it is deliberately broad because staff correct mistakes. Handing that reach to an
        // external caller would let a customer move a quotation from Rejected back to Sent, or stamp a
        // Draft they were never shown. So the portal states, in one place, the only transitions a
        // CUSTOMER may cause:
        //
        //     Sent  -> Accepted      the offer is taken
        //     Sent  -> Rejected      the offer is declined
        //
        // and nothing else. Draft is not customer business, Expired and Converted are closed, and
        // Accepted/Rejected are terminal for this actor. A transition absent from the matrix is refused
        // rather than passed down, so widening the staff API can never widen the portal by accident.
        //
        // IDEMPOTENT ON THE SAME ANSWER, CONFLICTING ON A DIFFERENT ONE. A customer who double-clicks
        // Accept gets the same success twice and the row is written once; a customer who accepted and
        // then rejects is refused, because the first answer already had contractual meaning. That
        // asymmetry is the reason this is not simply "if status == target return ok".
        //
        // EXPIRY IS A DATE, NOT ONLY A STATUS. A quotation whose ValidUntil has passed is expired
        // whether or not a sweep has written the word yet, and accepting one would create an agreement
        // on lapsed terms. The read side already derives AwaitingResponse the same way, so the two
        // halves of the portal agree about what is still answerable.
        public async Task<PortalDecisionResult> RespondToQuotationAsync(
            int quotationId, bool accept, CancellationToken ct = default)
        {
            // 1 - IDENTITY. Never a request parameter; the accessor fails closed on every missing part.
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx == null) return PortalDecisionResult.Refused(PortalDecisionResult.NotFound);

            // 2 - CAPABILITY. Reading quotations and answering them are different rights, and the
            // default grant deliberately withholds this one.
            if (!ctx.Can(PortalCapabilities.RespondToQuotations))
                return PortalDecisionResult.Refused(PortalDecisionResult.NotPermitted);

            if (quotationId <= 0) return PortalDecisionResult.Refused(PortalDecisionResult.NotFound);

            // 3 - OWNERSHIP, IN THE QUERY. Company AND customer are predicates, so a foreign row is
            // never materialised - it is not loaded and then refused. SetQuotationStatusAsync takes a
            // company but has no notion of a customer at all, which is precisely why the portal cannot
            // lean on it for ownership.
            var row = await _db.Quotations.AsNoTracking()
                .Where(q => q.ID == quotationId
                            && q.CompanyID == ctx.CompanyId
                            && q.CustomerId == ctx.CustomerId)
                .Select(q => new { q.Status, q.ValidUntil })
                .FirstOrDefaultAsync(ct);

            // Absent, another company's, another customer's - one answer for all three.
            if (row == null) return PortalDecisionResult.Refused(PortalDecisionResult.NotFound);

            var target = accept ? "Accepted" : "Rejected";

            // 4 - STATE. Draft is refused as NOT FOUND rather than as a wrong state: a draft is invisible
            // to this customer, and a distinct refusal would confirm that one exists.
            if (string.Equals(row.Status, "Draft", StringComparison.Ordinal))
                return PortalDecisionResult.Refused(PortalDecisionResult.NotFound);

            // Idempotent: the same answer again is the same answer, and writes nothing.
            if (string.Equals(row.Status, target, StringComparison.Ordinal))
                return PortalDecisionResult.Accepted(target);

            // Terminal for this actor. Converted is terminal for everyone - even staff cannot move it.
            if (string.Equals(row.Status, "Accepted", StringComparison.Ordinal)
                || string.Equals(row.Status, "Rejected", StringComparison.Ordinal)
                || string.Equals(row.Status, "Converted", StringComparison.Ordinal))
                return PortalDecisionResult.Refused(PortalDecisionResult.AlreadyDecided);

            if (string.Equals(row.Status, "Expired", StringComparison.Ordinal))
                return PortalDecisionResult.Refused(PortalDecisionResult.Expired);

            // Anything that is not Sent by now is a status this matrix does not recognise. REFUSE
            // rather than fall through: a status added to the domain later must be decided about
            // deliberately, not inherited silently by the portal.
            if (!string.Equals(row.Status, "Sent", StringComparison.Ordinal))
                return PortalDecisionResult.Refused(PortalDecisionResult.NotFound);

            if (row.ValidUntil is DateTime validUntil && validUntil.Date < DateTime.UtcNow.Date)
                return PortalDecisionResult.Refused(PortalDecisionResult.Expired);

            // 5 - THE AUTHORITATIVE WRITE. The module service owns the transition; the portal decided
            // only whether this caller may ask for it. The optional dependency is checked rather than
            // assumed so a host without selling wired refuses honestly instead of throwing.
            if (_selling == null) return PortalDecisionResult.Refused(PortalDecisionResult.Unavailable);

            // customerDecision: TRUE. This is the flag that turns the transition into a recorded
            // Quotation.*ByCustomer fact, inside the module's own transaction.
            var (ok, _) = await _selling.SetQuotationStatusAsync(
                ctx.CompanyId, quotationId, target, customerDecision: true);
            if (!ok) return PortalDecisionResult.Refused(PortalDecisionResult.AlreadyDecided);

            return PortalDecisionResult.Accepted(target);
        }

        // =========================================================================================
        // "PLEASE GET IN TOUCH" - the portal's second write, and its first ORIGINATED fact.
        //
        // The quotation decision answers something staff started. This one starts something, which is
        // why it is the first place an outsider puts a row in front of employees, and why the row
        // records its own provenance: company, customer, PORTAL USER, and what they actually typed.
        //
        // THE QUOTATION REFERENCE IS CHECKED, NOT TRUSTED. A caller may pass any integer. If it is not
        // a quotation belonging to this customer in this company AND visible to them, the whole request
        // is refused with the same not_found the decision path uses - not "saved without the link",
        // which would quietly turn a probe into a successful write. Refusing keeps the id from being a
        // way to find out which quotations exist.
        //
        // NO EVENT IS EMITTED, and that is a governance fact rather than an oversight. SHF-60 says in
        // terms that a Request Contact event is NOT authorized and belongs with this contract if and
        // when it lands. The row is the authoritative first write; the downstream notification and
        // orchestration gap is REPORTED rather than improvised by having this method create a
        // notification or a task directly, which is exactly the multi-side-effect logic the platform
        // keeps out of services like this one.
        // The shell's own read. It is deliberately NOT HomeAsync: that materialises quotations,
        // projects and invoices in full, and the band needs a name and three counts on every page.
        public async Task<PortalIdentityCard?> IdentityAsync(CancellationToken ct = default)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx == null) return null;

            var name = await _db.Customers.AsNoTracking()
                .Where(c => c.ID == ctx.CustomerId && c.CompanyID == ctx.CompanyId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct);
            if (name == null) return null;

            var today = DateTime.UtcNow.Date;
            var awaiting = ctx.Can(PortalCapabilities.ViewQuotations)
                ? await _db.Quotations.AsNoTracking().CountAsync(
                    q => q.CompanyID == ctx.CompanyId && q.CustomerId == ctx.CustomerId
                         && q.Status == "Sent"
                         && (q.ValidUntil == null || q.ValidUntil.Value.Date >= today), ct)
                : 0;

            var projects = ctx.Can(PortalCapabilities.ViewProjects)
                ? await _db.Projects.AsNoTracking().CountAsync(
                    p => p.CompanyID == ctx.CompanyId && p.CustomerId == ctx.CustomerId
                         && p.Status != "Completed" && p.Status != "Cancelled", ct)
                : 0;

            // Outstanding reuses the settled-amount rule rather than restating it, so the band and the
            // Invoices screen can never disagree about what is owed.
            var outstanding = ctx.Can(PortalCapabilities.ViewInvoices)
                ? (await InvoicesAsync(ct)).Where(i => i.Outstanding > 0).Sum(i => i.Outstanding)
                : 0m;

            return new PortalIdentityCard(name, InitialsOf(name), awaiting, projects, outstanding);
        }

        /// Up to two letters, taken from the first two words. Arabic and Latin alike - this is a
        /// character operation, not a language one.
        private static string InitialsOf(string name)
        {
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "?";
            var first = words[0][0].ToString();
            return words.Length == 1 ? first : first + words[1][0];
        }

        public async Task<PortalDecisionResult> RequestContactAsync(
            int? quotationId, string? message, CancellationToken ct = default)
        {
            // 1 - IDENTITY, then CAPABILITY. Same order and same fail-closed shape as the decision.
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx == null) return PortalDecisionResult.Refused(PortalDecisionResult.NotFound);

            if (!ctx.Can(PortalCapabilities.RequestContact))
                return PortalDecisionResult.Refused(PortalDecisionResult.NotPermitted);

            // 2 - THE OPTIONAL REFERENCE, owned or refused. Note the visibility list: a Draft is not
            // this customer's business, so naming one is refused exactly as an absent id is.
            int? quotation = null;
            if (quotationId is int qid)
            {
                if (qid <= 0) return PortalDecisionResult.Refused(PortalDecisionResult.NotFound);

                var owned = await _db.Quotations.AsNoTracking()
                    .AnyAsync(q => q.ID == qid
                                   && q.CompanyID == ctx.CompanyId
                                   && q.CustomerId == ctx.CustomerId
                                   && CustomerVisibleQuotationStatuses.Contains(q.Status), ct);

                if (!owned) return PortalDecisionResult.Refused(PortalDecisionResult.NotFound);
                quotation = qid;
            }

            // 3 - IDEMPOTENCY, by OPEN request rather than by a key. Asking twice about the same thing
            // while the first ask is still open is the same ask; once staff have handled it, a new
            // request is a genuinely new one and is allowed. The predicate carries company AND customer
            // so it can never match somebody else's row.
            var alreadyOpen = await _db.Set<PortalContactRequest>().AsNoTracking()
                .AnyAsync(r => r.CompanyID == ctx.CompanyId
                               && r.CustomerId == ctx.CustomerId
                               && r.QuotationId == quotation
                               && r.Status == PortalContactRequestStatus.Requested, ct);

            if (alreadyOpen) return PortalDecisionResult.Refused(PortalDecisionResult.AlreadyRequested);

            // 4 - THE WRITE. Every scalar comes from the resolved identity or from the checked
            // reference. The message is the one caller-supplied value, and it is trimmed and bounded
            // here as well as in the column: a service that relies on the database to enforce a length
            // is a service that throws where it should refuse.
            var text = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
            if (text != null && text.Length > PortalContactRequestModel.MaxMessageLength)
                text = text.Substring(0, PortalContactRequestModel.MaxMessageLength);

            _db.Set<PortalContactRequest>().Add(new PortalContactRequest
            {
                CompanyID = ctx.CompanyId,
                CustomerId = ctx.CustomerId,
                PortalUserId = ctx.PortalUserId,
                QuotationId = quotation,
                RequestedAt = DateTime.UtcNow,
                Message = text,
                Status = PortalContactRequestStatus.Requested,
            });

            await _db.SaveChangesAsync(ct);
            return PortalDecisionResult.Accepted(PortalContactRequestStatus.Requested);
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
