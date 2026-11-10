using CrossBuy.Models.Context.Hospital;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
	// Hospital H7 slice against a REAL SQL Server, on the slices it builds on (H1..H6): it runs twice,
	// the EF mapping round-trips a list (frozen rate and percentages), a line with an own price, the item
	// map, a public price, a patient's list and a foreign bill's currency, closing rate and a payment's
	// facility amount; and the database itself keeps one line per item per list, one version per code,
	// a line naming exactly one of service or item, and only the known kinds and states.
	// Skipped without CROSSBUY_TEST_SQL.
	[Collection(SqlServerCollection.Name)]
	public class HospitalPricingSqlTests : IAsyncLifetime
	{
		private readonly SqlServerFixture _sql;
		private SqlServerFixture.ProbeDatabase? _probe;
		public HospitalPricingSqlTests(SqlServerFixture sql) { _sql = sql; }

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			_probe = await _sql.CreateProbeDatabaseAsync("HospitalPricing", applyModelSchema: false);
		}

		public async Task DisposeAsync()
		{
			if (_probe != null) await _sql.DropProbeDatabaseAsync(_probe);
		}

		private async Task ApplySlicesAsync()
		{
			await using var c = new SqlConnection(_probe!.ConnectionString);
			await c.OpenAsync();
			foreach (var s in new[] { "hospital_h1_facility.sql", "hospital_h2_patients.sql", "hospital_h3_admissions.sql", "hospital_h57_patient_merge.sql", "hospital_h59_deaths.sql", "hospital_h4_outpatient.sql",
				"hospital_h5_consultation.sql", "hospital_h6_billing.sql", "hospital_h7_pricelists.sql" })
				await _sql.RunScriptAsync(c, s);
		}

		[SkippableFact]
		public async Task The_slice_runs_twice_and_a_list_its_lines_and_a_foreign_bill_round_trip()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			await ApplySlicesAsync();
			await ApplySlicesAsync();

			int listId;
			await using (var db = _sql.ContextFor(_probe!))
			{
				var pl = new HospitalPriceList { CompanyId = 1, Code = "PL-0001", Name = "الخليج", NameEn = "Gulf", Kind = HospitalPriceListKinds.Insurer, CurrencyId = 4, FrozenRate = 12.18026797m,
					ServicesAdjPct = -12.5m, MedicinesAdjPct = -5m, ConsumablesAdjPct = 2.25m, RoundingStep = 5m, ValidFrom = new(2026, 3, 1), ValidTo = new(2027, 2, 28), CreatedAt = DateTime.UtcNow };
				db.Add(pl); await db.SaveChangesAsync(); listId = pl.ID;
				db.Add(new HospitalPriceListLine { CompanyId = 1, PriceListId = pl.ID, ServiceId = 9, FixedPrice = 100.125m, NeedsReview = false, ConfirmedMainPrice = 120.5m, AddedAt = DateTime.UtcNow });
				db.Add(new HospitalPriceListLine { CompanyId = 1, PriceListId = pl.ID, ItemId = 71, IsExcluded = true, AddedAt = DateTime.UtcNow });
				db.Add(new HospitalItemCategory { CompanyId = 1, ItemCategoryId = 61, Kind = HospitalPriceGroups.Medicine });
				db.Add(new HospitalItemPublicPrice { CompanyId = 1, ItemId = 72, PublicPrice = 3.75m, UpdatedAt = DateTime.UtcNow });
				db.Add(new HospitalPatient { CompanyId = 1, Mrn = "MRN-100001", Name = "م", NameEn = "P", Sex = "M", PriceListId = pl.ID, CreatedAt = DateTime.UtcNow });
				var bill = new HospitalBill { CompanyId = 1, BillNo = "BILL-000001", PatientId = 1, EncounterId = 7, OpenedAt = DateTime.UtcNow, PriceListId = pl.ID, CurrencyId = 4, ExchangeRate = 0.08210000m };
				var shift = new HospitalCashierShift { CompanyId = 1, Desk = "خ", CashierEmployeeId = 5, OpenedAt = DateTime.UtcNow };
				db.AddRange(bill, shift); await db.SaveChangesAsync();
				db.Add(new HospitalBillLine { CompanyId = 1, BillId = bill.ID, Code = "PROC-STRESS", Description = "ج", DescriptionEn = "S", Category = "Procedure", UnitPrice = 60m, OutsideList = true, AddedAt = DateTime.UtcNow });
				db.Add(new HospitalPayment { CompanyId = 1, BillId = bill.ID, ShiftId = shift.ID, ReceiptNo = "RCPT-000001", Amount = 465m, BaseAmount = 38.177m, Method = HospitalPaymentMethods.Card, ReceivedAt = DateTime.UtcNow, CashierEmployeeId = 5 });
				await db.SaveChangesAsync();
			}
			await using (var db = _sql.ContextFor(_probe!))
			{
				var pl = await db.HospitalPriceLists.AsNoTracking().SingleAsync();
				Assert.Equal((12.18026797m, -12.5m, -5m, 2.25m, 5m), (pl.FrozenRate, pl.ServicesAdjPct, pl.MedicinesAdjPct, pl.ConsumablesAdjPct, pl.RoundingStep!.Value));
				Assert.Equal((HospitalPriceListStatus.Draft, 1), (pl.Status, pl.Version));
				var lines = await db.HospitalPriceListLines.AsNoTracking().OrderBy(l => l.ID).ToListAsync();
				Assert.Equal((100.125m, 120.5m, false), (lines[0].FixedPrice!.Value, lines[0].ConfirmedMainPrice!.Value, lines[0].NeedsReview));
				Assert.True(lines[1].IsExcluded && lines[1].NeedsReview);
				Assert.Equal(3.75m, (await db.HospitalItemPublicPrices.AsNoTracking().SingleAsync()).PublicPrice);
				Assert.Equal(listId, (await db.HospitalPatients.AsNoTracking().SingleAsync()).PriceListId);
				var bill = await db.HospitalBills.AsNoTracking().SingleAsync();
				Assert.Equal((listId, 4, 0.0821m), (bill.PriceListId!.Value, bill.CurrencyId!.Value, bill.ExchangeRate!.Value));
				Assert.True((await db.HospitalBillLines.AsNoTracking().SingleAsync()).OutsideList);
				Assert.Equal(38.177m, (await db.HospitalPayments.AsNoTracking().SingleAsync()).BaseAmount);
			}
		}

		[SkippableFact]
		public async Task The_database_keeps_one_line_per_item_one_version_per_code_and_known_kinds()
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
			string List(string code, int ver, string kind = "Insurer", string status = "Draft") =>
				$"INSERT dbo.HospitalPriceLists (CompanyId, Code, Name, NameEn, Kind, Version, Status, ValidFrom) VALUES (1, '{code}', N'ق', 'L', '{kind}', {ver}, '{status}', '2026-01-01');";
			Assert.Null(await Fails(List("PL-1", 1)));
			Assert.Equal(2601, await Fails(List("PL-1", 1)));                         // one row per code and version
			Assert.Null(await Fails(List("PL-1", 2)));
			Assert.Equal(547, await Fails(List("PL-2", 1, kind: "Friends")));        // unknown kind
			Assert.Equal(547, await Fails(List("PL-3", 1, status: "Pending")));      // unknown state
			Assert.Equal(547, await Fails("INSERT dbo.HospitalPriceLists (CompanyId, Code, Name, NameEn, Kind, ValidFrom, ValidTo) VALUES (1, 'PL-4', N'ق', 'L', 'Cash', '2026-12-31', '2026-01-01');"));   // ends before it starts

			string Line(string service, string item, string price = "NULL") => $"INSERT dbo.HospitalPriceListLines (CompanyId, PriceListId, ServiceId, ItemId, FixedPrice) VALUES (1, 1, {service}, {item}, {price});";
			Assert.Null(await Fails(Line("9", "NULL")));
			Assert.Equal(2601, await Fails(Line("9", "NULL")));                        // a service once per list
			Assert.Null(await Fails(Line("NULL", "71")));
			Assert.Equal(2601, await Fails(Line("NULL", "71")));                       // an item once per list
			Assert.Equal(547, await Fails(Line("10", "72")));                          // a line names a service OR an item
			Assert.Equal(547, await Fails(Line("NULL", "NULL")));
			Assert.Equal(547, await Fails(Line("11", "NULL", "-1")));                  // never a negative price
			Assert.Equal(547, await Fails("INSERT dbo.HospitalItemCategories (CompanyId, ItemCategoryId, Kind) VALUES (1, 61, 'Food');"));
		}
	}
}
