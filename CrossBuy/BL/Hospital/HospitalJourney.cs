using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	/// One step of a patient's way through the hospital: what it is, which department does it, the permission it needs,
	/// the screen that does it, and whether it is the step now.
	public sealed record HospitalJourneyStep(string Key, string Action, string Where, string Url, bool Now);

	/// <summary>
	/// HOSPITAL H272 — the patient's journey, read from where the patient actually stands. The owner (2026-11-26): "why are
	/// the steps not linked - when a step is done, tell the user what comes next and let him go there or stop; the whole
	/// hospital should have a map like this". Nothing is stored: the steps are the open work the record already holds, in
	/// the order a visit or a stay moves through them; the first one is the next step. The screens ask for it after a step
	/// is done and offer it when the user's permissions allow it.
	/// </summary>
	public static class HospitalJourney
	{
		public const string CheckIn = "check-in", Consult = "consult", LabSample = "lab-sample", Imaging = "imaging",
			Dispense = "dispense", Ward = "ward", Collect = "collect", CloseBill = "close-bill";

		/// The open steps of the patient, in the order they are walked. `root` is the module's base path ("/Hospital").
		public static async Task<IReadOnlyList<HospitalJourneyStep>> StepsAsync(CrossDbContext db, BusinessContext ctx, HospitalBillingService billing, int patientId,
			DateTime todayLocal, DateTime nowUtc, string root, CancellationToken ct)
		{
			int c = ctx.CompanyId;
			var steps = new List<HospitalJourneyStep>();
			void Add(string key, string action, string where, string link) => steps.Add(new(key, action, where, link, steps.Count == 0));

			// a stay: the ward carries it until the discharge
			var stay = await (from a in db.HospitalAdmissions.AsNoTracking()
							  join s in db.HospitalBedStays.AsNoTracking() on a.ID equals s.AdmissionId
							  join r in db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
							  where a.CompanyId == c && a.PatientId == patientId && a.Status == HospitalAdmissionStatus.Active && s.EndedAt == null
							  select new { a.ID, r.WardId }).FirstOrDefaultAsync(ct);

			// today's clinic visit: booked → the desk checks in; arrived or in the room → the doctor
			var visit = await db.HospitalAppointments.AsNoTracking()
				.Where(a => a.CompanyId == c && a.PatientId == patientId && a.AppointmentDate == todayLocal.Date
					&& (a.Status == HospitalAppointmentStatus.Booked || a.Status == HospitalAppointmentStatus.Arrived || a.Status == HospitalAppointmentStatus.InRoom))
				.OrderBy(a => a.StartMinute).Select(a => new { a.ID, a.Status, a.PractitionerId }).FirstOrDefaultAsync(ct);
			if (visit?.Status == HospitalAppointmentStatus.Booked) Add(CheckIn, HospitalActions.Book, "desk", $"{root}/FrontDesk?patient={patientId}");
			else if (visit != null) Add(Consult, HospitalActions.Consult, "clinic", $"{root}/Worklist?doctor={visit.PractitionerId}");

			// what the visit or the stay ordered and is still waiting
			var lab = await db.HospitalLabOrders.AsNoTracking().Where(o => o.CompanyId == c && o.PatientId == patientId && o.Status == HospitalLabStatus.Ordered)
				.OrderBy(o => o.OrderedAt).Select(o => (int?)o.ID).FirstOrDefaultAsync(ct);
			if (lab is int lo) Add(LabSample, HospitalActions.Lab, "lab", $"{root}/Lab/{lo}");
			var rad = await db.HospitalRadOrders.AsNoTracking().Where(o => o.CompanyId == c && o.PatientId == patientId
					&& (o.Status == HospitalRadStatus.Scheduled || o.Status == HospitalRadStatus.Arrived || o.Status == HospitalRadStatus.Scanning))
				.OrderBy(o => o.ID).Select(o => (int?)o.ID).FirstOrDefaultAsync(ct);
			if (rad is int ro) Add(Imaging, HospitalActions.Rad, "radiology", $"{root}/Radiology/{ro}");
			var rx = await db.HospitalDispenses.AsNoTracking().Where(d => d.CompanyId == c && d.PatientId == patientId
					&& (d.Status == HospitalDispenseStatus.New || d.Status == HospitalDispenseStatus.Verified || d.Status == HospitalDispenseStatus.Partial))
				.OrderBy(d => d.PrescribedAt).Select(d => (int?)d.ID).FirstOrDefaultAsync(ct);
			if (rx is int rid) Add(Dispense, HospitalActions.Pharmacy, "pharmacy", $"{root}/Pharmacy?rx={rid}#rx-detail");

			if (stay != null) Add(Ward, HospitalActions.Admit, "ward", $"{root}/Nursing?ward={stay.WardId}");

			// the money: what is owed is collected at the counter; a bill paid in full is closed and posted (a stay's bill
			// waits for the discharge)
			var bills = (await billing.BillsAsync(ctx, HospitalBillStatus.Open, patientId, nowUtc, ct))
				.Where(b => !(b.Inpatient && stay != null)).ToList();
			if (bills.Any(b => b.Total - b.Paid > 0)) Add(Collect, HospitalActions.Bill, "desk", $"{root}/FrontDesk?patient={patientId}");
			else if (bills.FirstOrDefault(b => b.Total > 0) is { } paid) Add(CloseBill, HospitalActions.Bill, "cashier", $"{root}/Bill/{paid.Id}");
			return steps;
		}
	}
}
