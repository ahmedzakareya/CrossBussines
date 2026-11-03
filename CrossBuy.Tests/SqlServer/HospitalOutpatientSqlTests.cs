using CrossBuy.Models.Context.Hospital;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
	// Hospital H4 slice against a REAL SQL Server: the script runs twice, the EF mapping round-trips a
	// department, a practitioner and an appointment, and the database itself keeps one live appointment
	// per doctor per slot while a cancelled one frees it — the filtered index SQLite cannot prove.
	// Skipped without CROSSBUY_TEST_SQL.
	[Collection(SqlServerCollection.Name)]
	public class HospitalOutpatientSqlTests : IAsyncLifetime
	{
		private readonly SqlServerFixture _sql;
		private SqlServerFixture.ProbeDatabase? _probe;
		public HospitalOutpatientSqlTests(SqlServerFixture sql) { _sql = sql; }

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			_probe = await _sql.CreateProbeDatabaseAsync("HospitalOutpatient", applyModelSchema: false);
		}

		public async Task DisposeAsync()
		{
			if (_probe != null) await _sql.DropProbeDatabaseAsync(_probe);
		}

		private async Task ApplySliceAsync()
		{
			await using var c = new SqlConnection(_probe!.ConnectionString);
			await c.OpenAsync();
			await _sql.RunScriptAsync(c, "hospital_h4_outpatient.sql");
			// The practitioner entity carries H6's ConsultationServiceId, so the schema under test is the one
			// that ships: H4, then the slices that extend it.
			await _sql.RunScriptAsync(c, "hospital_h5_consultation.sql");
			await _sql.RunScriptAsync(c, "hospital_h6_billing.sql");
		}

		[SkippableFact]
		public async Task The_slice_runs_twice_and_a_practitioner_and_an_appointment_round_trip()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			await ApplySliceAsync();
			await ApplySliceAsync();

			await using (var db = _sql.ContextFor(_probe!))
			{
				var dep = new HospitalDepartment { CompanyId = 1, Code = "IM", Name = "الباطنة", NameEn = "Internal medicine", CreatedAt = DateTime.UtcNow };
				db.Add(dep); await db.SaveChangesAsync();
				var doc = new HospitalPractitioner { CompanyId = 1, Name = "د. علي", NameEn = "Dr Ali", DepartmentId = dep.ID, Engagement = HospitalEngagements.Visiting, PayChannel = HospitalPayChannels.Vendor, WorkDays = "0,1", Privileges = "opd,proc", LicenseExpiry = new DateTime(2027, 3, 1), CreatedAt = DateTime.UtcNow };
				db.Add(doc); await db.SaveChangesAsync();
				db.Add(new HospitalAppointment { CompanyId = 1, PatientId = 4, PractitionerId = doc.ID, AppointmentDate = new DateTime(2026, 11, 15), StartMinute = 570, DurationMinutes = 15, VisitType = HospitalVisitTypes.FollowUp, QueueNumber = 3, CreatedAt = DateTime.UtcNow });
				await db.SaveChangesAsync();
			}
			await using (var db = _sql.ContextFor(_probe!))
			{
				var doc = await db.HospitalPractitioners.AsNoTracking().SingleAsync();
				Assert.Equal((HospitalEngagements.Visiting, "0,1", "opd,proc"), (doc.Engagement, doc.WorkDays, doc.Privileges));
				var a = await db.HospitalAppointments.AsNoTracking().SingleAsync();
				Assert.Equal((570, HospitalVisitTypes.FollowUp, HospitalAppointmentStatus.Booked, (int?)3), (a.StartMinute, a.VisitType, a.Status, a.QueueNumber));
			}
		}

		[SkippableFact]
		public async Task The_database_keeps_one_live_appointment_per_slot_and_a_cancelled_one_frees_it()
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
			string Appt(int patient, string status = "Booked", int minute = 540) =>
				$"INSERT dbo.HospitalAppointments (CompanyId, PatientId, PractitionerId, AppointmentDate, StartMinute, DurationMinutes, Status) VALUES (1, {patient}, 7, '2026-11-15', {minute}, 15, '{status}');";

			Assert.Null(await Fails(Appt(1)));
			Assert.Equal(2601, await Fails(Appt(2)));                          // the slot is taken
			Assert.Null(await Fails("UPDATE dbo.HospitalAppointments SET Status = 'Cancelled' WHERE PatientId = 1;"));
			Assert.Null(await Fails(Appt(2)));                                 // a cancellation frees it
			Assert.Equal(547, await Fails(Appt(3, "Lost", 555)));              // unknown status
			Assert.Equal(547, await Fails(Appt(3, "Booked", 1440)));           // minute off the day
			Assert.Equal(547, await Fails("INSERT dbo.HospitalPractitioners (CompanyId, Name, NameEn, SessionStartMinute, SessionEndMinute) VALUES (1, N'د', 'D', 600, 500);"));
		}
	}
}
