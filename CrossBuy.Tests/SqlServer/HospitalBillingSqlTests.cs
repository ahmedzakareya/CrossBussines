using CrossBuy.Models.Context.Hospital;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
	// Hospital H6 slice against a REAL SQL Server, on the slices it builds on (H4, H5): it runs twice,
	// the EF mapping round-trips settings, a service, a bill with a line, a shift and a payment (the
	// decimal precisions included), and the database itself keeps one bill per visit and per stay, one
	// open shift per cashier, unique receipt numbers, and refuses a payment of zero or an unknown method.
	// Skipped without CROSSBUY_TEST_SQL.
	[Collection(SqlServerCollection.Name)]
	public class HospitalBillingSqlTests : IAsyncLifetime
	{
		private readonly SqlServerFixture _sql;
		private SqlServerFixture.ProbeDatabase? _probe;
		public HospitalBillingSqlTests(SqlServerFixture sql) { _sql = sql; }

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			_probe = await _sql.CreateProbeDatabaseAsync("HospitalBilling", applyModelSchema: false);
		}

		public async Task DisposeAsync()
		{
			if (_probe != null) await _sql.DropProbeDatabaseAsync(_probe);
		}

		private async Task ApplySlicesAsync()
		{
			await using var c = new SqlConnection(_probe!.ConnectionString);
			await c.OpenAsync();
			foreach (var s in new[] { "hospital_h2_patients.sql", "hospital_h3_admissions.sql", "hospital_h4_outpatient.sql", "hospital_h5_consultation.sql", "hospital_h6_billing.sql", "hospital_h7_pricelists.sql" })
				await _sql.RunScriptAsync(c, s);
		}

		[SkippableFact]
		public async Task The_slice_runs_twice_and_a_bill_with_its_line_shift_and_payment_round_trips()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			await ApplySlicesAsync();
			await ApplySlicesAsync();

			await using (var db = _sql.ContextFor(_probe!))
			{
				db.Add(new HospitalBillingSettings { CompanyId = 1, VatRatePercent = 5m, CashAccountId = 4, ReceivableAccountId = 9 });
				db.Add(new HospitalService { CompanyId = 1, Code = "LAB-CBC", Name = "صورة دم", NameEn = "CBC", Category = HospitalServiceCategories.Lab, Price = 6.125m, CreatedAt = DateTime.UtcNow });
				var bill = new HospitalBill { CompanyId = 1, BillNo = "BILL-000001", PatientId = 3, EncounterId = 7, OpenedAt = DateTime.UtcNow };
				var shift = new HospitalCashierShift { CompanyId = 1, Desk = "خزينة العيادات", CashierEmployeeId = 5, OpenedAt = DateTime.UtcNow, OpeningFloat = 50.5m };
				db.AddRange(bill, shift); await db.SaveChangesAsync();
				db.Add(new HospitalBillLine { CompanyId = 1, BillId = bill.ID, Code = "LAB-CBC", Description = "صورة دم", DescriptionEn = "CBC", Category = "Lab", Quantity = 2.5m, UnitPrice = 6.125m, Source = HospitalBillLineSources.Order, AddedAt = DateTime.UtcNow });
				db.Add(new HospitalPayment { CompanyId = 1, BillId = bill.ID, ShiftId = shift.ID, ReceiptNo = "RCPT-000001", Amount = 15.312m, Method = HospitalPaymentMethods.Card, ReceivedAt = DateTime.UtcNow, CashierEmployeeId = 5 });
				await db.SaveChangesAsync();
			}
			await using (var db = _sql.ContextFor(_probe!))
			{
				Assert.Equal(5m, (await db.HospitalBillingSettings.AsNoTracking().SingleAsync()).VatRatePercent);
				Assert.Equal(6.125m, (await db.HospitalServices.AsNoTracking().SingleAsync()).Price);
				var line = await db.HospitalBillLines.AsNoTracking().SingleAsync();
				Assert.Equal((2.5m, 6.125m), (line.Quantity, line.UnitPrice));
				Assert.Equal(15.312m, (await db.HospitalPayments.AsNoTracking().SingleAsync()).Amount);
				Assert.Equal(50.5m, (await db.HospitalCashierShifts.AsNoTracking().SingleAsync()).OpeningFloat);
			}
		}

		[SkippableFact]
		public async Task The_database_keeps_one_bill_per_visit_one_open_shift_per_cashier_and_real_payments()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			await ApplySlicesAsync();
			await using var c = new SqlConnection(_probe!.ConnectionString);
			await c.OpenAsync();
			async Task<int?> Fails(string sql)
			{
				try { await using var cmd = new SqlCommand("SET QUOTED_IDENTIFIER ON; " + sql, c); await cmd.ExecuteNonQueryAsync(); return null; }
				catch (SqlException ex) { return ex.Number; }
			}
			string Bill(string no, string enc, string adm = "NULL") => $"INSERT dbo.HospitalBills (CompanyId, BillNo, PatientId, EncounterId, AdmissionId, OpenedAt) VALUES (1, '{no}', 1, {enc}, {adm}, SYSUTCDATETIME());";
			Assert.Null(await Fails(Bill("B1", "10")));
			Assert.Equal(2601, await Fails(Bill("B2", "10")));                       // one bill per visit
			Assert.Null(await Fails(Bill("B3", "NULL", "20")));
			Assert.Equal(2601, await Fails(Bill("B4", "NULL", "20")));               // one bill per stay
			Assert.Equal(547, await Fails("INSERT dbo.HospitalBills (CompanyId, BillNo, PatientId, Status, OpenedAt) VALUES (1, 'B5', 1, 'Closed', SYSUTCDATETIME());"));   // closed without a time

			string Shift(string status) => $"INSERT dbo.HospitalCashierShifts (CompanyId, Desk, CashierEmployeeId, Status, OpenedAt) VALUES (1, N'خ', 5, '{status}', SYSUTCDATETIME());";
			Assert.Null(await Fails(Shift("Open")));
			Assert.Equal(2601, await Fails(Shift("Open")));                          // one open shift per cashier
			Assert.Null(await Fails(Shift("Closed")));

			string Pay(string receipt, string amount, string method) => $"INSERT dbo.HospitalPayments (CompanyId, BillId, ShiftId, ReceiptNo, Amount, Method, ReceivedAt, CashierEmployeeId) VALUES (1, 1, 1, '{receipt}', {amount}, '{method}', SYSUTCDATETIME(), 5);";
			Assert.Null(await Fails(Pay("R1", "5", "Cash")));
			Assert.Equal(2601, await Fails(Pay("R1", "6", "Cash")));                 // receipt numbers are unique
			Assert.Equal(547, await Fails(Pay("R2", "0", "Cash")));                  // never zero
			Assert.Equal(547, await Fails(Pay("R3", "5", "Cheque")));                // unknown method
		}
	}
}
