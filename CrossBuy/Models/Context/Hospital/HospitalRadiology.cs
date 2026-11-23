using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL RADIOLOGY (unit H9).
	//
	// An imaging EXAM is a service of the Radiology category; its setup (modality, contrast, a report
	// template) lives here. The imaging ROOMS are the machines (X-ray 1, CT 1, ...) with a modality and a
	// maintenance switch. An imaging ORDER is born from a signed visit's radiology order line (one to one)
	// and walks Scheduled → Arrived (given a room of its modality) → Scanning → Reporting → Signed.
	// A radiographer works the scan; a radiologist writes and signs the report — signed, it is locked and
	// reaches the doctor and the file. A report signed Critical is told through the critical-value alert.
	// deploy/sql/hospital_h9_radiology.sql builds the tables.
	// ==========================================================================================

	public static class HospitalRadModality
	{
		public const string XRay = "XR";
		public const string CT = "CT";
		public const string MRI = "MR";
		public const string Ultrasound = "US";
		public static readonly IReadOnlyList<string> All = new[] { XRay, CT, MRI, Ultrasound };
	}

	public static class HospitalRadStatus
	{
		public const string Scheduled = "Scheduled";
		public const string Arrived = "Arrived";
		public const string Scanning = "Scanning";
		public const string Reporting = "Reporting";
		public const string Signed = "Signed";
		public static readonly IReadOnlyList<string> All = new[] { Scheduled, Arrived, Scanning, Reporting, Signed };
	}

	public static class HospitalRadImpression
	{
		public const string Normal = "Normal";
		public const string Abnormal = "Abnormal";
		public const string Critical = "Critical";
		public static readonly IReadOnlyList<string> All = new[] { Normal, Abnormal, Critical };
	}

	/// The imaging setup of a Radiology-category service.
	public class HospitalRadExam
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ServiceId { get; set; }
		public string Modality { get; set; } = HospitalRadModality.XRay;
		public bool Contrast { get; set; }
		/// The report's starting text — findings in the reader's language, edited per patient.
		public string? Template { get; set; }
		public string? TemplateEn { get; set; }
	}

	/// An imaging machine and the room it stands in.
	public class HospitalRadRoom
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string Modality { get; set; } = HospitalRadModality.XRay;
		/// The drawn room of the building it stands in, if any.
		public int? FacilityRoomId { get; set; }
		public bool InMaintenance { get; set; }
		public string? MaintenanceNote { get; set; }
		public bool IsActive { get; set; } = true;
		public int SortOrder { get; set; }
	}

	public class HospitalRadOrder
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string AccessionNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? EncounterId { get; set; }
		public int? EncounterOrderId { get; set; }
		public int ServiceId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string Modality { get; set; } = HospitalRadModality.XRay;
		public bool Contrast { get; set; }
		public int? OrderedByEmployeeId { get; set; }
		public string Priority { get; set; } = HospitalLabPriority.Routine;
		public string Status { get; set; } = HospitalRadStatus.Scheduled;
		public int? RadRoomId { get; set; }
		public DateTime OrderedAt { get; set; }
		public DateTime? ArrivedAt { get; set; }
		public int? ArrivedByEmployeeId { get; set; }
		public DateTime? ScanStartedAt { get; set; }
		public DateTime? ScanEndedAt { get; set; }
		public int? ScannedByEmployeeId { get; set; }
		public string? Findings { get; set; }
		public string? Impression { get; set; }
		/// H33: the report's English twins (the portal reads by the reader's language).
		public string? FindingsEn { get; set; }
		public string? ImpressionEn { get; set; }
		public string? ImpressionCategory { get; set; }
		/// H180: the teeth a dental image shows (FDI numbers, comma separated), so a tooth's radiographs are found by it.
		public string? Teeth { get; set; }
		public DateTime? ReportedAt { get; set; }
		public int? ReportedByEmployeeId { get; set; }
		public DateTime? SignedAt { get; set; }
		public int? SignedByEmployeeId { get; set; }
		public bool IsCritical { get; set; }
		public DateTime? CriticalAcknowledgedAt { get; set; }
		public int? CriticalAcknowledgedByEmployeeId { get; set; }
		public string? CriticalNote { get; set; }
		/// H34: the English added to a report after it was signed — the signed Arabic is never changed; who and when.
		public DateTime? TranslatedAt { get; set; }
		public int? TranslatedByEmployeeId { get; set; }
	}

	public static class HospitalRadiologyModel
	{
		public static void Configure(ModelBuilder b)
		{
			HospitalRadQualityModel.Configure(b);   // H243: structured category, follow-up and dose
			HospitalPacsModel.Configure(b);   // H199: the imaging archive's viewer and order address
			b.Entity<HospitalRadExam>(e => e.HasIndex(x => new { x.CompanyId, x.ServiceId }).IsUnique());
			b.Entity<HospitalRadRoom>(e => e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique());
			b.Entity<HospitalRadOrder>(e =>
			{
				e.Property(x => x.Teeth).HasMaxLength(60);   // H180
				e.HasIndex(x => new { x.CompanyId, x.AccessionNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.EncounterOrderId }).IsUnique().HasFilter("[EncounterOrderId] IS NOT NULL");
				e.HasIndex(x => new { x.CompanyId, x.Status });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
