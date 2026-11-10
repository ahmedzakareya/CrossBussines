using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H23 — the patient portal's booking rules, with no database. The settings screen saves through
	// these refusals; the portal (H24) books through the same decisions, so the two never disagree.
	// ==========================================================================================

	/// One clinic's effective rule: its saved row, or the default for its department code.
	public sealed record HospitalPortalClinic(int DepartmentId, string Code, string Name, string NameEn, string Mode, bool FollowUpOwnDoctor, bool ReferralForNew, bool Video,
		IReadOnlyList<string> VisitTypes, bool Saved);

	/// A doctor as the portal weighs them.
	public sealed record HospitalPortalDoctor(int PractitionerId, int? DepartmentId, bool Online, bool NewPatients);

	public static class HospitalPortalPolicy
	{
		/// The rule a department books by until the hospital saves its own (the approved prototype's defaults).
		public static HospitalPortalClinicRule Default(string? departmentCode) => (departmentCode ?? "").ToUpperInvariant() switch
		{
			"IM" => Rule(HospitalPortalModes.Doctor, own: true, referral: false, video: true, HospitalVisitTypes.New, HospitalVisitTypes.FollowUp, HospitalVisitTypes.Video),
			"CARD" => Rule(HospitalPortalModes.Doctor, own: true, referral: true, video: true, HospitalVisitTypes.New, HospitalVisitTypes.FollowUp, HospitalVisitTypes.Video),
			"PED" => Rule(HospitalPortalModes.Next, own: false, referral: false, video: false, HospitalVisitTypes.New, HospitalVisitTypes.FollowUp),
			"OBG" => Rule(HospitalPortalModes.Doctor, own: true, referral: false, video: true, HospitalVisitTypes.New, HospitalVisitTypes.FollowUp, HospitalVisitTypes.Video),
			"ORTH" => Rule(HospitalPortalModes.Doctor, own: true, referral: false, video: false, HospitalVisitTypes.New, HospitalVisitTypes.FollowUp),
			// the prototype's dentistry: a request, booked by the call centre on a chair at the procedure's length
			"DENT" => Rule(HospitalPortalModes.Request, own: false, referral: false, video: false, HospitalVisitTypes.New, HospitalVisitTypes.FollowUp),
			// the owner, 2026-11-04: every clinic books online ("لازم الكل يشتغل") — dermatology like the others.
			// Only the emergency department stays by phone: it is walk-in, never an appointment.
			"DERM" => Rule(HospitalPortalModes.Doctor, own: false, referral: false, video: false, HospitalVisitTypes.New, HospitalVisitTypes.FollowUp),
			"ER" => Rule(HospitalPortalModes.Off, own: false, referral: false, video: false, HospitalVisitTypes.New),
			_ => Rule(HospitalPortalModes.Doctor, own: false, referral: false, video: false, HospitalVisitTypes.New, HospitalVisitTypes.FollowUp),
		};
		private static HospitalPortalClinicRule Rule(string mode, bool own, bool referral, bool video, params string[] types) =>
			new() { Mode = mode, FollowUpOwnDoctor = own, ReferralForNew = referral, Video = video, VisitTypes = FormatTypes(types) };

		public static IReadOnlyList<string> ParseTypes(string? csv) =>
			(csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(HospitalVisitTypes.IsKnown).Distinct().ToList();
		public static string FormatTypes(IEnumerable<string> types) =>
			string.Join(",", HospitalVisitTypes.All.Where(t => types.Contains(t)));   // the canonical order, so a save is stable

		// ---------------- what the settings screen may save ----------------

		public static string? SettingsRefusal(HospitalPortalSettings s)
		{
			if (s.WindowDays is < 1 or > 365) return "The booking window is 1 to 365 days";
			if (s.LeadHours is < 0 or > 72) return "The minimum notice is 0 to 72 hours";
			if (s.CancelHours is < 0 or > 168) return "The online cancel cut-off is 0 to 168 hours";
			if (s.MaxActive is < 1 or > 20) return "Open bookings per patient are 1 to 20";
			if (s.OfferMinutes is < 5 or > 240) return "An earlier-slot offer is held 5 to 240 minutes";
			if (!HospitalPortalPayment.All.Contains(s.Payment)) return "Choose how payment is taken";
			return null;
		}

		public static string? ClinicRefusal(string? mode, IReadOnlyList<string> types, bool video)
		{
			if (mode == null || !HospitalPortalModes.All.Contains(mode)) return "Choose the booking mode";
			if (types.Count == 0) return "A clinic needs at least one visit type";
			if (types.Contains(HospitalVisitTypes.Video) && !video) return "Turn video visits on to offer a video visit";
			return null;
		}

		// ---------------- what a patient may do (H24 books through these) ----------------

		public static bool IsFollowUp(string visitType) => visitType == HospitalVisitTypes.FollowUp;

		/// The dentistry clinic: its visits are booked on chairs (H28), never as a bare doctor's slot.
		public static bool IsDental(string? departmentCode) => string.Equals(departmentCode, "DENT", StringComparison.OrdinalIgnoreCase);

		/// Why a clinic cannot take this visit type from this patient online, or null.
		public static string? OfferRefusal(HospitalPortalClinic c, string visitType, bool seenInClinic)
		{
			if (c.Mode == HospitalPortalModes.Off) return "This clinic is booked by phone";
			if (!c.VisitTypes.Contains(visitType)) return "This visit is not booked online in this clinic";
			if (visitType == HospitalVisitTypes.Video && !c.Video) return "This clinic offers no video visits";
			if (IsFollowUp(visitType) && !seenInClinic) return "A follow-up is for patients already seen in this clinic";
			return null;
		}

		/// The doctors a patient may book for this visit: online, in the clinic; a follow-up only with their own doctor
		/// when the clinic says so; a new visit only with a doctor taking new patients (unless already theirs).
		public static IReadOnlyList<int> DoctorsFor(HospitalPortalClinic c, string visitType, IReadOnlyCollection<int> ownDoctors, IEnumerable<HospitalPortalDoctor> doctors)
		{
			var list = doctors.Where(d => d.DepartmentId == c.DepartmentId && d.Online);
			if (IsFollowUp(visitType) && c.FollowUpOwnDoctor) list = list.Where(d => ownDoctors.Contains(d.PractitionerId));
			if (visitType == HospitalVisitTypes.New) list = list.Where(d => d.NewPatients || ownDoctors.Contains(d.PractitionerId));
			return list.Select(d => d.PractitionerId).ToList();
		}

		/// Why this slot cannot be booked online, or null: inside the window, after the minimum notice, under the
		/// open-bookings limit, with the referral a new visit needs.
		public static string? BookingRefusal(HospitalPortalSettings s, HospitalPortalClinic c, string visitType, DateTime slotLocal, DateTime nowLocal, int openBookings, string? referral)
		{
			if ((slotLocal - nowLocal).TotalHours < s.LeadHours) return "This time is too soon to book online";
			if ((slotLocal.Date - nowLocal.Date).TotalDays > s.WindowDays) return "This time is beyond the booking window";
			if (openBookings >= s.MaxActive) return "You have reached the limit of open bookings";
			if (c.ReferralForNew && visitType == HospitalVisitTypes.New && string.IsNullOrWhiteSpace(referral)) return "A new visit in this clinic needs a referral";
			return null;
		}

		/// A booking can be cancelled or moved online until the cut-off before it.
		public static bool CanChangeOnline(HospitalPortalSettings s, DateTime slotLocal, DateTime nowLocal) => (slotLocal - nowLocal).TotalHours >= s.CancelHours;

		// ---------------- H24: the engine's decisions ----------------

		/// A doctor's free start minutes on a day: the schedule's times (H26: weekly sessions less leave), less those
		/// already taken, after the minimum notice and inside the window.
		public static IReadOnlyList<int> FreeSlots(HospitalDoctorSchedule d, DateTime day, IReadOnlyCollection<int> taken, HospitalPortalSettings s, DateTime nowLocal) =>
			DaySlots(d, day, taken, s, nowLocal).Where(x => x.State == SlotFree).Select(x => x.Minute).ToList();

		public const string SlotFree = "Free", SlotBooked = "Booked", SlotUnavailable = "Unavailable";

		/// EVERY time of the doctor's sessions on a day, each with its state — so the patient sees the whole session:
		/// free, booked by another patient, or unavailable (past, inside the minimum notice). A day off, a day of leave,
		/// a day past the window or behind an expired licence has no session at all (empty).
		public static IReadOnlyList<(int Minute, string State)> DaySlots(HospitalDoctorSchedule d, DateTime day, IReadOnlyCollection<int> taken, HospitalPortalSettings s, DateTime nowLocal)
		{
			if (!InWindow(day, s, nowLocal)) return Array.Empty<(int, string)>();
			return d.SlotsOn(day).Select(x =>
				(x.Minute, taken.Contains(x.Minute) ? SlotBooked : (day.Date.AddMinutes(x.Minute) - nowLocal).TotalHours < s.LeadHours ? SlotUnavailable : SlotFree)).ToList();
		}

		/// Whether the doctor has a session to offer that day at all (a working day, not on leave, inside the window).
		public static bool WorksOn(HospitalDoctorSchedule d, DateTime day, HospitalPortalSettings s, DateTime nowLocal) => InWindow(day, s, nowLocal) && d.WorksOn(day);

		private static bool InWindow(DateTime day, HospitalPortalSettings s, DateTime nowLocal) => day.Date >= nowLocal.Date && (day.Date - nowLocal.Date).TotalDays <= s.WindowDays;

		/// Why this account cannot do this, or null: a disabled account does nothing; an unverified one (no patient
		/// file yet) can only send requests.
		public static string? AccountRefusal(string status, bool isRequest)
		{
			if (status == HospitalPortalAccountStatus.Disabled) return "This portal account is disabled";
			if (status == HospitalPortalAccountStatus.Unverified && !isRequest) return "Your account is not verified yet: send a request and the call centre will book it";
			if (!HospitalPortalAccountStatus.All.Contains(status)) return "This portal account is disabled";
			return null;
		}

		/// The account may book for its own file, and for its family when the hospital allows family booking.
		public static bool MayBookFor(int? ownPatientId, IReadOnlyCollection<int> familyPatientIds, bool familyBooking, int patientId) =>
			patientId == ownPatientId || (familyBooking && familyPatientIds.Contains(patientId));

		/// Why a request cannot be sent, or null.
		public static string? RequestRefusal(string? timeOfDay, string? preferredDays)
		{
			if (timeOfDay == null || !HospitalPortalTimeOfDay.All.Contains(timeOfDay)) return "Choose the time of day";
			if (string.IsNullOrWhiteSpace(preferredDays) || preferredDays.Trim().Length < 2) return "Say which days suit you";
			return null;
		}
	}
}
