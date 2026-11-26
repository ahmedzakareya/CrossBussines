using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H272 — the patient's journey. The owner: "when a step is done, tell the user what comes next". The steps
	// are read from what the record holds, in the order a visit walks them; the first is the next step.
	// ==========================================================================================
	public class HospitalJourneyTests
	{
		private static BusinessContext Ctx() => new() { CompanyId = 1, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private sealed class NoLedger : IHospitalLedger
		{
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
				=> Task.FromResult<(bool, string?, int?)>((true, null, 1));
		}

		[Fact]
		public async Task The_next_step_follows_where_the_patient_stands()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var billing = new HospitalBillingService(host.Db, access, new NoLedger());
			var now = DateTime.UtcNow; var today = DateTime.Now.Date;
			var patients = new HospitalPatientService(host.Db, access);
			var pid = (await patients.SaveAsync(Ctx(), new HospitalPatientInput { Name = "م مسار", NameEn = "Journey Test", Sex = "M" }, 7, now)).Id!.Value;
			var other = (await patients.SaveAsync(Ctx(), new HospitalPatientInput { Name = "م آخر", NameEn = "Other Test", Sex = "F" }, 7, now)).Id!.Value;
			async Task<string> Keys(int p) => string.Join(",", (await HospitalJourney.StepsAsync(host.Db, Ctx(), billing, p, today, now, "/Hospital", default)).Select(s => s.Key));

			Assert.Equal("", await Keys(pid));   // nothing open: the journey is complete

			var doc = new HospitalPractitioner { CompanyId = 1, Name = "د اختبار", NameEn = "Dr Test", CreatedAt = now };
			host.Seed.HospitalPractitioners.Add(doc); await host.Seed.SaveChangesAsync();
			var appt = new HospitalAppointment { CompanyId = 1, PatientId = pid, PractitionerId = doc.ID, AppointmentDate = today, StartMinute = 600, DurationMinutes = 15, CreatedAt = now };
			host.Seed.HospitalAppointments.Add(appt);
			host.Seed.HospitalDispenses.Add(new HospitalDispense { CompanyId = 1, RxNo = "RX-T1", PatientId = pid, Status = HospitalDispenseStatus.New, PrescribedAt = now });
			var bill = new HospitalBill { CompanyId = 1, BillNo = "BILL-T1", PatientId = pid, OpenedAt = now };
			host.Seed.HospitalBills.Add(bill); await host.Seed.SaveChangesAsync();
			host.Seed.HospitalBillLines.Add(new HospitalBillLine { CompanyId = 1, BillId = bill.ID, Code = "C1", Description = "كشف", DescriptionEn = "Consultation", UnitPrice = 25, AddedAt = now });
			await host.Seed.SaveChangesAsync();

			// booked today: the desk checks him in first; the pharmacy and the counter wait after it
			var steps = await HospitalJourney.StepsAsync(host.Db, Ctx(), billing, pid, today, now, "/Hospital", default);
			Assert.Equal("check-in,dispense,collect", string.Join(",", steps.Select(s => s.Key)));
			Assert.True(steps[0].Now); Assert.All(steps.Skip(1), s => Assert.False(s.Now));
			Assert.Equal($"/Hospital/FrontDesk?patient={pid}", steps[0].Url);
			Assert.Equal(HospitalActions.Book, steps[0].Action);
			Assert.Equal(HospitalActions.Bill, steps[2].Action);

			// checked in: the doctor is next, on his own list
			await host.Db.HospitalAppointments.Where(a => a.ID == appt.ID).ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, HospitalAppointmentStatus.Arrived));
			steps = await HospitalJourney.StepsAsync(host.Db, Ctx(), billing, pid, today, now, "/Hospital", default);
			Assert.Equal("consult", steps[0].Key);
			Assert.Equal($"/Hospital/Worklist?doctor={doc.ID}", steps[0].Url);

			// seen and dispensed: only the money is left
			await host.Db.HospitalAppointments.Where(a => a.ID == appt.ID).ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, HospitalAppointmentStatus.Done));
			await host.Db.HospitalDispenses.Where(d => d.PatientId == pid).ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, HospitalDispenseStatus.Dispensed));
			Assert.Equal("collect", await Keys(pid));

			// another patient's work is never his
			Assert.Equal("", await Keys(other));
		}
	}
}
