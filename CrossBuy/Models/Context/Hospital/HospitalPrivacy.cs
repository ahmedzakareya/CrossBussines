using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H255 — BEHAVIOURAL PRIVACY MONITORING. The record of who opened whose file (HospitalPatientAccessLogs) is
	// read for patterns that deserve a privacy officer's look; each becomes an ALERT (one per employee, patient, kind and
	// day) that the officer clears (explained) or confirms (a breach to follow up). The log itself is never changed.
	// deploy/sql/hospital_h255_privacy.sql builds the table.
	// ==========================================================================================
	public static class HospitalPrivacyKind
	{
		/// The employee shares a family name with the patient (looking up relatives).
		public const string SameFamily = "SameFamily";
		/// No appointment, stay, emergency visit or order links the employee to the patient around that day.
		public const string NoCareLink = "NoCareLink";
		/// Far more distinct files opened in a day than the employee's own usual.
		public const string Volume = "Volume";
		/// A restricted (VIP) file opened outside working hours.
		public const string AfterHours = "AfterHours";
		public static readonly IReadOnlyList<string> All = new[] { SameFamily, NoCareLink, Volume, AfterHours };
	}

	public static class HospitalPrivacyStatus
	{
		public const string Open = "Open", Cleared = "Cleared", Confirmed = "Confirmed";
	}

	public class HospitalPrivacyAlert
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int EmployeeId { get; set; }
		/// Null for a volume alert (it is about the day, not one file).
		public int? PatientId { get; set; }
		public string Kind { get; set; } = HospitalPrivacyKind.NoCareLink;
		public DateTime Day { get; set; }
		public int Count { get; set; }
		public string Status { get; set; } = HospitalPrivacyStatus.Open;
		public string? Note { get; set; }
		public DateTime CreatedAt { get; set; }
		public DateTime? ReviewedAt { get; set; }
		public int? ReviewedByEmployeeId { get; set; }
	}

	public static class HospitalPrivacyModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPrivacyAlert>(e =>
			{
				e.ToTable("HospitalPrivacyAlerts");
				e.Property(x => x.Kind).HasMaxLength(12); e.Property(x => x.Status).HasMaxLength(10); e.Property(x => x.Note).HasMaxLength(500);
				e.Property(x => x.Day).HasColumnType("date");
				e.HasIndex(x => new { x.CompanyId, x.EmployeeId, x.PatientId, x.Kind, x.Day }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status });
			});
		}
	}
}
