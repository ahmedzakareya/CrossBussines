using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H26 — THE ONE PLACE a doctor's times on a day are decided: the weekly sessions (or, until the weekly
	// schedule is started, H4's single window), less the leave. The front desk's BookAsync, the appointments grid and
	// the patient portal all read it.
	// ==========================================================================================

	/// One bookable time: its start and its length (a session's minutes per patient).
	public readonly record struct HospitalScheduleSlot(int Minute, int Duration);

	public sealed class HospitalDoctorSchedule
	{
		public HospitalPractitioner Doctor { get; }
		public IReadOnlyList<HospitalPractitionerSession> Sessions { get; }
		public IReadOnlyList<HospitalPractitionerLeave> Leaves { get; }

		public HospitalDoctorSchedule(HospitalPractitioner doctor, IEnumerable<HospitalPractitionerSession>? sessions = null, IEnumerable<HospitalPractitionerLeave>? leaves = null)
		{
			Doctor = doctor;
			Sessions = (sessions ?? Array.Empty<HospitalPractitionerSession>()).OrderBy(s => s.DayOfWeek).ThenBy(s => s.StartMinute).ToList();
			Leaves = (leaves ?? Array.Empty<HospitalPractitionerLeave>()).ToList();
		}

		/// Whether the weekly schedule has been started (otherwise H4's single window applies).
		public bool Weekly => Sessions.Count > 0;

		/// The sessions of a weekday, as (from, to, minutes per patient).
		public IReadOnlyList<(int Start, int End, int Slot)> SessionsOn(DayOfWeek day) => Weekly
			? Sessions.Where(s => s.DayOfWeek == (byte)day).Select(s => (s.StartMinute, s.EndMinute, s.SlotMinutes)).ToList()
			: Doctor.Days().Contains(day) ? new List<(int, int, int)> { (Doctor.SessionStartMinute, Doctor.SessionEndMinute, Doctor.SlotMinutes) } : new List<(int, int, int)>();

		/// The leave that covers this day (whole, or the part it closes), or null.
		public IReadOnlyList<HospitalPractitionerLeave> LeavesOn(DateTime day) => Leaves.Where(l => l.FromDate.Date <= day.Date && day.Date <= l.ToDate.Date).ToList();

		public bool WholeDayLeave(DateTime day) => LeavesOn(day).Any(l => l.StartMinute == null);

		/// Every bookable time of the day: the sessions' slots, none crossing a leave, none on a day off, none for an
		/// inactive doctor or behind an expired licence.
		public IReadOnlyList<HospitalScheduleSlot> SlotsOn(DateTime day)
		{
			if (!Doctor.IsActive || !HospitalOutpatientService.LicenseValidOn(Doctor, day)) return Array.Empty<HospitalScheduleSlot>();
			var leaves = LeavesOn(day);
			if (leaves.Any(l => l.StartMinute == null)) return Array.Empty<HospitalScheduleSlot>();
			var list = new List<HospitalScheduleSlot>();
			foreach (var (start, end, slot) in SessionsOn(day.DayOfWeek))
				for (int m = start; m + slot <= end; m += slot)
					if (!leaves.Any(l => m < l.EndMinute && l.StartMinute < m + slot)) list.Add(new HospitalScheduleSlot(m, slot));
			return list.GroupBy(x => x.Minute).Select(g => g.First()).OrderBy(x => x.Minute).ToList();
		}

		/// The doctor sees patients that day (some bookable time).
		public bool WorksOn(DateTime day) => SlotsOn(day).Count > 0;

		/// The doctor is at work at this moment: inside one of the day's sessions, not on leave (the lab's critical
		/// alerts tell an on-duty doctor first).
		public bool OnDutyAt(DateTime local)
		{
			int m = local.Hour * 60 + local.Minute;
			if (!Doctor.IsActive || LeavesOn(local).Any(l => l.StartMinute == null || (m >= l.StartMinute && m < l.EndMinute))) return false;
			return SessionsOn(local.DayOfWeek).Any(s => m >= s.Start && m < s.End);
		}

		/// The length of the visit starting at that minute that day, or null when it is not one of the day's times.
		public int? DurationAt(DateTime day, int minute) => SlotsOn(day).Where(x => x.Minute == minute).Select(x => (int?)x.Duration).FirstOrDefault();

		// ---------------- what the schedule screen may save (pure) ----------------

		/// Why a session cannot be added to a day, or null: a real weekday, from before to, a slot that fits, no overlap.
		public static string? SessionRefusal(int day, int start, int end, int slot, IEnumerable<(int Start, int End)> sameDay)
		{
			if (day is < 0 or > 6) return "Choose the day";
			if (slot is < 5 or > 120) return "A slot is 5 to 120 minutes";
			if (start < 0 || end > 1440 || start + slot > end) return "The session must start before it ends and hold at least one slot";
			if (sameDay.Any(s => start < s.End && s.Start < end)) return "This session overlaps another one on the same day";
			return null;
		}

		/// Why a leave cannot be saved, or null.
		public static string? LeaveRefusal(DateTime from, DateTime to, int? start, int? end)
		{
			if (from == default || to == default) return "Enter the dates";
			if (to.Date < from.Date) return "The end date is before the start date";
			if ((to.Date - from.Date).TotalDays > 366) return "A leave is at most a year";
			if ((start == null) != (end == null)) return "Enter both times, or neither for whole days";
			if (start != null && (start < 0 || end > 1440 || start >= end)) return "The leave must start before it ends";
			return null;
		}
	}

	public static class HospitalScheduleLoader
	{
		/// The schedules of these doctors (their sessions, and the leave touching [from, to]). A database without the
		/// H26 tables reads as "no weekly schedule, no leave": every doctor books by H4's window, as before.
		public static async Task<Dictionary<int, HospitalDoctorSchedule>> LoadAsync(CrossDbContext db, int companyId, IReadOnlyCollection<HospitalPractitioner> doctors, DateTime from, DateTime to, CancellationToken ct)
		{
			var ids = doctors.Select(d => d.ID).ToList();
			List<HospitalPractitionerSession> sessions; List<HospitalPractitionerLeave> leaves;
			try
			{
				sessions = await db.Set<HospitalPractitionerSession>().AsNoTracking().Where(s => s.CompanyId == companyId && ids.Contains(s.PractitionerId)).ToListAsync(ct);
				leaves = await db.Set<HospitalPractitionerLeave>().AsNoTracking().Where(l => l.CompanyId == companyId && ids.Contains(l.PractitionerId) && l.FromDate <= to.Date && l.ToDate >= from.Date).ToListAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { sessions = new(); leaves = new(); }
			return doctors.ToDictionary(d => d.ID, d => new HospitalDoctorSchedule(d, sessions.Where(s => s.PractitionerId == d.ID), leaves.Where(l => l.PractitionerId == d.ID)));
		}

		public static async Task<HospitalDoctorSchedule> OneAsync(CrossDbContext db, int companyId, HospitalPractitioner doctor, DateTime from, DateTime to, CancellationToken ct) =>
			(await LoadAsync(db, companyId, new[] { doctor }, from, to, ct))[doctor.ID];
	}
}
