using CrossBuy.Models.Context.Hospital;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
	// Hospital H8 slice against a REAL SQL Server, on the slices it builds on (H1..H7): it runs twice,
	// the EF mapping round-trips a test, an analyte's limits, an order through its states and a result's
	// snapshot; and the database itself keeps one order per visit line, one accession per company, one
	// result per analyte per order, only the known states, priorities and flags, no "verified" order
	// without its verification time, and alerts only of known levels and reasons on a real order.
	// Skipped without CROSSBUY_TEST_SQL.
	[Collection(SqlServerCollection.Name)]
	public class HospitalLabSqlTests : IAsyncLifetime
	{
		private readonly SqlServerFixture _sql;
		private SqlServerFixture.ProbeDatabase? _probe;
		public HospitalLabSqlTests(SqlServerFixture sql) { _sql = sql; }

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			_probe = await _sql.CreateProbeDatabaseAsync("HospitalLab", applyModelSchema: false);
		}

		public async Task DisposeAsync()
		{
			if (_probe != null) await _sql.DropProbeDatabaseAsync(_probe);
		}

		private async Task ApplySlicesAsync()
		{
			await using var c = new SqlConnection(_probe!.ConnectionString);
			await c.OpenAsync();
			foreach (var s in new[] { "hospital_h1_facility.sql", "hospital_h2_patients.sql", "hospital_h3_admissions.sql", "hospital_h4_outpatient.sql",
				"hospital_h5_consultation.sql", "hospital_h6_billing.sql", "hospital_h7_pricelists.sql", "hospital_h8_lab.sql", "hospital_h230_microbiology.sql", "hospital_h232_lab_qc.sql", "hospital_h233_lab_ranges_autoverify.sql" })   // H230: the culture flag on lab tests
				await _sql.RunScriptAsync(c, s);
		}

		[SkippableFact]
		public async Task The_slice_runs_twice_and_a_test_an_order_and_its_results_round_trip()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			await ApplySlicesAsync();
			await ApplySlicesAsync();

			var at = new DateTime(2026, 11, 15, 9, 0, 0);
			await using (var db = _sql.ContextFor(_probe!))
			{
				db.Add(new HospitalLabTest { CompanyId = 1, ServiceId = 10, Specimen = "دم كامل EDTA", SpecimenEn = "Whole blood EDTA", Section = "أمراض الدم", SectionEn = "Haematology" });
				var a = new HospitalLabAnalyte { CompanyId = 1, ServiceId = 10, Code = "CREA", Name = "الكرياتينين", NameEn = "Creatinine", Unit = "mg/dL", RefLow = 0.6m, RefHigh = 1.3m, CriticalHigh = 7.0m, Decimals = 2, SortOrder = 20 };
				var o = new HospitalLabOrder { CompanyId = 1, AccessionNo = "LAB-000001", PatientId = 1, EncounterId = 5, EncounterOrderId = 3, ServiceId = 10, Code = "LAB-KFT", Name = "وظائف الكلى", NameEn = "Kidney function", Priority = HospitalLabPriority.Stat, OrderedAt = at };
				db.AddRange(a, o); await db.SaveChangesAsync();
				db.Add(new HospitalLabResult { CompanyId = 1, LabOrderId = o.ID, AnalyteId = a.ID, Name = a.Name, NameEn = a.NameEn, Unit = a.Unit, Value = 7.4567m, RefLow = a.RefLow, RefHigh = a.RefHigh, Flag = HospitalLabFlag.CriticalHigh, Decimals = 2, SortOrder = 1 });
				o.Status = HospitalLabStatus.Verified; o.ResultedAt = at.AddMinutes(30); o.VerifiedAt = at.AddMinutes(40); o.VerifiedByEmployeeId = 22;
				o.IsCritical = true; o.CriticalAcknowledgedAt = at.AddMinutes(35); o.CriticalNote = "د. خالد هاتفياً";
				await db.SaveChangesAsync();
			}
			await using (var db = _sql.ContextFor(_probe!))
			{
				Assert.Equal("Haematology", (await db.HospitalLabTests.AsNoTracking().SingleAsync()).SectionEn);
				var a = await db.HospitalLabAnalytes.AsNoTracking().SingleAsync();
				Assert.Equal((0.6m, 1.3m, (decimal?)null, 7.0m, true), (a.RefLow!.Value, a.RefHigh!.Value, a.CriticalLow, a.CriticalHigh!.Value, a.IsActive));
				var o = await db.HospitalLabOrders.AsNoTracking().SingleAsync();
				Assert.Equal((HospitalLabStatus.Verified, HospitalLabPriority.Stat, true, "د. خالد هاتفياً"), (o.Status, o.Priority, o.IsCritical, o.CriticalNote));
				var r = await db.HospitalLabResults.AsNoTracking().SingleAsync();
				Assert.Equal((7.4567m, "HH", "mg/dL"), (r.Value!.Value, r.Flag, r.Unit));
			}
		}

		[SkippableFact]
		public async Task The_database_keeps_one_order_per_visit_line_and_only_known_states()
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
			string Order(string acc, string line, string prio = "Routine", string status = "Ordered", string verified = "NULL") =>
				$"INSERT dbo.HospitalLabOrders (CompanyId, AccessionNo, PatientId, EncounterOrderId, ServiceId, Code, Name, NameEn, Priority, Status, OrderedAt, ResultedAt, VerifiedAt) VALUES (1, '{acc}', 1, {line}, 10, 'LAB-CBC', N'ص', 'CBC', '{prio}', '{status}', SYSUTCDATETIME(), SYSUTCDATETIME(), {verified});";
			Assert.Null(await Fails(Order("LAB-1", "3")));
			Assert.Equal(2601, await Fails(Order("LAB-1", "4")));                          // one accession per company
			Assert.Equal(2601, await Fails(Order("LAB-2", "3")));                          // one order per visit line
			Assert.Null(await Fails(Order("LAB-3", "NULL")));
			Assert.Null(await Fails(Order("LAB-4", "NULL")));                              // an order with no visit line is not limited
			Assert.Equal(547, await Fails(Order("LAB-5", "5", prio: "Asap")));            // unknown priority
			Assert.Equal(547, await Fails(Order("LAB-6", "6", status: "Cancelled")));     // unknown state
			Assert.Equal(547, await Fails(Order("LAB-7", "7", status: "Verified")));      // verified needs its time
			Assert.Null(await Fails(Order("LAB-8", "8", status: "Verified", verified: "SYSUTCDATETIME()")));

			string Result(string analyte, string flag = "N") => $"INSERT dbo.HospitalLabResults (CompanyId, LabOrderId, AnalyteId, Name, NameEn, Value, Flag) VALUES (1, 1, {analyte}, N'ه', 'H', 1.5, '{flag}');";
			Assert.Null(await Fails(Result("1")));
			Assert.Equal(2601, await Fails(Result("1")));                                  // one result per analyte per order
			Assert.Equal(547, await Fails(Result("2", "X")));                             // unknown flag
			Assert.Equal(547, await Fails("INSERT dbo.HospitalLabResults (CompanyId, LabOrderId, AnalyteId, Name, NameEn, Flag) VALUES (1, 999, 3, N'ه', 'H', 'N');"));   // no such order

			string Analyte(string code, string lo, string hi, string dec = "1") => $"INSERT dbo.HospitalLabAnalytes (CompanyId, ServiceId, Code, Name, NameEn, RefLow, RefHigh, Decimals) VALUES (1, 10, '{code}', N'م', 'A', {lo}, {hi}, {dec});";
			Assert.Null(await Fails(Analyte("WBC", "4", "11")));
			Assert.Equal(2601, await Fails(Analyte("WBC", "4", "11")));                    // one code per test
			Assert.Equal(547, await Fails(Analyte("RBC", "6", "4")));                     // a range that ends before it starts
			Assert.Equal(547, await Fails(Analyte("HGB", "12", "17", "5")));               // decimals 0..4

			string Alert(string order, string level = "Critical", string reason = "Ordering") => $"INSERT dbo.HospitalLabCriticalAlerts (CompanyId, LabOrderId, Level, Reason, ToEmployeeId, Value, SentAt) VALUES (1, {order}, '{level}', '{reason}', 40, '32.4', SYSUTCDATETIME());";
			Assert.Null(await Fails(Alert("1")));
			Assert.Equal(547, await Fails(Alert("1", level: "Info")));                    // unknown level
			Assert.Equal(547, await Fails(Alert("1", reason: "Random")));                 // unknown reason
			Assert.Equal(547, await Fails(Alert("999")));                                  // no such order
		}
	}
}
