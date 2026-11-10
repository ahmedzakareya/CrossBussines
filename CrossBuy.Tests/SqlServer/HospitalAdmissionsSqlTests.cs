using CrossBuy.Models.Context.Hospital;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
	// Hospital H3 slice against a REAL SQL Server, on top of H2 as it ships: the script runs twice,
	// the EF mapping round-trips an admission, its stay and the patient's photo, and the database
	// itself refuses the two things a race could make — a second open admission for a patient and a
	// second open stay in a bed — which the SQLite tests cannot prove. Skipped without CROSSBUY_TEST_SQL.
	[Collection(SqlServerCollection.Name)]
	public class HospitalAdmissionsSqlTests : IAsyncLifetime
	{
		private readonly SqlServerFixture _sql;
		private SqlServerFixture.ProbeDatabase? _probe;
		public HospitalAdmissionsSqlTests(SqlServerFixture sql) { _sql = sql; }

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			_probe = await _sql.CreateProbeDatabaseAsync("HospitalAdmissions", applyModelSchema: false);
		}

		public async Task DisposeAsync()
		{
			if (_probe != null) await _sql.DropProbeDatabaseAsync(_probe);
		}

		private async Task ApplySlicesAsync()
		{
			await using var c = new SqlConnection(_probe!.ConnectionString);
			await c.OpenAsync();
			await _sql.RunScriptAsync(c, "hospital_h2_patients.sql");
			await _sql.RunScriptAsync(c, "hospital_h3_admissions.sql");
			// H7 added nullable columns to patients, bills, lines and payments: the EF model maps them, so the probe needs them.
			foreach (var s in new[] { "hospital_h57_patient_merge.sql", "hospital_h59_deaths.sql", "hospital_h4_outpatient.sql", "hospital_h5_consultation.sql", "hospital_h6_billing.sql", "hospital_h7_pricelists.sql" })
				await _sql.RunScriptAsync(c, s);
		}

		[SkippableFact]
		public async Task The_slice_runs_twice_and_an_admission_with_its_stay_and_photo_round_trips()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			await ApplySlicesAsync();
			await ApplySlicesAsync();

			await using (var db = _sql.ContextFor(_probe!))
			{
				var p = new HospitalPatient { CompanyId = 1, Mrn = "MRN-100001", Name = "عمر", NameEn = "Omar", Sex = "M", PhotoPath = "/uploads/hospital/patients/1/a.jpg", CreatedAt = DateTime.UtcNow };
				db.Add(p); await db.SaveChangesAsync();
				var a = new HospitalAdmission { CompanyId = 1, PatientId = p.ID, AdmittedAt = new DateTime(2026, 11, 1, 8, 0, 0), Diagnosis = "التهاب رئوي", CreatedAt = DateTime.UtcNow };
				db.Add(a); await db.SaveChangesAsync();
				db.Add(new HospitalBedStay { CompanyId = 1, AdmissionId = a.ID, PatientId = p.ID, BedId = 5, RoomId = 2, StartedAt = a.AdmittedAt });
				await db.SaveChangesAsync();
			}
			await using (var db = _sql.ContextFor(_probe!))
			{
				Assert.Equal("/uploads/hospital/patients/1/a.jpg", (await db.HospitalPatients.AsNoTracking().SingleAsync()).PhotoPath);
				var a = await db.HospitalAdmissions.AsNoTracking().SingleAsync();
				Assert.Equal((HospitalAdmissionStatus.Active, "التهاب رئوي"), (a.Status, a.Diagnosis));
				Assert.Null((await db.HospitalBedStays.AsNoTracking().SingleAsync()).EndedAt);
			}
		}

		[SkippableFact]
		public async Task The_database_refuses_a_second_open_admission_and_a_second_open_stay_in_one_bed()
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
			const string adm = "INSERT dbo.HospitalAdmissions (CompanyId, PatientId, Status, AdmittedAt, DischargedAt) VALUES ";
			Assert.Null(await Fails(adm + "(1, 10, 'Active', SYSUTCDATETIME(), NULL);"));
			Assert.Equal(2601, await Fails(adm + "(1, 10, 'Active', SYSUTCDATETIME(), NULL);"));                // one open admission per patient
			Assert.Null(await Fails(adm + "(1, 10, 'Discharged', SYSUTCDATETIME(), SYSUTCDATETIME());"));       // history is fine
			Assert.Equal(547, await Fails(adm + "(1, 11, 'Discharged', SYSUTCDATETIME(), NULL);"));            // discharged needs a date
			Assert.Equal(547, await Fails(adm + "(1, 12, 'Waiting', SYSUTCDATETIME(), NULL);"));               // unknown status

			const string stay = "INSERT dbo.HospitalBedStays (CompanyId, AdmissionId, PatientId, BedId, RoomId, StartedAt, EndedAt) VALUES ";
			Assert.Null(await Fails(stay + "(1, 1, 10, 7, 3, SYSUTCDATETIME(), NULL);"));
			Assert.Equal(2601, await Fails(stay + "(1, 1, 10, 7, 3, SYSUTCDATETIME(), NULL);"));               // one open stay per bed
			Assert.Null(await Fails(stay + "(1, 1, 10, 7, 3, '2026-01-01', '2026-01-02');"));                  // a closed one is history
			Assert.Equal(547, await Fails(stay + "(1, 999, 10, 8, 3, SYSUTCDATETIME(), NULL);"));              // FK to the admission
		}
	}
}
