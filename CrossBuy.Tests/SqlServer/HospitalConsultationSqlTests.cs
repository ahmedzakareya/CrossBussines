using CrossBuy.Models.Context.Hospital;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
	// Hospital H5 slice against a REAL SQL Server: the script runs twice, the EF mapping round-trips an
	// encounter with its vitals (the decimal precisions included), a diagnosis and a prescription line,
	// and the database itself keeps one encounter per appointment and refuses a signed one without a
	// signing time. Skipped without CROSSBUY_TEST_SQL.
	[Collection(SqlServerCollection.Name)]
	public class HospitalConsultationSqlTests : IAsyncLifetime
	{
		private readonly SqlServerFixture _sql;
		private SqlServerFixture.ProbeDatabase? _probe;
		public HospitalConsultationSqlTests(SqlServerFixture sql) { _sql = sql; }

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			_probe = await _sql.CreateProbeDatabaseAsync("HospitalConsultation", applyModelSchema: false);
		}

		public async Task DisposeAsync()
		{
			if (_probe != null) await _sql.DropProbeDatabaseAsync(_probe);
		}

		private async Task ApplySliceAsync()
		{
			await using var c = new SqlConnection(_probe!.ConnectionString);
			await c.OpenAsync();
			// The model maps every hospital slice's columns (H10 adds HospitalPrescriptionLines.ItemId), so the probe needs them all.
			foreach (var s in new[] { "hospital_h1_facility.sql", "hospital_h2_patients.sql", "hospital_h3_admissions.sql", "hospital_h4_outpatient.sql",
				"hospital_h5_consultation.sql", "hospital_h6_billing.sql", "hospital_h7_pricelists.sql", "hospital_h8_lab.sql", "hospital_h9_radiology.sql", "hospital_h10_pharmacy.sql" })
				await _sql.RunScriptAsync(c, s);
		}

		[SkippableFact]
		public async Task The_slice_runs_twice_and_an_encounter_with_its_vitals_diagnosis_and_prescription_round_trips()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			await ApplySliceAsync();
			await ApplySliceAsync();

			await using (var db = _sql.ContextFor(_probe!))
			{
				db.Add(new HospitalDiagnosisCode { CompanyId = 1, Code = "J02.9", Name = "التهاب البلعوم الحاد", NameEn = "Acute pharyngitis, unspecified", CreatedAt = DateTime.UtcNow });
				var e = new HospitalEncounter { CompanyId = 1, AppointmentId = 5, PatientId = 3, PractitionerId = 2, StartedAt = DateTime.UtcNow, ChiefComplaint = "حمى", TemperatureC = 38.4m, WeightKg = 70.5m, HeightCm = 175.2m, BpSystolic = 120, BpDiastolic = 80, CreatedAt = DateTime.UtcNow };
				db.Add(e); await db.SaveChangesAsync();
				db.Add(new HospitalEncounterDiagnosis { CompanyId = 1, EncounterId = e.ID, Code = "J02.9", Name = "التهاب البلعوم الحاد", NameEn = "Acute pharyngitis, unspecified", IsPrimary = true });
				db.Add(new HospitalPrescriptionLine { CompanyId = 1, EncounterId = e.ID, Drug = "باراسيتامول ٥٠٠ ملجم", Instructions = "قرص كل ٨ ساعات", Quantity = 9 });
				await db.SaveChangesAsync();
			}
			await using (var db = _sql.ContextFor(_probe!))
			{
				var e = await db.HospitalEncounters.AsNoTracking().SingleAsync();
				Assert.Equal((38.4m, 70.5m, 175.2m, "حمى"), (e.TemperatureC!.Value, e.WeightKg!.Value, e.HeightCm!.Value, e.ChiefComplaint));
				Assert.True((await db.HospitalEncounterDiagnoses.AsNoTracking().SingleAsync()).IsPrimary);
				Assert.Equal(9, (await db.HospitalPrescriptionLines.AsNoTracking().SingleAsync()).Quantity);
			}
		}

		[SkippableFact]
		public async Task The_database_keeps_one_encounter_per_appointment_and_a_signed_one_records_when()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			await ApplySliceAsync();
			await using var c = new SqlConnection(_probe!.ConnectionString);
			await c.OpenAsync();
			async Task<int?> Fails(string sql)
			{
				try { await using var cmd = new SqlCommand("SET QUOTED_IDENTIFIER ON; " + sql, c); await cmd.ExecuteNonQueryAsync(); return null; }
				catch (SqlException ex) { return ex.Number; }
			}
			string Enc(int appt, string status = "Draft", string signed = "NULL") =>
				$"INSERT dbo.HospitalEncounters (CompanyId, AppointmentId, PatientId, PractitionerId, Status, StartedAt, SignedAt) VALUES (1, {appt}, 1, 1, '{status}', SYSUTCDATETIME(), {signed});";
			Assert.Null(await Fails(Enc(10)));
			Assert.Equal(2601, await Fails(Enc(10)));                                  // one encounter per appointment
			Assert.Equal(547, await Fails(Enc(11, "Signed")));                        // signed without a time
			Assert.Null(await Fails(Enc(12, "Signed", "SYSUTCDATETIME()")));
			Assert.Equal(547, await Fails("INSERT dbo.HospitalPrescriptionLines (CompanyId, EncounterId, Drug, Quantity) VALUES (1, 999, N'x', 1);"));   // FK
			Assert.Equal(2601, await Fails("INSERT dbo.HospitalDiagnosisCodes (CompanyId, Code, Name, NameEn) VALUES (1, 'A09', N'أ', 'A'), (1, 'A09', N'ب', 'B');"));
		}
	}
}
