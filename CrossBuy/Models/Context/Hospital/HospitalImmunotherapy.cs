using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H97 — ALLERGEN IMMUNOTHERAPY (the approved prototype screen sc-allergy): a course of injections of one
	// extract, built up dose by dose. Every dose is followed by a 30-minute observation; its reaction is recorded:
	//   * none, or a local wheal under 25 mm — the next dose steps up the schedule;
	//   * a large local wheal (25 mm or more) — the same dose is repeated;
	//   * a systemic reaction — the course is HELD: no dose until the doctor resumes it, with a reason, two steps lower.
	// CrossBuy/deploy/sql/hospital_h97_allergy.sql builds the tables.
	// ==========================================================================================
	public static class HospitalImmunoStatus
	{
		public const string Active = "Active";
		public const string Held = "Held";
		public const string Stopped = "Stopped";
		public static readonly IReadOnlyList<string> All = new[] { Active, Held, Stopped };
	}

	public static class HospitalImmunoReaction
	{
		/// The dose is given and its reaction not yet recorded.
		public const string Pending = "Pending";
		public const string None = "None";
		public const string Local = "Local";
		public const string LargeLocal = "LargeLocal";
		public const string Systemic = "Systemic";
		public static readonly IReadOnlyList<string> All = new[] { Pending, None, Local, LargeLocal, Systemic };
	}

	public class HospitalImmunoCourse
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public string Extract { get; set; } = "";
		public string ExtractEn { get; set; } = "";
		public string Status { get; set; } = HospitalImmunoStatus.Active;
		/// Why it is held or stopped, or why it was resumed (the doctor's words).
		public string? StatusNote { get; set; }
		public DateTime StartedAt { get; set; }
		public int? StartedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class HospitalImmunoDose
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int CourseId { get; set; }
		public int PatientId { get; set; }
		public int Seq { get; set; }
		/// The vial's dilution: "1:1000", "1:100", "1:10", "1:1".
		public string Vial { get; set; } = "";
		public decimal DoseMl { get; set; }
		public DateTime GivenAt { get; set; }
		public int? GivenByEmployeeId { get; set; }
		/// The patient does not leave before this (30 minutes after the dose).
		public DateTime ObserveUntil { get; set; }
		public string Reaction { get; set; } = HospitalImmunoReaction.Pending;
		public int? WhealMm { get; set; }
		public string? ReactionNote { get; set; }
		public DateTime? ReactionAt { get; set; }
		public int? ReactionByEmployeeId { get; set; }
	}

	public static class HospitalImmunotherapyModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalImmunoCourse>(e =>
			{
				e.ToTable("HospitalImmunoCourses");   // no DbSet: each names its table
				e.Property(x => x.Extract).HasMaxLength(150);
				e.Property(x => x.ExtractEn).HasMaxLength(150);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.StatusNote).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalImmunoDose>(e =>
			{
				e.ToTable("HospitalImmunoDoses");
				e.Property(x => x.Vial).HasMaxLength(10);
				e.Property(x => x.DoseMl).HasPrecision(6, 2);
				e.Property(x => x.Reaction).HasMaxLength(12);
				e.Property(x => x.ReactionNote).HasMaxLength(300);
				e.HasIndex(x => new { x.CourseId, x.Seq }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
