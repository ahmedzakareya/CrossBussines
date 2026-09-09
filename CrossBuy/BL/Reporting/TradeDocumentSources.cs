using Microsoft.EntityFrameworkCore;
using CrossBuy.Models.Context;

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
    internal static class TradeDocumentRows
    {
        // Every source builds the same row, so the shape is written once. A missing key here is a blank
        // cell in three documents at once, which is easier to notice than one.
        public static Dictionary<string, object?> Row(
            int lineNo, string? itemCode, string? description,
            decimal qty, decimal unitPrice, decimal discount, decimal taxRate, decimal lineTotal,
            string? documentNo, DateTime documentDate, string? partyName, string? status, string? notes,
            decimal subTotal, decimal taxTotal, decimal grandTotal) =>
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
                    header.SubTotal, header.TaxTotal, header.GrandTotal));

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
                    header.SubTotal, header.TaxTotal, header.GrandTotal));

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
                    header.SubTotal, header.TaxTotal, header.GrandTotal));

            return builder.Build(truncated, truncated ? null : lines.Count,
                Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }
}
