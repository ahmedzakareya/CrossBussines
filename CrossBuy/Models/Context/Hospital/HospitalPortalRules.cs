using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — patient portal booking rules (unit H23; the owner approved the prototype on 2026-11-04 and left
	// the order of the build to me).
	//
	// The hospital decides how a patient books online:
	//   · one row of GENERAL rules per company: the booking window, the minimum notice, the online cancel cut-off,
	//     the open bookings one patient may hold, family booking, the earlier-slot waitlist, self-registration,
	//     showing the cost before confirming, the insurance check, online payment;
	//   · per CLINIC (an outpatient department, H4): the booking mode —
	//       Doctor  the patient chooses the doctor          (Epic MyChart direct scheduling, Doctolib)
	//       Next    the patient chooses a time; the clinic assigns the first free doctor
	//       Request the patient asks; the call centre confirms
	//       Off     phone only
	//     — follow-ups only with the patient's own doctor, a referral for a new visit, video visits, and which
	//     visit types (H4's HospitalVisitTypes) are offered online;
	//   · per DOCTOR: bookable online, and taking new patients.
	// A company or clinic with no row books by the defaults (HospitalPortalRules.Defaults), so nothing has to be
	// set up before the portal works. CrossBuy/deploy/sql/hospital_h23_portal_rules.sql builds the tables.
	// ==========================================================================================

	public static class HospitalPortalModes
	{
		public const string Doctor = "Doctor";
		public const string Next = "Next";
		public const string Request = "Request";
		public const string Off = "Off";
		public static readonly IReadOnlyList<string> All = new[] { Doctor, Next, Request, Off };
	}

	public static class HospitalPortalPayment
	{
		public const string None = "None";
		public const string Deposit = "Deposit";
		public const string Full = "Full";
		public static readonly IReadOnlyList<string> All = new[] { None, Deposit, Full };
	}

	public class HospitalPortalSettings
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int WindowDays { get; set; } = 60;
		public int LeadHours { get; set; } = 2;
		public int CancelHours { get; set; } = 24;
		public int MaxActive { get; set; } = 3;
		public bool FamilyBooking { get; set; } = true;
		public bool Waitlist { get; set; } = true;
		/// H48: how long one earlier-slot offer is held for one patient before it passes to the next (minutes).
		public int OfferMinutes { get; set; } = 30;
		public bool SelfRegistration { get; set; } = true;
		public bool ShowFees { get; set; } = true;
		public bool InsuranceCheck { get; set; } = true;
		public string Payment { get; set; } = HospitalPortalPayment.None;
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class HospitalPortalClinicRule
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int DepartmentId { get; set; }
		public string Mode { get; set; } = HospitalPortalModes.Doctor;
		public bool FollowUpOwnDoctor { get; set; }
		public bool ReferralForNew { get; set; }
		public bool Video { get; set; }
		/// The visit types offered online (HospitalVisitTypes), comma-separated.
		public string VisitTypes { get; set; } = "";
	}

	public class HospitalPortalDoctorRule
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PractitionerId { get; set; }
		public bool Online { get; set; } = true;
		public bool NewPatients { get; set; } = true;
	}

	public static class HospitalPortalRulesModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPortalSettings>(e =>
			{
				e.ToTable("HospitalPortalSettings");   // no DbSet: each names its table
				e.Property(x => x.Payment).HasMaxLength(10);
				e.HasIndex(x => x.CompanyId).IsUnique();
			});
			b.Entity<HospitalPortalClinicRule>(e =>
			{
				e.ToTable("HospitalPortalClinicRules");
				e.Property(x => x.Mode).HasMaxLength(10);
				e.Property(x => x.VisitTypes).HasMaxLength(100);
				e.HasIndex(x => new { x.CompanyId, x.DepartmentId }).IsUnique();
			});
			b.Entity<HospitalPortalDoctorRule>(e =>
			{
				e.ToTable("HospitalPortalDoctorRules");
				e.HasIndex(x => new { x.CompanyId, x.PractitionerId }).IsUnique();
			});
		}
	}
}
