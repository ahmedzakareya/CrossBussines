using Microsoft.EntityFrameworkCore;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // THE DOCUMENTS THEMSELVES — one entry point per document, one shape out.
    //
    // Each source answers the same question about a different table: "give me this document's header
    // and its lines, as rows a template can lay out". The FIELD KEYS are identical across all three
    // because the definition is shared (TradeDocumentDatasets), which is what lets one house layout
    // print an invoice and a quotation without being authored twice.
    //
    // TENANCY IS CHECKED ON THE HEADER, never inferred from the id. The id arrives from a URL, so the
    // header is loaded WITH CompanyID = the resolved context and a miss returns an EMPTY set — the same
    // "no such document, as far as you are concerned" the rest of the platform gives, which also does
    // not confirm that the id exists.
    //
    // AND THE LINES ARE FILTERED BY THE DOCUMENT, not by company: they carry no CompanyID of their own,
    // and they are only ever reached through a header this caller was already allowed to load.
    // ============================================================================================
    // WHO ISSUED THE DOCUMENT. A printed invoice is the issuing business's paper, and until now the
    // dataset only knew the counterparty — so a template could head the page with the customer and
    // nothing else. This reads the company, and the branch the document is being printed FROM.
    //
    // THE BRANCH IS THE CONTEXT'S, NOT THE DOCUMENT'S, and that is a fact about the schema rather than a
    // shortcut: SalesInvoice, PurchaseInvoice, Quotation and PurchaseOrder all carry CompanyID and no
    // BranchID. So it is BusinessContext.BranchId — the employee's branch — and it stays EMPTY when the
    // context has none. Falling back to "the company's first branch" would print a fact the business
    // never recorded, on a document a customer keeps.
    //
    // The branch read is filtered by company as well as by id. It comes from the resolved context and not
    // from a URL, but a source that reaches a row without its tenancy predicate is one refactor away
    // from being the hole.
    internal readonly record struct TradeDocumentOrg(
        string? CompanyName, string? CompanyTaxNo, string? CompanyAddress, string? CompanyPhone,
        string? BranchName, string? BranchLocation, string? BranchPhone)
    {
        public static async Task<TradeDocumentOrg> LoadAsync(
            CrossDbContext db, BusinessContext context, bool arabic, CancellationToken ct)
        {
            var company = await db.Companies.AsNoTracking()
                .Where(c => c.CompanyID == context.CompanyId)
                .Select(c => new
                {
                    // ComoanyNameAr is the model's own spelling of the Arabic name column.
                    Name = arabic ? (c.ComoanyNameAr ?? c.CompanyName) : (c.CompanyName ?? c.ComoanyNameAr),
                    c.TaxNumber, c.Address, c.PhoneNumber,
                })
                .FirstOrDefaultAsync(ct);

            var branch = context.BranchId is > 0
                ? await db.Branches.AsNoTracking()
                    .Where(b => b.ID == context.BranchId!.Value && b.CompanyID == context.CompanyId)
                    .Select(b => new
                    {
                        Name = arabic ? (b.NameAr ?? b.Name) : (b.Name ?? b.NameAr),
                        b.Location, b.PhoneNumber,
                    })
                    .FirstOrDefaultAsync(ct)
                : null;

            return new TradeDocumentOrg(
                company?.Name, company?.TaxNumber, company?.Address, company?.PhoneNumber,
                branch?.Name, branch?.Location, branch?.PhoneNumber);
        }
    }

    internal static class TradeDocumentRows
    {
        // Every source builds the same row, so the shape is written once. A missing key here is a blank
        // cell in three documents at once, which is easier to notice than one.
        public static Dictionary<string, object?> Row(
            int lineNo, string? itemCode, string? description,
            decimal qty, decimal unitPrice, decimal discount, decimal taxRate, decimal lineTotal,
            string? documentNo, DateTime documentDate, string? partyName, string? status, string? notes,
            decimal subTotal, decimal taxTotal, decimal grandTotal, TradeDocumentOrg org) =>
            new(StringComparer.Ordinal)
            {
                ["LineNo"] = lineNo,
                ["ItemCode"] = itemCode,
                ["ItemDescription"] = description,
                ["Qty"] = qty,
                ["UnitPrice"] = unitPrice,
                ["DiscountAmount"] = discount,
                ["TaxRate"] = taxRate,
                ["LineTotal"] = lineTotal,

                ["DocumentNo"] = documentNo,
                ["DocumentDate"] = documentDate,
                ["PartyName"] = partyName,
                ["Status"] = status,
                ["Notes"] = notes,
                ["SubTotal"] = subTotal,
                ["TaxTotal"] = taxTotal,
                ["GrandTotal"] = grandTotal,

                ["CompanyName"] = org.CompanyName,
                ["CompanyTaxNo"] = org.CompanyTaxNo,
                ["CompanyAddress"] = org.CompanyAddress,
                ["CompanyPhone"] = org.CompanyPhone,
                ["BranchName"] = org.BranchName,
                ["BranchLocation"] = org.BranchLocation,
                ["BranchPhone"] = org.BranchPhone,
            };
    }

    public sealed class SalesInvoiceDocumentSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public SalesInvoiceDocumentSource(CrossDbContext db) { _db = db; }

        public string Key => TradeDocumentDatasetCodes.SalesInvoice;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var id = query.Parameters.GetInt("InvoiceId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);

            var header = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.ID == id.Value && i.CompanyID == context.CompanyId)
                .Select(i => new
                {
                    i.InvoiceNo, i.InvoiceDate, i.Status, i.Notes,
                    i.SubTotal, i.TaxTotal, i.GrandTotal,
                    i.CustomerId, i.CustomerNameOverride,
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (header is null) return builder.Build(totalRowCount: 0);

            bool arabic = AccountingSourceHelpers.Arabic(query);
            var org = await TradeDocumentOrg.LoadAsync(_db, context, arabic, cancellationToken);

            // The OVERRIDE WINS when it is set. A one-off customer name typed on the invoice is what the
            // document was issued to, and printing the master record's name instead would make the
            // printed copy disagree with the one the customer holds.
            var party = header.CustomerNameOverride;
            if (string.IsNullOrWhiteSpace(party))
                party = await _db.Customers.AsNoTracking()
                    .Where(c => c.ID == header.CustomerId)
                    .Select(c => arabic ? c.Name : (c.NameEn ?? c.Name))
                    .FirstOrDefaultAsync(cancellationToken);

            int cap = query.MaxRows > 0 ? query.MaxRows : TradeDocumentDatasets.MaxRows;
            var lines = await _db.SalesInvoiceLines.AsNoTracking()
                .Where(l => l.SalesInvoiceId == id.Value)
                .OrderBy(l => l.LineNo)
                .Take(cap + 1)
                .ToListAsync(cancellationToken);

            bool truncated = lines.Count > cap;
            if (truncated) lines = lines.Take(cap).ToList();

            foreach (var l in lines)
                builder.AddRow(TradeDocumentRows.Row(
                    l.LineNo, l.ItemCode,
                    AccountingSourceHelpers.Pick(arabic, l.ItemDescription, l.ItemDescriptionEn),
                    l.Qty, l.UnitPrice, l.DiscountAmount, l.TaxRate, l.LineTotal,
                    header.InvoiceNo, header.InvoiceDate, party,
                    AccountingSourceHelpers.StatusLabel(header.Status, arabic), header.Notes,
                    header.SubTotal, header.TaxTotal, header.GrandTotal, org));

            return builder.Build(truncated, truncated ? null : lines.Count,
                Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }

    public sealed class PurchaseInvoiceDocumentSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public PurchaseInvoiceDocumentSource(CrossDbContext db) { _db = db; }

        public string Key => TradeDocumentDatasetCodes.PurchaseInvoice;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var id = query.Parameters.GetInt("InvoiceId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);

            var header = await _db.PurchaseInvoices.AsNoTracking()
                .Where(i => i.ID == id.Value && i.CompanyID == context.CompanyId)
                .Select(i => new
                {
                    i.InvoiceNo, i.InvoiceDate, i.Status, i.Notes,
                    i.SubTotal, i.TaxTotal, i.GrandTotal, i.VendorId,
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (header is null) return builder.Build(totalRowCount: 0);

            bool arabic = AccountingSourceHelpers.Arabic(query);
            var org = await TradeDocumentOrg.LoadAsync(_db, context, arabic, cancellationToken);

            var party = await _db.Vendors.AsNoTracking()
                .Where(v => v.ID == header.VendorId)
                .Select(v => arabic ? v.Name : (v.NameEn ?? v.Name))
                .FirstOrDefaultAsync(cancellationToken);

            int cap = query.MaxRows > 0 ? query.MaxRows : TradeDocumentDatasets.MaxRows;
            var lines = await _db.PurchaseInvoiceLines.AsNoTracking()
                .Where(l => l.PurchaseInvoiceId == id.Value)
                .OrderBy(l => l.LineNo)
                .Take(cap + 1)
                .ToListAsync(cancellationToken);

            bool truncated = lines.Count > cap;
            if (truncated) lines = lines.Take(cap).ToList();

            foreach (var l in lines)
                builder.AddRow(TradeDocumentRows.Row(
                    l.LineNo, null,
                    AccountingSourceHelpers.Pick(arabic, l.ItemDescription, l.ItemDescriptionEn),
                    l.Qty, l.UnitPrice, l.DiscountAmount, l.TaxRate, l.LineTotal,
                    header.InvoiceNo, header.InvoiceDate, party,
                    AccountingSourceHelpers.StatusLabel(header.Status, arabic), header.Notes,
                    header.SubTotal, header.TaxTotal, header.GrandTotal, org));

            return builder.Build(truncated, truncated ? null : lines.Count,
                Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }

    public sealed class QuotationDocumentSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public QuotationDocumentSource(CrossDbContext db) { _db = db; }

        public string Key => TradeDocumentDatasetCodes.Quotation;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var id = query.Parameters.GetInt("QuotationId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);

            var header = await _db.Quotations.AsNoTracking()
                .Where(q => q.ID == id.Value && q.CompanyID == context.CompanyId)
                .Select(q => new
                {
                    q.QuoteNo, q.QuoteDate, q.Status, q.Notes,
                    q.SubTotal, q.TaxTotal, q.GrandTotal, q.CustomerId,
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (header is null) return builder.Build(totalRowCount: 0);

            bool arabic = AccountingSourceHelpers.Arabic(query);
            var org = await TradeDocumentOrg.LoadAsync(_db, context, arabic, cancellationToken);

            var party = await _db.Customers.AsNoTracking()
                .Where(c => c.ID == header.CustomerId)
                .Select(c => arabic ? c.Name : (c.NameEn ?? c.Name))
                .FirstOrDefaultAsync(cancellationToken);

            int cap = query.MaxRows > 0 ? query.MaxRows : TradeDocumentDatasets.MaxRows;

            // THE ITEM CODE IS JOINED, because a quotation line carries an item id and a description but
            // no code, and a quotation without codes is one the customer cannot order from.
            var lines = await (
                from l in _db.QuotationLines.AsNoTracking()
                where l.QuotationId == id.Value
                join it in _db.Items.AsNoTracking() on l.ItemId equals it.ID into ij
                from it in ij.DefaultIfEmpty()
                orderby l.LineNo
                select new
                {
                    l.LineNo, l.ItemDescription, l.Qty, l.UnitPrice,
                    l.DiscountAmount, l.TaxRate, l.LineTotal,
                    ItemCode = it != null ? it.ItemCode : null,
                })
                .Take(cap + 1)
                .ToListAsync(cancellationToken);

            bool truncated = lines.Count > cap;
            if (truncated) lines = lines.Take(cap).ToList();

            foreach (var l in lines)
                builder.AddRow(TradeDocumentRows.Row(
                    l.LineNo, l.ItemCode, l.ItemDescription,
                    l.Qty, l.UnitPrice, l.DiscountAmount, l.TaxRate, l.LineTotal,
                    header.QuoteNo, header.QuoteDate, party,
                    AccountingSourceHelpers.StatusLabel(header.Status, arabic), header.Notes,
                    header.SubTotal, header.TaxTotal, header.GrandTotal, org));

            return builder.Build(truncated, truncated ? null : lines.Count,
                Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }
    public sealed class PurchaseOrderDocumentSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public PurchaseOrderDocumentSource(CrossDbContext db) { _db = db; }

        public string Key => TradeDocumentDatasetCodes.PurchaseOrder;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var id = query.Parameters.GetInt("OrderId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);

            var header = await _db.PurchaseOrders.AsNoTracking()
                .Where(o => o.ID == id.Value && o.CompanyID == context.CompanyId)
                .Select(o => new
                {
                    o.OrderNo, o.OrderDate, o.Status, o.Notes,
                    o.SubTotal, o.TaxTotal, o.GrandTotal, o.VendorId,
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (header is null) return builder.Build(totalRowCount: 0);

            bool arabic = AccountingSourceHelpers.Arabic(query);
            var org = await TradeDocumentOrg.LoadAsync(_db, context, arabic, cancellationToken);

            var party = await _db.Vendors.AsNoTracking()
                .Where(v => v.ID == header.VendorId)
                .Select(v => arabic ? v.Name : (v.NameEn ?? v.Name))
                .FirstOrDefaultAsync(cancellationToken);

            int cap = query.MaxRows > 0 ? query.MaxRows : TradeDocumentDatasets.MaxRows;

            // THE ITEM CODE IS JOINED. An order line carries an item id and a free-text description; an
            // order a vendor is expected to fulfil without codes is one they will guess at.
            var lines = await (
                from l in _db.PurchaseOrderLines.AsNoTracking()
                where l.PurchaseOrderId == id.Value
                join it in _db.Items.AsNoTracking() on l.ItemId equals it.ID into ij
                from it in ij.DefaultIfEmpty()
                orderby l.LineNo
                select new
                {
                    l.LineNo, l.ItemDescription, l.Qty, l.UnitPrice,
                    l.DiscountAmount, l.TaxRate, l.LineTotal,
                    ItemCode = it != null ? it.ItemCode : null,
                })
                .Take(cap + 1)
                .ToListAsync(cancellationToken);

            bool truncated = lines.Count > cap;
            if (truncated) lines = lines.Take(cap).ToList();

            foreach (var l in lines)
                builder.AddRow(TradeDocumentRows.Row(
                    l.LineNo, l.ItemCode, l.ItemDescription,
                    l.Qty, l.UnitPrice, l.DiscountAmount, l.TaxRate, l.LineTotal,
                    header.OrderNo, header.OrderDate, party,
                    AccountingSourceHelpers.StatusLabel(header.Status, arabic), header.Notes,
                    header.SubTotal, header.TaxTotal, header.GrandTotal, org));

            return builder.Build(truncated, truncated ? null : lines.Count,
                Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }
    public sealed class SalesReturnDocumentSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public SalesReturnDocumentSource(CrossDbContext db) { _db = db; }

        public string Key => TradeDocumentDatasetCodes.SalesReturn;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var id = query.Parameters.GetInt("ReturnId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);

            var header = await _db.SalesReturns.AsNoTracking()
                .Where(r => r.ID == id.Value && r.CompanyID == context.CompanyId)
                .Select(r => new
                {
                    r.ReturnNo, r.ReturnDate, r.Status, r.Notes,
                    r.SubTotal, r.TaxTotal, r.GrandTotal, r.CustomerId,
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (header is null) return builder.Build(totalRowCount: 0);

            bool arabic = AccountingSourceHelpers.Arabic(query);
            var org = await TradeDocumentOrg.LoadAsync(_db, context, arabic, cancellationToken);

            var party = await _db.Customers.AsNoTracking()
                .Where(c => c.ID == header.CustomerId)
                .Select(c => arabic ? c.Name : (c.NameEn ?? c.Name))
                .FirstOrDefaultAsync(cancellationToken);

            int cap = query.MaxRows > 0 ? query.MaxRows : TradeDocumentDatasets.MaxRows;
            var lines = await _db.SalesReturnLines.AsNoTracking()
                .Where(l => l.SalesReturnId == id.Value)
                .OrderBy(l => l.LineNo)
                .Take(cap + 1)
                .ToListAsync(cancellationToken);

            bool truncated = lines.Count > cap;
            if (truncated) lines = lines.Take(cap).ToList();

            foreach (var l in lines)
                builder.AddRow(TradeDocumentRows.Row(
                    l.LineNo, null, l.ItemDescription,
                    l.Qty, l.UnitPrice, l.DiscountAmount, l.TaxRate, l.LineTotal,
                    header.ReturnNo, header.ReturnDate, party,
                    AccountingSourceHelpers.StatusLabel(header.Status, arabic), header.Notes,
                    header.SubTotal, header.TaxTotal, header.GrandTotal, org));

            return builder.Build(truncated, truncated ? null : lines.Count,
                Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }

    public sealed class PurchaseReturnDocumentSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public PurchaseReturnDocumentSource(CrossDbContext db) { _db = db; }

        public string Key => TradeDocumentDatasetCodes.PurchaseReturn;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var id = query.Parameters.GetInt("ReturnId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);

            var header = await _db.PurchaseReturns.AsNoTracking()
                .Where(r => r.ID == id.Value && r.CompanyID == context.CompanyId)
                .Select(r => new
                {
                    r.ReturnNo, r.ReturnDate, r.Status, r.Notes,
                    r.SubTotal, r.TaxTotal, r.GrandTotal, r.VendorId,
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (header is null) return builder.Build(totalRowCount: 0);

            bool arabic = AccountingSourceHelpers.Arabic(query);
            var org = await TradeDocumentOrg.LoadAsync(_db, context, arabic, cancellationToken);

            var party = await _db.Vendors.AsNoTracking()
                .Where(v => v.ID == header.VendorId)
                .Select(v => arabic ? v.Name : (v.NameEn ?? v.Name))
                .FirstOrDefaultAsync(cancellationToken);

            int cap = query.MaxRows > 0 ? query.MaxRows : TradeDocumentDatasets.MaxRows;
            var lines = await _db.PurchaseReturnLines.AsNoTracking()
                .Where(l => l.PurchaseReturnId == id.Value)
                .OrderBy(l => l.LineNo)
                .Take(cap + 1)
                .ToListAsync(cancellationToken);

            bool truncated = lines.Count > cap;
            if (truncated) lines = lines.Take(cap).ToList();

            foreach (var l in lines)
                builder.AddRow(TradeDocumentRows.Row(
                    l.LineNo, null, l.ItemDescription,
                    // A PURCHASE RETURN IS PRICED AT COST, not at a selling price: the line carries
                    // UnitCost (the average cost at the moment of return) and no discount, because a
                    // return to a vendor reverses what the stock was worth, not what it was sold for.
                    l.Qty, l.UnitCost, 0m, l.TaxRate, l.LineTotal,
                    header.ReturnNo, header.ReturnDate, party,
                    AccountingSourceHelpers.StatusLabel(header.Status, arabic), header.Notes,
                    header.SubTotal, header.TaxTotal, header.GrandTotal, org));

            return builder.Build(truncated, truncated ? null : lines.Count,
                Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }
    public sealed class SalesOrderDocumentSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public SalesOrderDocumentSource(CrossDbContext db) { _db = db; }

        public string Key => TradeDocumentDatasetCodes.SalesOrder;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var id = query.Parameters.GetInt("OrderId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);

            var header = await _db.SalesOrders.AsNoTracking()
                .Where(o => o.ID == id.Value && o.CompanyID == context.CompanyId)
                .Select(o => new
                {
                    o.OrderNo, o.OrderDate, o.Status, o.Notes,
                    o.SubTotal, o.TaxTotal, o.GrandTotal, o.CustomerId,
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (header is null) return builder.Build(totalRowCount: 0);

            bool arabic = AccountingSourceHelpers.Arabic(query);
            var org = await TradeDocumentOrg.LoadAsync(_db, context, arabic, cancellationToken);

            var party = await _db.Customers.AsNoTracking()
                .Where(c => c.ID == header.CustomerId)
                .Select(c => arabic ? c.Name : (c.NameEn ?? c.Name))
                .FirstOrDefaultAsync(cancellationToken);

            int cap = query.MaxRows > 0 ? query.MaxRows : TradeDocumentDatasets.MaxRows;

            // THE ITEM CODE IS JOINED. An order line carries an item id and a free-text description;
            // an order a warehouse is expected to pick without codes is one they will guess at.
            var lines = await (
                from l in _db.SalesOrderLines.AsNoTracking()
                where l.SalesOrderId == id.Value
                join it in _db.Items.AsNoTracking() on l.ItemId equals it.ID into ij
                from it in ij.DefaultIfEmpty()
                orderby l.LineNo
                select new
                {
                    l.LineNo, l.ItemDescription, l.Qty, l.UnitPrice,
                    l.DiscountAmount, l.TaxRate, l.LineTotal,
                    ItemCode = it != null ? it.ItemCode : null,
                })
                .Take(cap + 1)
                .ToListAsync(cancellationToken);

            bool truncated = lines.Count > cap;
            if (truncated) lines = lines.Take(cap).ToList();

            foreach (var l in lines)
                builder.AddRow(TradeDocumentRows.Row(
                    l.LineNo, l.ItemCode, l.ItemDescription,
                    l.Qty, l.UnitPrice, l.DiscountAmount, l.TaxRate, l.LineTotal,
                    header.OrderNo, header.OrderDate, party,
                    AccountingSourceHelpers.StatusLabel(header.Status, arabic), header.Notes,
                    header.SubTotal, header.TaxTotal, header.GrandTotal, org));

            return builder.Build(truncated, truncated ? null : lines.Count,
                Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }
}
