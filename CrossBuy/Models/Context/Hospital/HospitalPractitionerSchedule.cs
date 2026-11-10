using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — a doctor's weekly schedule and leave (unit H26; the owner, 2026-11-04: "the doctor has his times set
	// in the back office, and a time that is not available, or has another patient, shows as not available").
	//
	//   · A SESSION is one stretch of a weekday the doctor sees patients: from, to, and the minutes per patient. A day
	//     can hold two (a morning and an evening clinic). A doctor with NO session rows still books by H4's single
	//     window (WorkDays + SessionStart/End + SlotMinutes) — the weekly schedule is started on purpose, by copying
	//     that window, so no doctor loses a working day by accident.
	//   · A LEAVE closes days (FromDate–ToDate), or only part of them (StartMinute–EndMinute), with its reason.
	//   · Every place that offers or books a time reads HospitalSchedule (BL), so the front desk, the appointments grid
	//     and the patient portal can never disagree.
	// CrossBuy/deploy/sql/hospital_h26_doctor_schedule.sql builds the tables.
	// ==========================================================================================

	public class HospitalPractitionerSession
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PractitionerId { get; set; }
		/// DayOfWeek: 0 = Sunday … 6 = Saturday.
		public byte DayOfWeek { get; set; }
		public int StartMinute { get; set; }
		public int EndMinute { get; set; }
		public int SlotMinutes { get; set; } = 15;
	}

	public class HospitalPractitionerLeave
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PractitionerId { get; set; }
		public DateTime FromDate { get; set; }
		public DateTime ToDate { get; set; }
		/// Both null = the whole of every day; both set = only that part of each day.
		public int? StartMinute { get; set; }
		public int? EndMinute { get; set; }
		public string? Reason { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public static class HospitalPractitionerScheduleModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPractitionerSession>(e =>
			{
				e.ToTable("HospitalPractitionerSessions");   // no DbSet: each names its table
				e.HasIndex(x => new { x.CompanyId, x.PractitionerId, x.DayOfWeek });
			});
			b.Entity<HospitalPractitionerLeave>(e =>
			{
				e.ToTable("HospitalPractitionerLeaves");
				e.Property(x => x.Reason).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.PractitionerId, x.FromDate });
			});
		}
	}
}
