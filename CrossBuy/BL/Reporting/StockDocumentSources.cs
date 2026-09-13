using Microsoft.EntityFrameworkCore;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // THE WAREHOUSE DOCUMENTS THEMSELVES — one entry point per document, one shape out.
    //
    // Each source answers the same question about a different table: "give me this document's header
    // and its lines, as rows a template can lay out". The FIELD KEYS are identical across all five
    // because the definition is shared (StockDocumentDatasets), which is what lets one house layout
    // print a receipt and a transfer without being authored twice.
    //
    // TENANCY IS CHECKED ON THE HEADER, never inferred from the id: the id arrives from a URL, so the
    // header is loaded WITH CompanyID = the resolved context and a miss returns an EMPTY set — the
    // same "no such document, as far as you are concerned" the rest of the platform gives, which also
    // does not confirm that the id exists elsewhere.
    //
    // THE LINES ARE FILTERED BY THE DOCUMENT, not by company: they carry no CompanyID of their own and
    // are only ever reached through a header this caller was already allowed to load.
    // ============================================================================================
    internal static class StockDocumentRows
    {
        // Written once, so a missing key is a blank cell in five documents at once rather than in one
        // that nobody notices.
        public static Dictionary<string, object?> Row(
            int lineNo, string? itemCode, string? itemName,
            decimal qty, decimal? bookQty, decimal? diffQty, decimal unitCost, decimal lineTotal,
            string? batchNo, string? serialNo, DateTime? expiry, string? lineReason,
            string? documentNo, DateTime documentDate, string? warehouse, string? toWarehouse,
            string? party, string? status, string? notes, decimal documentTotal,
            TradeDocumentOrg org) =>
            new(StringComparer.Ordinal)
            {
                ["LineNo"] = lineNo,
                ["ItemCode"] = itemCode,
                ["ItemName"] = itemName,
                ["Qty"] = qty,
                ["BookQty"] = bookQty,
                ["DiffQty"] = diffQty,
                ["UnitCost"] = unitCost,
                ["LineTotal"] = lineTotal,
                ["BatchNo"] = batchNo,
                ["SerialNo"] = serialNo,
                ["ExpiryDate"] = expiry,
                ["LineReason"] = lineReason,

                ["DocumentNo"] = documentNo,
                ["DocumentDate"] = documentDate,
                ["WarehouseName"] = warehouse,
                ["ToWarehouseName"] = toWarehouse,
                ["PartyName"] = party,
                ["Status"] = status,
                ["Notes"] = notes,
                ["DocumentTotal"] = documentTotal,

                ["CompanyName"] = org.CompanyName,
                ["CompanyTaxNo"] = org.CompanyTaxNo,
                ["CompanyAddress"] = org.CompanyAddress,
                ["CompanyPhone"] = org.CompanyPhone,
                ["BranchName"] = org.BranchName,
                ["BranchLocation"] = org.BranchLocation,
                ["BranchPhone"] = org.BranchPhone,
            };

        /// Item id → (code, name) for the ids on one document's lines, in the caller's company.
        public static async Task<Dictionary<int, (string? Code, string? Name)>> ItemsAsync(
            CrossDbContext db, BusinessContext context, IEnumerable<int> itemIds, bool arabic, CancellationToken ct)
        {
            var ids = itemIds.Where(i => i > 0).Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<int, (string?, string?)>();

            var rows = await db.Items.AsNoTracking()
                .Where(i => ids.Contains(i.ID) && i.CompanyID == context.CompanyId)
                .Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn })
                .ToListAsync(ct);

            return rows.ToDictionary(
                r => r.ID,
                r => ((string?)r.ItemCode, (string?)(arabic ? (r.Name ?? r.NameEn) : (r.NameEn ?? r.Name))));
        }

        /// Warehouse id → name, for the one or two a stock document names.
        public static async Task<Dictionary<int, string?>> WarehousesAsync(
            CrossDbContext db, BusinessContext context, IEnumerable<int> ids, bool arabic, CancellationToken ct)
        {
            var wanted = ids.Where(i => i > 0).Distinct().ToList();
            if (wanted.Count == 0) return new Dictionary<int, string?>();

            var rows = await db.Warehouses.AsNoTracking()
                .Where(w => wanted.Contains(w.ID) && w.CompanyID == context.CompanyId)
                .Select(w => new { w.ID, w.Name, w.NameEn })
                .ToListAsync(ct);

            return rows.ToDictionary(r => r.ID, r => (string?)(arabic ? (r.Name ?? r.NameEn) : (r.NameEn ?? r.Name)));
        }
    }

    public sealed class GoodsReceiptDocumentSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public GoodsReceiptDocumentSource(CrossDbContext db) { _db = db; }
        public string Key => StockDocumentDatasetCodes.GoodsReceipt;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var id = query.Parameters.GetInt("ReceiptId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);

            var header = await _db.GoodsReceipts.AsNoTracking()
                .Where(d => d.ID == id.Value && d.CompanyID == context.CompanyId)
                .Select(d => new { d.ReceiptNo, d.ReceiptDate, d.Status, d.Notes, d.TotalCost, d.WarehouseId, d.VendorId })
                .FirstOrDefaultAsync(ct);
            if (header is null) return builder.Build(totalRowCount: 0);

            bool arabic = AccountingSourceHelpers.Arabic(query);
            var org = await TradeDocumentOrg.LoadAsync(_db, context, arabic, ct);

            var party = header.VendorId == null ? null : await _db.Vendors.AsNoTracking()
                .Where(v => v.ID == header.VendorId)
                .Select(v => arabic ? v.Name : (v.NameEn ?? v.Name))
                .FirstOrDefaultAsync(ct);

            int cap = query.MaxRows > 0 ? query.MaxRows : StockDocumentDatasets.MaxRows;
            var lines = await _db.GoodsReceiptLines.AsNoTracking()
                .Where(l => l.GoodsReceiptId == id.Value)
                .OrderBy(l => l.LineNo).Take(cap + 1).ToListAsync(ct);

            bool truncated = lines.Count > cap;
            if (truncated) lines = lines.Take(cap).ToList();

            var items = await StockDocumentRows.ItemsAsync(_db, context, lines.Select(l => l.ItemId), arabic, ct);
            var wh = await StockDocumentRows.WarehousesAsync(_db, context, new[] { header.WarehouseId }, arabic, ct);

            foreach (var l in lines)
            {
                items.TryGetValue(l.ItemId, out var it);
                builder.AddRow(StockDocumentRows.Row(
                    l.LineNo, it.Code, it.Name, l.Qty, null, null, l.UnitCost, l.LineTotal,
                    l.BatchNo, l.SerialNo, l.ExpiryDate, null,
                    header.ReceiptNo, header.ReceiptDate, wh.GetValueOrDefault(header.WarehouseId), null,
                    party, AccountingSourceHelpers.StatusLabel(header.Status, arabic), header.Notes,
                    header.TotalCost, org));
            }

            return builder.Build(truncated, truncated ? null : lines.Count,
                Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }

    public sealed class DeliveryNoteDocumentSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public DeliveryNoteDocumentSource(CrossDbContext db) { _db = db; }
        public string Key => StockDocumentDatasetCodes.DeliveryNote;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var id = query.Parameters.GetInt("DeliveryId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);

            var header = await _db.DeliveryNotes.AsNoTracking()
                .Where(d => d.ID == id.Value && d.CompanyID == context.CompanyId)
                .Select(d => new { d.DeliveryNo, d.DeliveryDate, d.Status, d.Notes, d.TotalCost, d.WarehouseId, d.CustomerId })
                .FirstOrDefaultAsync(ct);
            if (header is null) return builder.Build(totalRowCount: 0);

            bool arabic = AccountingSourceHelpers.Arabic(query);
            var org = await TradeDocumentOrg.LoadAsync(_db, context, arabic, ct);

            var party = header.CustomerId == null ? null : await _db.Customers.AsNoTracking()
                .Where(c => c.ID == header.CustomerId)
                .Select(c => arabic ? c.Name : (c.NameEn ?? c.Name))
                .FirstOrDefaultAsync(ct);

            int cap = query.MaxRows > 0 ? query.MaxRows : StockDocumentDatasets.MaxRows;
            var lines = await _db.DeliveryNoteLines.AsNoTracking()
                .Where(l => l.DeliveryNoteId == id.Value)
                .OrderBy(l => l.LineNo).Take(cap + 1).ToListAsync(ct);

            bool truncated = lines.Count > cap;
            if (truncated) lines = lines.Take(cap).ToList();

            var items = await StockDocumentRows.ItemsAsync(_db, context, lines.Select(l => l.ItemId), arabic, ct);
            var wh = await StockDocumentRows.WarehousesAsync(_db, context, new[] { header.WarehouseId }, arabic, ct);

            foreach (var l in lines)
            {
                items.TryGetValue(l.ItemId, out var it);
                builder.AddRow(StockDocumentRows.Row(
                    l.LineNo, it.Code, it.Name, l.Qty, null, null, l.UnitCost, l.LineTotal,
                    l.BatchNo, l.SerialNo, null, null,
                    header.DeliveryNo, header.DeliveryDate, wh.GetValueOrDefault(header.WarehouseId), null,
                    party, AccountingSourceHelpers.StatusLabel(header.Status, arabic), header.Notes,
                    header.TotalCost, org));
            }

            return builder.Build(truncated, truncated ? null : lines.Count,
                Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }

    public sealed class StockTransferDocumentSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public StockTransferDocumentSource(CrossDbContext db) { _db = db; }
        public string Key => StockDocumentDatasetCodes.StockTransfer;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var id = query.Parameters.GetInt("TransferId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);

            var header = await _db.StockTransfers.AsNoTracking()
                .Where(d => d.ID == id.Value && d.CompanyID == context.CompanyId)
                .Select(d => new { d.TransferNo, d.TransferDate, d.Status, d.Notes, d.TotalCost, d.FromWarehouseId, d.ToWarehouseId })
                .FirstOrDefaultAsync(ct);
            if (header is null) return builder.Build(totalRowCount: 0);

            bool arabic = AccountingSourceHelpers.Arabic(query);
            var org = await TradeDocumentOrg.LoadAsync(_db, context, arabic, ct);

            int cap = query.MaxRows > 0 ? query.MaxRows : StockDocumentDatasets.MaxRows;
            var lines = await _db.StockTransferLines.AsNoTracking()
                .Where(l => l.StockTransferId == id.Value)
                .OrderBy(l => l.LineNo).Take(cap + 1).ToListAsync(ct);

            bool truncated = lines.Count > cap;
            if (truncated) lines = lines.Take(cap).ToList();

            var items = await StockDocumentRows.ItemsAsync(_db, context, lines.Select(l => l.ItemId), arabic, ct);
            var wh = await StockDocumentRows.WarehousesAsync(
                _db, context, new[] { header.FromWarehouseId, header.ToWarehouseId }, arabic, ct);

            foreach (var l in lines)
            {
                items.TryGetValue(l.ItemId, out var it);
                builder.AddRow(StockDocumentRows.Row(
                    l.LineNo, it.Code, it.Name, l.Qty, null, null, l.UnitCost, l.LineTotal,
                    l.BatchNo, l.SerialNo, null, null,
                    header.TransferNo, header.TransferDate,
                    wh.GetValueOrDefault(header.FromWarehouseId), wh.GetValueOrDefault(header.ToWarehouseId),
                    null, AccountingSourceHelpers.StatusLabel(header.Status, arabic), header.Notes,
                    header.TotalCost, org));
            }

            return builder.Build(truncated, truncated ? null : lines.Count,
                Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }

    public sealed class StockWriteOffDocumentSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public StockWriteOffDocumentSource(CrossDbContext db) { _db = db; }
        public string Key => StockDocumentDatasetCodes.StockWriteOff;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var id = query.Parameters.GetInt("WriteOffId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);

            var header = await _db.StockWriteOffs.AsNoTracking()
                .Where(d => d.ID == id.Value && d.CompanyID == context.CompanyId)
                .Select(d => new { d.WriteOffNo, d.WriteOffDate, d.Status, d.Notes, d.TotalValue, d.WarehouseId, d.Reason })
                .FirstOrDefaultAsync(ct);
            if (header is null) return builder.Build(totalRowCount: 0);

            bool arabic = AccountingSourceHelpers.Arabic(query);
            var org = await TradeDocumentOrg.LoadAsync(_db, context, arabic, ct);

            int cap = query.MaxRows > 0 ? query.MaxRows : StockDocumentDatasets.MaxRows;
            var lines = await _db.StockWriteOffLines.AsNoTracking()
                .Where(l => l.StockWriteOffId == id.Value)
                .OrderBy(l => l.LineNo).Take(cap + 1).ToListAsync(ct);

            bool truncated = lines.Count > cap;
            if (truncated) lines = lines.Take(cap).ToList();

            var items = await StockDocumentRows.ItemsAsync(_db, context, lines.Select(l => l.ItemId), arabic, ct);
            var wh = await StockDocumentRows.WarehousesAsync(_db, context, new[] { header.WarehouseId }, arabic, ct);

            foreach (var l in lines)
            {
                items.TryGetValue(l.ItemId, out var it);
                builder.AddRow(StockDocumentRows.Row(
                    l.LineNo, it.Code, it.Name, l.Qty, null, null, l.UnitCost, l.LineValue,
                    l.BatchNo, l.SerialNo, null, l.Reason,
                    header.WriteOffNo, header.WriteOffDate, wh.GetValueOrDefault(header.WarehouseId), null,
                    null, AccountingSourceHelpers.StatusLabel(header.Status, arabic),
                    // The document's own reason is the note when it has one — a write-off without a
                    // stated reason is the thing an auditor asks about first.
                    string.IsNullOrWhiteSpace(header.Notes) ? header.Reason : header.Notes,
                    header.TotalValue, org));
            }

            return builder.Build(truncated, truncated ? null : lines.Count,
                Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }

    public sealed class StockCountDocumentSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public StockCountDocumentSource(CrossDbContext db) { _db = db; }
        public string Key => StockDocumentDatasetCodes.StockCount;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var id = query.Parameters.GetInt("CountId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);

            var header = await _db.StockCounts.AsNoTracking()
                .Where(d => d.ID == id.Value && d.CompanyID == context.CompanyId)
                .Select(d => new { d.CountNo, d.CountDate, d.Status, d.Notes, d.TotalAdjValue, d.WarehouseId })
                .FirstOrDefaultAsync(ct);
            if (header is null) return builder.Build(totalRowCount: 0);

            bool arabic = AccountingSourceHelpers.Arabic(query);
            var org = await TradeDocumentOrg.LoadAsync(_db, context, arabic, ct);

            int cap = query.MaxRows > 0 ? query.MaxRows : StockDocumentDatasets.MaxRows;
            var lines = await _db.StockCountLines.AsNoTracking()
                .Where(l => l.StockCountId == id.Value)
                .OrderBy(l => l.LineNo).Take(cap + 1).ToListAsync(ct);

            bool truncated = lines.Count > cap;
            if (truncated) lines = lines.Take(cap).ToList();

            var items = await StockDocumentRows.ItemsAsync(_db, context, lines.Select(l => l.ItemId), arabic, ct);
            var wh = await StockDocumentRows.WarehousesAsync(_db, context, new[] { header.WarehouseId }, arabic, ct);

            foreach (var l in lines)
            {
                items.TryGetValue(l.ItemId, out var it);
                builder.AddRow(StockDocumentRows.Row(
                    // Qty IS THE COUNTED QUANTITY on a count sheet — the book quantity and the
                    // difference are their own fields, so a template can show all three.
                    l.LineNo, it.Code, it.Name, l.CountedQty, l.BookQty, l.DiffQty, l.UnitCost, l.DiffValue,
                    l.BatchNo, null, l.ExpiryDate, l.Reason,
                    header.CountNo, header.CountDate, wh.GetValueOrDefault(header.WarehouseId), null,
                    null, AccountingSourceHelpers.StatusLabel(header.Status, arabic), header.Notes,
                    header.TotalAdjValue, org));
            }

            return builder.Build(truncated, truncated ? null : lines.Count,
                Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }
}
