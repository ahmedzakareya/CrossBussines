using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ---- Hospital.PatientStatement (H63): the patient's account, one row per entry in date order — a closed bill debits
    // the patient's share (patient + VAT, as HospitalBillingService.BillAsync computes it, so the statement and the bill
    // never disagree), a payment and an approved charity waiver credit it — with the running balance. The header repeats
    // on every row (a document dataset), totals and the closing balance included. Facility-currency bills only: a
    // foreign-currency bill is owed in its own money and cannot share one running balance. ----
    public sealed class HospitalPatientStatementSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        private readonly IServiceProvider _services;
        public HospitalPatientStatementSource(CrossDbContext db, IServiceProvider services) { _db = db; _services = services; }

        // The billing service is RESOLVED ON USE, not injected. HospitalBillingService is registered by the
        // application (Program.cs), never by AddCrossBusinessReporting, so taking it as a constructor parameter
        // made the reporting pack unbuildable on its own: ValidateOnBuild failed and took 15 DI tests with it.
        // Resolving through the scope's own provider returns the SAME scoped instance the constructor would have
        // received, so nothing changes at runtime. This is the idiom HospitalDischargeSummarySource already uses.
        private HospitalBillingService Billing =>
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<HospitalBillingService>(_services);
        public string Key => HospitalDocumentDatasetCodes.PatientStatement;

        private sealed record Entry(DateTime At, int Order, string Kind, string Reference, string Description, decimal Debit, decimal Credit);

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var ctx = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
            var id = query.Parameters.GetInt("PatientId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);
            var p = await _db.Set<HospitalPatient>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId, cancellationToken);
            if (p == null) return builder.Build(totalRowCount: 0);
            bool ar = AccountingSourceHelpers.Arabic(query);
            var now = DateTime.UtcNow;

            var bills = await _db.HospitalBills.AsNoTracking().Where(b => b.CompanyId == ctx.CompanyId && b.PatientId == p.ID && b.CurrencyId == null).OrderBy(b => b.OpenedAt).ToListAsync(cancellationToken);
            var entries = new List<Entry>();
            foreach (var b in bills)
            {
                var v = await Billing.BillAsync(ctx, b.ID, now, cancellationToken);
                if (v == null) continue;
                if (b.Status == HospitalBillStatus.Closed && b.ClosedAt is DateTime closed && v.Totals.Patient + v.Totals.Vat > 0)
                    entries.Add(new Entry(closed, 0, ar ? "فاتورة" : "Bill", b.BillNo, b.AdmissionId != null ? (ar ? "إقامة" : "Inpatient stay") : (ar ? "زيارة / خدمات" : "Visit / services"),
                        v.Totals.Patient + v.Totals.Vat, 0));
                foreach (var pay in v.Payments)
                    entries.Add(new Entry(pay.ReceivedAt, 1, ar ? "دفعة" : "Payment", pay.ReceiptNo, (ar ? "على الفاتورة " : "On bill ") + b.BillNo, 0, pay.Amount));
            }
            var funds = await _db.Set<HospitalCharityFund>().AsNoTracking().Where(f => f.CompanyId == ctx.CompanyId).ToDictionaryAsync(f => f.ID, cancellationToken);
            var billNos = bills.ToDictionary(b => b.ID, b => b.BillNo);
            var grants = await _db.Set<HospitalCharityGrant>().AsNoTracking()
                .Where(g => g.CompanyId == ctx.CompanyId && g.PatientId == p.ID && g.Status == HospitalCharityStatus.Approved && g.DecidedAt != null).ToListAsync(cancellationToken);
            foreach (var g in grants.Where(g => billNos.ContainsKey(g.BillId)))
            {
                var fund = funds.GetValueOrDefault(g.FundId);
                entries.Add(new Entry(g.DecidedAt!.Value, 2, ar ? "إعفاء خيري" : "Charity waiver", g.GrantNo,
                    (fund == null ? "" : (ar ? fund.Name : fund.NameEn) + " — ") + (ar ? "على الفاتورة " : "on bill ") + billNos[g.BillId], 0, g.Amount));
            }

            var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, p, null, p.Mrn, now, now, cancellationToken);
            var currency = await Billing.CurrencyLabelAsync(ctx, null, cancellationToken);
            decimal debit = entries.Sum(e => e.Debit), credit = entries.Sum(e => e.Credit);
            header["Currency"] = currency;
            header["TotalDebit"] = debit;
            header["TotalCredit"] = credit;
            header["ClosingBalance"] = debit - credit;
            decimal balance = 0; int n = 0;
            foreach (var e in entries.OrderBy(e => e.At.ToLocalTime().Date).ThenBy(e => e.Order).ThenBy(e => e.At))   // a day: its bills, then what paid them
            {
                balance += e.Debit - e.Credit;
                var row = new Dictionary<string, object?>(header, StringComparer.Ordinal)
                {
                    ["LineNo"] = ++n, ["EntryDate"] = e.At.ToLocalTime().Date, ["Kind"] = e.Kind, ["Reference"] = e.Reference, ["Description"] = e.Description,
                    ["Debit"] = e.Debit == 0 ? null : e.Debit, ["Credit"] = e.Credit == 0 ? null : e.Credit, ["Balance"] = balance,
                };
                builder.AddRow(row);
                if (n >= HospitalDocumentDatasets.MaxRows) break;
            }
            return builder.Build(totalRowCount: entries.Count);
        }
    }
}
