using CrossBuy.Models.Context.Hospital;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
	// Hospital H2 slice against a REAL SQL Server: the shipped deploy/sql script builds the patient
	// tables, twice (idempotency), and the EF mapping then reads and writes them. It also proves the
	// filtered unique index the SQLite tests cannot: one file per national ID, while any number of
	// files may have none. Skipped, never silently passed, without CROSSBUY_TEST_SQL.
	[Collection(SqlServerCollection.Name)]
	public class HospitalPatientsSqlTests : IAsyncLifetime
	{
		private readonly SqlServerFixture _sql;
		private SqlServerFixture.ProbeDatabase? _probe;
		public HospitalPatientsSqlTests(SqlServerFixture sql) { _sql = sql; }

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			_probe = await _sql.CreateProbeDatabaseAsync("HospitalPatients", applyModelSchema: false);
		}

		public async Task DisposeAsync()
		{
			if (_probe != null) await _sql.DropProbeDatabaseAsync(_probe);
		}

		private async Task ApplySliceAsync()
		{
			await using var c = new SqlConnection(_probe!.ConnectionString);
			await c.OpenAsync();
			await _sql.RunScriptAsync(c, "hospital_h2_patients.sql");
			// The entity carries H3's PhotoPath too, so the schema under test is the one that ships: H2 then H3.
			await _sql.RunScriptAsync(c, "hospital_h3_admissions.sql");
			// H7 added nullable columns to patients, bills, lines and payments: the EF model maps them, so the probe needs them.
			foreach (var s in new[] { "hospital_h4_outpatient.sql", "hospital_h5_consultation.sql", "hospital_h6_billing.sql", "hospital_h7_pricelists.sql" })
				await _sql.RunScriptAsync(c, s);
		}

		[SkippableFact]
		public async Task The_slice_runs_twice_and_the_patient_and_its_access_log_round_trip_through_it()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			await ApplySliceAsync();
			await ApplySliceAsync();

			await using (var db = _sql.ContextFor(_probe!))
			{
				var p = new HospitalPatient
				{
					CompanyId = 1, Mrn = "MRN-100001", Name = "عمر علي", NameEn = "Omar Ali", Sex = HospitalPatientSex.Male,
					BirthDate = new DateTime(1990, 3, 4), NationalId = "29003041234567", NationalityId = 51, Phone = "+20 100 000 0001",
					BloodGroup = "O+", Allergies = "بنسلين", ChronicConditions = "Diabetes", EmergencyName = "سارة", EmergencyRelation = "زوجة",
					EmergencyPhone = "+20 100 000 0002", ConsentSigned = true, CreatedAt = DateTime.UtcNow,
				};
				db.Add(p);
				await db.SaveChangesAsync();
				db.Add(new HospitalPatientAccessLog { CompanyId = 1, PatientId = p.ID, EmployeeId = 7, Action = HospitalPatientAccessActions.Open, At = DateTime.UtcNow });
				await db.SaveChangesAsync();
			}

			await using (var db = _sql.ContextFor(_probe!))
			{
				var p = await db.HospitalPatients.AsNoTracking().SingleAsync();
				Assert.Equal(("MRN-100001", "عمر علي", "Omar Ali", "O+", "بنسلين"), (p.Mrn, p.Name, p.NameEn, p.BloodGroup, p.Allergies));
				Assert.Equal(new DateTime(1990, 3, 4), p.BirthDate);
				Assert.Equal("open", (await db.HospitalPatientAccessLogs.AsNoTracking().SingleAsync()).Action);
			}
		}

		[SkippableFact]
		public async Task The_database_keeps_one_file_per_national_id_and_refuses_an_unknown_sex_or_blood_group()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			await ApplySliceAsync();
			await using var c = new SqlConnection(_probe!.ConnectionString);
			await c.OpenAsync();

			async Task<int?> Fails(string sql)
			{
				try { await using var cmd = new SqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); return null; }
				catch (SqlException ex) { return ex.Number; }
			}
			string Row(string mrn, string? nid, string sex = "M", string blood = "NULL") =>
				$"INSERT dbo.HospitalPatients (CompanyId, Mrn, Name, NameEn, Sex, NationalId, BloodGroup) VALUES (1, '{mrn}', N'م', 'P', '{sex}', {(nid == null ? "NULL" : "'" + nid + "'")}, {blood});";

			Assert.Null(await Fails(Row("MRN-1", "111222")));
			Assert.Equal(2601, await Fails(Row("MRN-2", "111222")));          // unique index: same national ID
			Assert.Null(await Fails(Row("MRN-3", null)));
			Assert.Null(await Fails(Row("MRN-4", null)));                       // many files may have no ID
			Assert.Equal(2601, await Fails(Row("MRN-3", "999888")));          // unique MRN
			Assert.Equal(547, await Fails(Row("MRN-5", null, sex: "X")));     // CHECK: sex
			Assert.Equal(547, await Fails(Row("MRN-6", null, blood: "'Z+'"))); // CHECK: blood group
			Assert.Equal(547, await Fails("INSERT dbo.HospitalPatientAccessLogs (CompanyId, PatientId, Action) VALUES (1, 1, 'peek');"));
		}
	}
}
