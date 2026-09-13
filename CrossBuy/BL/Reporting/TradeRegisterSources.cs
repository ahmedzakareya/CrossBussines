using Microsoft.EntityFrameworkCore;
using CrossBuy.Models.Context;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // THE REGISTER SOURCES — five lists, one shape out.
    //
    // Each asks a different table for the same five things: number, date, other party, status, total.
    // What differs is the table, the column NAMES on it, and which party table to join — so the query
    // is expressed per register and the row-building, the search predicate, the cap and the truncation
    // handling are written once below.
    //
    // TENANCY IS IN THE QUERY, never applied after the fact: a row belonging to another company is not
    // fetched rather than fetched and then filtered, so a bug in the filtering cannot leak one.
    //
    // THE SEARCH PREDICATE MATCHES THE SCREEN'S. Each list already searches the document number and
    // both spellings of the party name; a register that searched differently would disagree with the
    // list it was printed from, and the reader would have no way to see it.
    // ============================================================================================
    internal readonly record struct RegisterRow(
        string? DocumentNo, DateTime DocumentDate, string? PartyAr, string? PartyEn,
        string? Status, decimal Total);

    internal static class TradeRegisterEmit
    {
        // `org` defaults to nothing on purpose: the early returns above it are the "no company resolved"
        // path, and there is no issuer to name when there is no company.
        public static ReportDataSet Emit(ReportDataQuery query, IReadOnlyList<RegisterRow> rows,
                                         bool truncated, bool arabic, List<ReportFilter> applied,
                                         TradeDocumentOrg org = default)
        {
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            foreach (var r in rows)
                builder.AddRow(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["DocumentNo"] = r.DocumentNo,
                    ["DocumentDate"] = r.DocumentDate,
                    ["PartyName"] = AccountingSourceHelpers.Pick(arabic, r.PartyAr, r.PartyEn),
                    ["Status"] = AccountingSourceHelpers.StatusLabel(r.Status, arabic),
                    ["GrandTotal"] = r.Total,

                    // Repeated per row so a template can bind them into a header band — the same reason
                    // and the same keys as the documents.
                    ["CompanyName"] = org.CompanyName,
                    ["CompanyTaxNo"] = org.CompanyTaxNo,
                    ["BranchName"] = org.BranchName,
                });

            return builder.Build(truncated, truncated ? null : rows.Count, applied,
                new[] { ReportSort.By("DocumentDate", descending: true) });
        }

        public static bool Matches(string? haystackA, string? haystackB, string? number, string term) =>
            (number != null && number.Contains(term))
            || (haystackA != null && haystackA.Contains(term))
            || (haystackB != null && haystackB.Contains(term));
    }

    public sealed class PurchaseOrderRegisterSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public PurchaseOrderRegisterSource(CrossDbContext db) { _db = db; }
        public string Key => TradeRegisterCodes.PurchaseOrders;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery q, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(q);
            if (q.Context.CompanyId <= 0) return TradeRegisterEmit.Emit(q, Array.Empty<RegisterRow>(), false, false, new());
            var search = q.Parameters.GetString("Search");
            var status = q.Parameters.GetString("Status");
            var applied = new List<ReportFilter>();

            // FILTERED AND ORDERED ON THE ENTITIES, projected afterwards. Ordering by a member of a
            // struct the query constructs is not translatable — EF refused the whole statement — so the
            // shape stays anonymous until the rows are in memory.
            var q0 = from d in _db.PurchaseOrders.AsNoTracking().Where(d => d.CompanyID == q.Context.CompanyId)
                     join party in _db.Vendors.AsNoTracking() on d.VendorId equals party.ID into pj
                     from party in pj.DefaultIfEmpty()
                     select new { d, party };

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                q0 = q0.Where(x => (x.d.OrderNo != null && x.d.OrderNo.Contains(s))
                                || (x.party != null && x.party.Name.Contains(s))
                                || (x.party != null && x.party.NameEn != null && x.party.NameEn.Contains(s)));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                q0 = q0.Where(x => x.d.Status == status);
                applied.Add(ReportFilter.Eq("Status", status));
            }

            int cap = q.MaxRows > 0 ? q.MaxRows : TradeRegisterDatasets.MaxRows;

            var fetched = await q0
                .OrderByDescending(x => x.d.OrderDate)
                .Take(cap + 1)
                .Select(x => new
                {
                    Number = x.d.OrderNo,
                    Date = x.d.OrderDate,
                    PartyAr = x.party != null ? x.party.Name : null,
                    PartyEn = x.party != null ? x.party.NameEn : null,
                    x.d.Status,
                    Total = x.d.GrandTotal,
                })
                .ToListAsync(ct);

            bool truncated = fetched.Count > cap;
            if (truncated) fetched = fetched.Take(cap).ToList();

            var list = fetched
                .Select(x => new RegisterRow(x.Number, x.Date, x.PartyAr, x.PartyEn, x.Status, x.Total))
                .ToList();

            bool arabic = AccountingSourceHelpers.Arabic(q);
            return TradeRegisterEmit.Emit(q, list, truncated, arabic, applied,
                await TradeDocumentOrg.LoadAsync(_db, q.Context, arabic, ct));
        }
    }

    public sealed class SalesOrderRegisterSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public SalesOrderRegisterSource(CrossDbContext db) { _db = db; }
        public string Key => TradeRegisterCodes.SalesOrders;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery q, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(q);
            if (q.Context.CompanyId <= 0) return TradeRegisterEmit.Emit(q, Array.Empty<RegisterRow>(), false, false, new());
            var search = q.Parameters.GetString("Search");
            var status = q.Parameters.GetString("Status");
            var applied = new List<ReportFilter>();

            // FILTERED AND ORDERED ON THE ENTITIES, projected afterwards. Ordering by a member of a
            // struct the query constructs is not translatable — EF refused the whole statement — so the
            // shape stays anonymous until the rows are in memory.
            var q0 = from d in _db.SalesOrders.AsNoTracking().Where(d => d.CompanyID == q.Context.CompanyId)
                     join party in _db.Customers.AsNoTracking() on d.CustomerId equals party.ID into pj
                     from party in pj.DefaultIfEmpty()
                     select new { d, party };

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                q0 = q0.Where(x => (x.d.OrderNo != null && x.d.OrderNo.Contains(s))
                                || (x.party != null && x.party.Name.Contains(s))
                                || (x.party != null && x.party.NameEn != null && x.party.NameEn.Contains(s)));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                q0 = q0.Where(x => x.d.Status == status);
                applied.Add(ReportFilter.Eq("Status", status));
            }

            int cap = q.MaxRows > 0 ? q.MaxRows : TradeRegisterDatasets.MaxRows;

            var fetched = await q0
                .OrderByDescending(x => x.d.OrderDate)
                .Take(cap + 1)
                .Select(x => new
                {
                    Number = x.d.OrderNo,
                    Date = x.d.OrderDate,
                    PartyAr = x.party != null ? x.party.Name : null,
                    PartyEn = x.party != null ? x.party.NameEn : null,
                    x.d.Status,
                    Total = x.d.GrandTotal,
                })
                .ToListAsync(ct);

            bool truncated = fetched.Count > cap;
            if (truncated) fetched = fetched.Take(cap).ToList();

            var list = fetched
                .Select(x => new RegisterRow(x.Number, x.Date, x.PartyAr, x.PartyEn, x.Status, x.Total))
                .ToList();

            bool arabic = AccountingSourceHelpers.Arabic(q);
            return TradeRegisterEmit.Emit(q, list, truncated, arabic, applied,
                await TradeDocumentOrg.LoadAsync(_db, q.Context, arabic, ct));
        }
    }

    public sealed class GoodsReceiptRegisterSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public GoodsReceiptRegisterSource(CrossDbContext db) { _db = db; }
        public string Key => TradeRegisterCodes.GoodsReceipts;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery q, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(q);
            if (q.Context.CompanyId <= 0) return TradeRegisterEmit.Emit(q, Array.Empty<RegisterRow>(), false, false, new());
            var search = q.Parameters.GetString("Search");
            var status = q.Parameters.GetString("Status");
            var applied = new List<ReportFilter>();

            // FILTERED AND ORDERED ON THE ENTITIES, projected afterwards. Ordering by a member of a
            // struct the query constructs is not translatable — EF refused the whole statement — so the
            // shape stays anonymous until the rows are in memory.
            var q0 = from d in _db.GoodsReceipts.AsNoTracking().Where(d => d.CompanyID == q.Context.CompanyId)
                     join party in _db.Vendors.AsNoTracking() on d.VendorId equals party.ID into pj
                     from party in pj.DefaultIfEmpty()
                     select new { d, party };

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                q0 = q0.Where(x => (x.d.ReceiptNo != null && x.d.ReceiptNo.Contains(s))
                                || (x.party != null && x.party.Name.Contains(s))
                                || (x.party != null && x.party.NameEn != null && x.party.NameEn.Contains(s)));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                q0 = q0.Where(x => x.d.Status == status);
                applied.Add(ReportFilter.Eq("Status", status));
            }

            int cap = q.MaxRows > 0 ? q.MaxRows : TradeRegisterDatasets.MaxRows;

            var fetched = await q0
                .OrderByDescending(x => x.d.ReceiptDate)
                .Take(cap + 1)
                .Select(x => new
                {
                    Number = x.d.ReceiptNo,
                    Date = x.d.ReceiptDate,
                    PartyAr = x.party != null ? x.party.Name : null,
                    PartyEn = x.party != null ? x.party.NameEn : null,
                    x.d.Status,
                    Total = x.d.TotalCost,
                })
                .ToListAsync(ct);

            bool truncated = fetched.Count > cap;
            if (truncated) fetched = fetched.Take(cap).ToList();

            var list = fetched
                .Select(x => new RegisterRow(x.Number, x.Date, x.PartyAr, x.PartyEn, x.Status, x.Total))
                .ToList();

            bool arabic = AccountingSourceHelpers.Arabic(q);
            return TradeRegisterEmit.Emit(q, list, truncated, arabic, applied,
                await TradeDocumentOrg.LoadAsync(_db, q.Context, arabic, ct));
        }
    }

    public sealed class PurchaseInvoiceRegisterSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public PurchaseInvoiceRegisterSource(CrossDbContext db) { _db = db; }
        public string Key => TradeRegisterCodes.PurchaseInvoices;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery q, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(q);
            if (q.Context.CompanyId <= 0) return TradeRegisterEmit.Emit(q, Array.Empty<RegisterRow>(), false, false, new());
            var search = q.Parameters.GetString("Search");
            var status = q.Parameters.GetString("Status");
            var applied = new List<ReportFilter>();

            // FILTERED AND ORDERED ON THE ENTITIES, projected afterwards. Ordering by a member of a
            // struct the query constructs is not translatable — EF refused the whole statement — so the
            // shape stays anonymous until the rows are in memory.
            var q0 = from d in _db.PurchaseInvoices.AsNoTracking().Where(d => d.CompanyID == q.Context.CompanyId)
                     join party in _db.Vendors.AsNoTracking() on d.VendorId equals party.ID into pj
                     from party in pj.DefaultIfEmpty()
                     select new { d, party };

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                q0 = q0.Where(x => (x.d.InvoiceNo != null && x.d.InvoiceNo.Contains(s))
                                || (x.party != null && x.party.Name.Contains(s))
                                || (x.party != null && x.party.NameEn != null && x.party.NameEn.Contains(s)));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                q0 = q0.Where(x => x.d.Status == status);
                applied.Add(ReportFilter.Eq("Status", status));
            }

            int cap = q.MaxRows > 0 ? q.MaxRows : TradeRegisterDatasets.MaxRows;

            var fetched = await q0
                .OrderByDescending(x => x.d.InvoiceDate)
                .Take(cap + 1)
                .Select(x => new
                {
                    Number = x.d.InvoiceNo,
                    Date = x.d.InvoiceDate,
                    PartyAr = x.party != null ? x.party.Name : null,
                    PartyEn = x.party != null ? x.party.NameEn : null,
                    x.d.Status,
                    Total = x.d.GrandTotal,
                })
                .ToListAsync(ct);

            bool truncated = fetched.Count > cap;
            if (truncated) fetched = fetched.Take(cap).ToList();

            var list = fetched
                .Select(x => new RegisterRow(x.Number, x.Date, x.PartyAr, x.PartyEn, x.Status, x.Total))
                .ToList();

            bool arabic = AccountingSourceHelpers.Arabic(q);
            return TradeRegisterEmit.Emit(q, list, truncated, arabic, applied,
                await TradeDocumentOrg.LoadAsync(_db, q.Context, arabic, ct));
        }
    }

    public sealed class SalesInvoiceRegisterSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public SalesInvoiceRegisterSource(CrossDbContext db) { _db = db; }
        public string Key => TradeRegisterCodes.SalesInvoices;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery q, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(q);
            if (q.Context.CompanyId <= 0) return TradeRegisterEmit.Emit(q, Array.Empty<RegisterRow>(), false, false, new());
            var search = q.Parameters.GetString("Search");
            var status = q.Parameters.GetString("Status");
            var applied = new List<ReportFilter>();

            // FILTERED AND ORDERED ON THE ENTITIES, projected afterwards. Ordering by a member of a
            // struct the query constructs is not translatable — EF refused the whole statement — so the
            // shape stays anonymous until the rows are in memory.
            var q0 = from d in _db.SalesInvoices.AsNoTracking().Where(d => d.CompanyID == q.Context.CompanyId)
                     join party in _db.Customers.AsNoTracking() on d.CustomerId equals party.ID into pj
                     from party in pj.DefaultIfEmpty()
                     select new { d, party };

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                q0 = q0.Where(x => (x.d.InvoiceNo != null && x.d.InvoiceNo.Contains(s))
                                || (x.party != null && x.party.Name.Contains(s))
                                || (x.party != null && x.party.NameEn != null && x.party.NameEn.Contains(s)));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                q0 = q0.Where(x => x.d.Status == status);
                applied.Add(ReportFilter.Eq("Status", status));
            }

            int cap = q.MaxRows > 0 ? q.MaxRows : TradeRegisterDatasets.MaxRows;

            var fetched = await q0
                .OrderByDescending(x => x.d.InvoiceDate)
                .Take(cap + 1)
                .Select(x => new
                {
                    Number = x.d.InvoiceNo,
                    Date = x.d.InvoiceDate,
                    PartyAr = x.party != null ? x.party.Name : null,
                    PartyEn = x.party != null ? x.party.NameEn : null,
                    x.d.Status,
                    Total = x.d.GrandTotal,
                    Override = x.d.CustomerNameOverride,
                })
                .ToListAsync(ct);

            bool truncated = fetched.Count > cap;
            if (truncated) fetched = fetched.Take(cap).ToList();

            var list = fetched
                .Select(x => new RegisterRow(x.Number, x.Date, string.IsNullOrWhiteSpace(x.Override) ? x.PartyAr : x.Override, string.IsNullOrWhiteSpace(x.Override) ? x.PartyEn : x.Override, x.Status, x.Total))
                .ToList();

            bool arabic = AccountingSourceHelpers.Arabic(q);
            return TradeRegisterEmit.Emit(q, list, truncated, arabic, applied,
                await TradeDocumentOrg.LoadAsync(_db, q.Context, arabic, ct));
        }
    }
}
