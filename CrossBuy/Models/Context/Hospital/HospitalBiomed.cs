using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H245 — BIOMEDICAL ENGINEERING on top of the company's fixed assets. A medical device IS a fixed asset
	// (cost, depreciation, maintenance schedules and records stay the asset module's — MaintenanceService — never a
	// second copy); the hospital adds what a biomedical department needs: where it is, its category and risk class,
	// serial/manufacturer/model, whether it is in service, and its EVENTS — out of service, back in service, the
	// electrical-safety test (pass/fail) and a manufacturer's recall.
	// deploy/sql/hospital_h245_biomed.sql builds the tables.
	// ==========================================================================================
	public static class HospitalBiomedRisk
	{
		public const string High = "High", Medium = "Medium", Low = "Low";
		public static readonly IReadOnlyList<string> All = new[] { High, Medium, Low };
	}

	public static class HospitalBiomedCategory
	{
		public const string LifeSupport = "LifeSupport", Monitoring = "Monitoring", Diagnostic = "Diagnostic", Therapeutic = "Therapeutic", Laboratory = "Laboratory", Other = "Other";
		public static readonly IReadOnlyList<string> All = new[] { LifeSupport, Monitoring, Diagnostic, Therapeutic, Laboratory, Other };
	}

	public static class HospitalBiomedStatus
	{
		public const string InService = "InService", Down = "Down", Retired = "Retired";
	}

	public static class HospitalBiomedEventKind
	{
		public const string Down = "Down", Up = "Up", SafetyTest = "SafetyTest", Recall = "Recall";
		public static readonly IReadOnlyList<string> All = new[] { Down, Up, SafetyTest, Recall };
	}

	public class HospitalBiomedDevice
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AssetId { get; set; }
		public int? RoomId { get; set; }
		public string Category { get; set; } = HospitalBiomedCategory.Other;
		public string RiskClass { get; set; } = HospitalBiomedRisk.Medium;
		public string? SerialNo { get; set; }
		public string? Manufacturer { get; set; }
		public string? ModelName { get; set; }
		public string Status { get; set; } = HospitalBiomedStatus.InService;
		public DateTime? DownSince { get; set; }
		public DateTime UpdatedAt { get; set; }
	}

	public class HospitalBiomedEvent
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int DeviceId { get; set; }
		public string Kind { get; set; } = HospitalBiomedEventKind.Down;
		public DateTime At { get; set; }
		/// Safety test: true = pass.
		public bool? Passed { get; set; }
		public string? Detail { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalBiomedModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalBiomedDevice>(e =>
			{
				e.ToTable("HospitalBiomedDevices");
				e.Property(x => x.Category).HasMaxLength(15); e.Property(x => x.RiskClass).HasMaxLength(8); e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.SerialNo).HasMaxLength(60); e.Property(x => x.Manufacturer).HasMaxLength(100); e.Property(x => x.ModelName).HasMaxLength(100);
				e.HasIndex(x => new { x.CompanyId, x.AssetId }).IsUnique();
			});
			b.Entity<HospitalBiomedEvent>(e =>
			{
				e.ToTable("HospitalBiomedEvents");
				e.Property(x => x.Kind).HasMaxLength(12); e.Property(x => x.Detail).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.DeviceId, x.At });
			});
		}
	}
}
