using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalCardioKind
	{
		public const string Ecg = "ECG";
		public const string Echo = "Echo";
		public static readonly IReadOnlyList<string> All = new[] { Ecg, Echo };
	}

	public static class HospitalCardioStatus
	{
		public const string Requested = "Requested";
		public const string Reported = "Reported";
		public static readonly IReadOnlyList<string> All = new[] { Requested, Reported };
	}

	// HOSPITAL H218 — a cardiac study: a 12-lead ECG (rate, rhythm, intervals, QTc, axis) or a transthoracic echo
	// (ejection fraction, chambers, valves, wall motion, pericardium), requested by a doctor and reported by the
	// cardiologist; a critical finding is flagged on the record.
	public class HospitalCardioStudy
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string StudyNo { get; set; } = "";
		public int PatientId { get; set; }
		public string Kind { get; set; } = HospitalCardioKind.Ecg;
		public string Indication { get; set; } = "";
		public string Status { get; set; } = HospitalCardioStatus.Requested;
		public DateTime RequestedAt { get; set; }
		public int? RequestedByEmployeeId { get; set; }
		public int? HeartRate { get; set; }
		public string? Rhythm { get; set; }
		public int? PrMs { get; set; }
		public int? QrsMs { get; set; }
		public int? QtMs { get; set; }
		public int? QtcMs { get; set; }
		public int? Axis { get; set; }
		public int? EfPercent { get; set; }
		public string? Chambers { get; set; }
		public string? Valves { get; set; }
		public string? WallMotion { get; set; }
		public string? Pericardium { get; set; }
		public string? Interpretation { get; set; }
		public bool Critical { get; set; }
		public string? CriticalWhy { get; set; }
		public DateTime? ReportedAt { get; set; }
		public int? ReportedByEmployeeId { get; set; }
	}

	public static class HospitalCardiologyModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalCardioStudy>(e =>
			{
				e.ToTable("HospitalCardioStudies");
				e.Property(x => x.StudyNo).HasMaxLength(20);
				e.Property(x => x.Kind).HasMaxLength(5);
				e.Property(x => x.Indication).HasMaxLength(500);
				e.Property(x => x.Status).HasMaxLength(10).IsConcurrencyToken();
				e.Property(x => x.Rhythm).HasMaxLength(100);
				e.Property(x => x.Chambers).HasMaxLength(500);
				e.Property(x => x.Valves).HasMaxLength(500);
				e.Property(x => x.WallMotion).HasMaxLength(500);
				e.Property(x => x.Pericardium).HasMaxLength(200);
				e.Property(x => x.Interpretation).HasMaxLength(2000);
				e.Property(x => x.CriticalWhy).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.StudyNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
