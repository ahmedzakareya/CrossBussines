using CrossBuy.Models.Context.Hospital;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
	// Hospital H9 slice against a REAL SQL Server, on the slices it builds on (H1..H8): it runs twice, the
	// EF mapping round-trips an exam, a room, an order through its report and an alert that names an imaging
	// order; and the database itself keeps one order per visit line, known modalities, states and categories,
	// no "signed" report without its time, impression and category, and an alert naming exactly one source.
	// Skipped without CROSSBUY_TEST_SQL.
	[Collection(SqlServerCollection.Name)]
	public class HospitalRadiologySqlTests : IAsyncLifetime
	{
		private readonly SqlServerFixture _sql;
		private SqlServerFixture.ProbeDatabase? _probe;
		public HospitalRadiologySqlTests(SqlServerFixture sql) { _sql = sql; }

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			_probe = await _sql.CreateProbeDatabaseAsync("HospitalRadiology", applyModelSchema: false);
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
				"hospital_h5_consultation.sql", "hospital_h6_billing.sql", "hospital_h7_pricelists.sql", "hospital_h8_lab.sql", "hospital_h9_radiology.sql",
				"hospital_h11_insurance.sql", "hospital_h15_dental.sql", "hospital_h24_portal_accounts.sql", "hospital_h27_portal_records.sql", "hospital_h33_bilingual_text.sql", "hospital_h34_rad_translation.sql" })   // h33 (bilingual text) and its chain; h34 (report translation)
				await _sql.RunScriptAsync(c, s);
		}

		[SkippableFact]
		public async Task The_slice_runs_twice_and_an_exam_a_room_a_report_and_its_alert_round_trip()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			await ApplySlicesAsync();
			await ApplySlicesAsync();

			var at = new DateTime(2026, 11, 15, 9, 0, 0);
			await using (var db = _sql.ContextFor(_probe!))
			{
				db.Add(new HospitalRadExam { CompanyId = 1, ServiceId = 23, Modality = HospitalRadModality.CT, Contrast = true, Template = "الفحص: مقطعية", TemplateEn = "Exam: CT" });
				var room = new HospitalRadRoom { CompanyId = 1, Code = "CT-1", Name = "مقطعية", NameEn = "CT", Modality = HospitalRadModality.CT, InMaintenance = true, MaintenanceNote = "Tube" };
				db.Add(room); await db.SaveChangesAsync();
				var o = new HospitalRadOrder { CompanyId = 1, AccessionNo = "RAD-000001", PatientId = 1, EncounterId = 5, EncounterOrderId = 10, ServiceId = 23, Code = "RAD-CT", Name = "مقطعية", NameEn = "CT",
					Modality = HospitalRadModality.CT, Contrast = true, Priority = HospitalLabPriority.Urgent, RadRoomId = room.ID, OrderedAt = at,
					Status = HospitalRadStatus.Signed, Findings = "Free air.", Impression = "Perforation.", ImpressionCategory = HospitalRadImpression.Critical, SignedAt = at.AddHours(1), IsCritical = true };
				db.Add(o); await db.SaveChangesAsync();
				db.Add(new HospitalLabCriticalAlert { CompanyId = 1, RadOrderId = o.ID, ToEmployeeId = 40, SentAt = at.AddHours(1) });
				await db.SaveChangesAsync();
			}
			await using (var db = _sql.ContextFor(_probe!))
			{
				Assert.True((await db.HospitalRadExams.AsNoTracking().SingleAsync()).Contrast);
				Assert.Equal((true, "Tube"), ((await db.HospitalRadRooms.AsNoTracking().SingleAsync()).InMaintenance, (await db.HospitalRadRooms.AsNoTracking().SingleAsync()).MaintenanceNote));
				var o = await db.HospitalRadOrders.AsNoTracking().SingleAsync();
				Assert.Equal((HospitalRadStatus.Signed, "Perforation.", HospitalRadImpression.Critical, true), (o.Status, o.Impression, o.ImpressionCategory, o.IsCritical));
				var a = await db.HospitalLabCriticalAlerts.AsNoTracking().SingleAsync();
				Assert.Equal(((int?)null, (int?)o.ID), (a.LabOrderId, a.RadOrderId));
			}
		}

		[SkippableFact]
		public async Task The_database_keeps_one_order_per_visit_line_known_states_and_one_alert_source()
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
			string Order(string acc, string line, string mod = "XR", string status = "Scheduled", string cat = "NULL", string signedAt = "NULL", string impression = "NULL") =>
				$"INSERT dbo.HospitalRadOrders (CompanyId, AccessionNo, PatientId, EncounterOrderId, ServiceId, Code, Name, NameEn, Modality, Status, OrderedAt, ImpressionCategory, SignedAt, Impression) VALUES (1, '{acc}', 1, {line}, 20, 'RAD-CXR', N'ص', 'CXR', '{mod}', '{status}', SYSUTCDATETIME(), {cat}, {signedAt}, {impression});";
			Assert.Null(await Fails(Order("RAD-1", "3")));
			Assert.Equal(2601, await Fails(Order("RAD-1", "4")));                                  // one accession per company
			Assert.Equal(2601, await Fails(Order("RAD-2", "3")));                                  // one order per visit line
			Assert.Equal(547, await Fails(Order("RAD-3", "5", mod: "PET")));                      // unknown modality
			Assert.Equal(547, await Fails(Order("RAD-4", "6", status: "Printed")));               // unknown state
			Assert.Equal(547, await Fails(Order("RAD-5", "7", cat: "'Maybe'")));                  // unknown category
			Assert.Equal(547, await Fails(Order("RAD-6", "8", status: "Signed", cat: "'Normal'", impression: "N'i'")));   // signed needs its time
			Assert.Null(await Fails(Order("RAD-7", "9", status: "Signed", cat: "'Normal'", signedAt: "SYSUTCDATETIME()", impression: "N'i'")));

			Assert.Equal(547, await Fails("INSERT dbo.HospitalLabCriticalAlerts (CompanyId, ToEmployeeId, SentAt) VALUES (1, 40, SYSUTCDATETIME());"));                               // no source
			Assert.Equal(547, await Fails("INSERT dbo.HospitalLabCriticalAlerts (CompanyId, LabOrderId, RadOrderId, ToEmployeeId, SentAt) VALUES (1, 1, 1, 40, SYSUTCDATETIME());"));   // two sources
			Assert.Null(await Fails("INSERT dbo.HospitalLabCriticalAlerts (CompanyId, RadOrderId, ToEmployeeId, SentAt) VALUES (1, 1, 40, SYSUTCDATETIME());"));
			Assert.Equal(547, await Fails("INSERT dbo.HospitalRadRooms (CompanyId, Code, Name, NameEn, Modality) VALUES (1, 'X', N'x', 'x', 'PET');"));
		}
	}
}
