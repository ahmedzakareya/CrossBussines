using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL LABORATORY (unit H8).
	//
	// A lab TEST is a service of the Lab category; its setup (specimen, section) and its ANALYTES (unit,
	// reference and critical limits) live here. A lab ORDER is born from a signed visit's lab order line
	// (HospitalEncounterOrders, one to one) and walks Ordered → Collected → Received → Resulted → Verified.
	// Two steps: a technician enters the results, a specialist verifies them — a different person unless
	// the verifier administers the hospital — and only a verified result reaches the doctor and the file.
	// A result past a critical limit raises the order's critical flag until someone records that the
	// doctor was told. deploy/sql/hospital_h8_lab.sql builds the tables.
	// ==========================================================================================

	public static class HospitalLabStatus
	{
		public const string Ordered = "Ordered";
		public const string Collected = "Collected";
		public const string Received = "Received";
		public const string Resulted = "Resulted";
		public const string Verified = "Verified";
		public static readonly IReadOnlyList<string> All = new[] { Ordered, Collected, Received, Resulted, Verified };
	}

	public static class HospitalLabPriority
	{
		public const string Routine = "Routine";
		public const string Urgent = "Urgent";
		public const string Stat = "Stat";
		public static readonly IReadOnlyList<string> All = new[] { Routine, Urgent, Stat };
	}

	public static class HospitalLabFlag
	{
		public const string Normal = "N";
		public const string Low = "L";
		public const string High = "H";
		public const string CriticalLow = "LL";
		public const string CriticalHigh = "HH";
	}

	/// The lab setup of a Lab-category service.
	public class HospitalLabTest
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ServiceId { get; set; }
		public string Specimen { get; set; } = "";
		public string? SpecimenEn { get; set; }
		public string Section { get; set; } = "";
		public string? SectionEn { get; set; }
		/// H230: a culture is reported from its microbiology panel (organisms and susceptibility), not an analyte grid.
		public bool IsCulture { get; set; }
	}

	/// One measured value of a test, with its limits. A limit left empty is not checked.
	public class HospitalLabAnalyte
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ServiceId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string? Unit { get; set; }
		public decimal? RefLow { get; set; }
		public decimal? RefHigh { get; set; }
		public decimal? CriticalLow { get; set; }
		public decimal? CriticalHigh { get; set; }
		public int Decimals { get; set; } = 1;
		public int SortOrder { get; set; }
		public bool IsActive { get; set; } = true;
		/// H233: a change beyond this percentage from the patient's previous verified value is flagged (null: no check).
		public decimal? DeltaPercent { get; set; }
		/// H233: a normal value of this analyte may be released without a second person.
		public bool AutoVerify { get; set; }
	}

	public class HospitalLabOrder
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string AccessionNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? EncounterId { get; set; }
		/// The visit's order line it came from — one lab order per line.
		public int? EncounterOrderId { get; set; }
		public int ServiceId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public int? OrderedByEmployeeId { get; set; }
		public string Priority { get; set; } = HospitalLabPriority.Routine;
		public string Status { get; set; } = HospitalLabStatus.Ordered;
		public DateTime OrderedAt { get; set; }
		public DateTime? CollectedAt { get; set; }
		public int? CollectedByEmployeeId { get; set; }
		public DateTime? ReceivedAt { get; set; }
		public int? ReceivedByEmployeeId { get; set; }
		public DateTime? ResultedAt { get; set; }
		public int? ResultedByEmployeeId { get; set; }
		public DateTime? VerifiedAt { get; set; }
		public int? VerifiedByEmployeeId { get; set; }
		public string? Comment { get; set; }
		public bool IsCritical { get; set; }
		/// H233: released by the autoverification rules, not by a person.
		public bool AutoVerified { get; set; }
		public DateTime? CriticalAcknowledgedAt { get; set; }
		public int? CriticalAcknowledgedByEmployeeId { get; set; }
		public string? CriticalNote { get; set; }
	}

	/// A result value — a snapshot of the analyte's name, unit and limits as they were when entered.
	public class HospitalLabResult
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int LabOrderId { get; set; }
		public int AnalyteId { get; set; }
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string? Unit { get; set; }
		public decimal? Value { get; set; }
		public string? TextValue { get; set; }
		public decimal? RefLow { get; set; }
		public decimal? RefHigh { get; set; }
		public string Flag { get; set; } = HospitalLabFlag.Normal;
		public int Decimals { get; set; } = 1;
		public int SortOrder { get; set; }
		/// H233: the previous verified value the delta check compared with, and whether the change was beyond the limit.
		public decimal? DeltaPrevious { get; set; }
		public bool DeltaFailed { get; set; }
	}

	public static class HospitalLabAlertLevel
	{
		/// LL/HH — a blocking alert on any screen of the system until the doctor reads the value back.
		public const string Critical = "Critical";
		/// L/H on an urgent or STAT order — a notice that does not stop the doctor's work.
		public const string Abnormal = "Abnormal";
	}

	public static class HospitalLabAlertReason
	{
		public const string Ordering = "Ordering";
		public const string Chosen = "Chosen";
		public const string Escalation = "Escalation";
	}

	/// Where an alert comes from: a lab order or an imaging order (exactly one of the two ids is set).
	public static class HospitalAlertSource
	{
		public const string Lab = "Lab";
		public const string Radiology = "Radiology";
	}

	/// One alert to one doctor about one lab order (or, since H9, one imaging order). It shows on every screen of the system the doctor
	/// opens until acknowledged; a critical one is acknowledged only by reading its value back. Not
	/// acknowledged within the reminder time it is reminded, within the escalation time it is escalated
	/// to another doctor of the department (a new alert, linked).
	public class HospitalLabCriticalAlert
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int? LabOrderId { get; set; }
		public int? RadOrderId { get; set; }
		public string Level { get; set; } = HospitalLabAlertLevel.Critical;
		public string Reason { get; set; } = HospitalLabAlertReason.Ordering;
		public int ToEmployeeId { get; set; }
		public int? ToPractitionerId { get; set; }
		/// The value the doctor reads back (the first critical result, at its decimals).
		public string? Value { get; set; }
		public bool ViaSms { get; set; }
		public DateTime SentAt { get; set; }
		public int? SentByEmployeeId { get; set; }
		public DateTime? SeenAt { get; set; }
		public DateTime? AcknowledgedAt { get; set; }
		public int? AcknowledgedByEmployeeId { get; set; }
		public string? ReadBack { get; set; }
		public DateTime? RemindedAt { get; set; }
		public DateTime? EscalatedAt { get; set; }
		/// On an escalation: the alert that was not acknowledged.
		public int? EscalatedFromAlertId { get; set; }
	}

	public static class HospitalLabModel
	{
		public static void Configure(ModelBuilder b)
		{
			HospitalLabDeviceModel.Configure(b);   // H198: the analysers' messages and code links
			b.Entity<HospitalLabTest>(e => e.HasIndex(x => new { x.CompanyId, x.ServiceId }).IsUnique());
			HospitalMicroModel.Configure(b);   // H230: microbiology
			HospitalLabQcModel.Configure(b);   // H232: quality control
			HospitalLabRefRangeModel.Configure(b);   // H233: ranges by age and sex
			b.Entity<HospitalLabAnalyte>(e => e.Property(x => x.DeltaPercent).HasPrecision(9, 2));   // H233
			b.Entity<HospitalLabAnalyte>(e =>
			{
				foreach (var p in new[] { nameof(HospitalLabAnalyte.RefLow), nameof(HospitalLabAnalyte.RefHigh), nameof(HospitalLabAnalyte.CriticalLow), nameof(HospitalLabAnalyte.CriticalHigh) })
					e.Property(p).HasPrecision(18, 4);
				e.HasIndex(x => new { x.CompanyId, x.ServiceId, x.Code }).IsUnique();
			});
			b.Entity<HospitalLabOrder>(e =>
			{
				e.HasIndex(x => new { x.CompanyId, x.AccessionNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.EncounterOrderId }).IsUnique().HasFilter("[EncounterOrderId] IS NOT NULL");
				e.HasIndex(x => new { x.CompanyId, x.Status });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalLabResult>(e =>
			{
				e.Property(x => x.Value).HasPrecision(18, 4);
				e.Property(x => x.RefLow).HasPrecision(18, 4);
				e.Property(x => x.RefHigh).HasPrecision(18, 4);
				e.Property(x => x.DeltaPrevious).HasPrecision(18, 4);   // H233
				e.HasOne<HospitalLabOrder>().WithMany().HasForeignKey(x => x.LabOrderId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.LabOrderId, x.AnalyteId }).IsUnique();
			});
			b.Entity<HospitalLabCriticalAlert>(e =>
			{
				e.HasOne<HospitalLabOrder>().WithMany().HasForeignKey(x => x.LabOrderId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.CompanyId, x.ToEmployeeId, x.AcknowledgedAt });
				e.HasIndex(x => new { x.CompanyId, x.LabOrderId });
				e.HasOne<HospitalRadOrder>().WithMany().HasForeignKey(x => x.RadOrderId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.CompanyId, x.RadOrderId });
			});
		}
	}
}
