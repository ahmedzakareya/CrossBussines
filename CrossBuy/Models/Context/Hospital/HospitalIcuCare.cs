using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H238 — the intensive-care unit's clinical depth:
	//   · SCORES: the Glasgow Coma Scale (eye / verbal / motor) and APACHE II (the acute physiology of the worst first-24 h
	//     values, age and chronic health), each a dated row with its components, so the trend is readable.
	//   · CONTINUOUS INFUSIONS run to a PROTOCOL set when started: the dose unit, the range (min..max), the largest single
	//     step and the target ("MAP ≥ 65"). Every TITRATION is a row (from → to, why) and is refused outside the protocol.
	//   · DEVICES (ventilator, central line, urinary catheter) with insertion and removal, so device-days are counted, and
	//     the DAILY BUNDLE CHECK on each (VAP / central-line / catheter care), complete only when every item is ticked.
	// deploy/sql/hospital_h238_icu_care.sql builds the tables.
	// ==========================================================================================
	public static class HospitalIcuScoreKind
	{
		public const string Gcs = "GCS", Apache2 = "APACHE2";
		public static readonly IReadOnlyList<string> All = new[] { Gcs, Apache2 };
	}

	public static class HospitalIcuDeviceKind
	{
		public const string Ventilator = "Ventilator", CentralLine = "CentralLine", UrinaryCatheter = "Catheter";
		public static readonly IReadOnlyList<string> All = new[] { Ventilator, CentralLine, UrinaryCatheter };
	}

	public class HospitalIcuScore
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string Kind { get; set; } = HospitalIcuScoreKind.Gcs;
		public DateTime At { get; set; }
		public int Total { get; set; }
		/// The components as "key=value;…" (E4;V5;M6, or each APACHE II physiology point).
		public string Detail { get; set; } = "";
		public int? ByEmployeeId { get; set; }
	}

	public class HospitalIcuInfusion
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string Drug { get; set; } = "";
		public string DoseUnit { get; set; } = "mcg/kg/min";
		/// Drug per mL of the bag, in the dose unit's mass (mcg or mg or units), for the pump rate.
		public decimal? ConcPerMl { get; set; }
		public decimal? WeightKg { get; set; }
		public decimal MinDose { get; set; }
		public decimal MaxDose { get; set; }
		public decimal MaxStep { get; set; }
		public string Target { get; set; } = "";
		public decimal Dose { get; set; }
		public DateTime StartedAt { get; set; }
		public DateTime? StoppedAt { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public class HospitalIcuTitration
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int InfusionId { get; set; }
		public DateTime At { get; set; }
		public decimal FromDose { get; set; }
		public decimal ToDose { get; set; }
		public string? Reason { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public class HospitalIcuDevice
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string Kind { get; set; } = HospitalIcuDeviceKind.CentralLine;
		public string? Site { get; set; }
		public DateTime InsertedAt { get; set; }
		public DateTime? RemovedAt { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public class HospitalIcuBundleCheck
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int DeviceId { get; set; }
		/// The ward-clock day checked.
		public DateTime Day { get; set; }
		/// The ticked item codes, comma-separated.
		public string Items { get; set; } = "";
		public bool Complete { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalIcuCareModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalIcuScore>(e =>
			{
				e.ToTable("HospitalIcuScores");
				e.Property(x => x.Kind).HasMaxLength(10); e.Property(x => x.Detail).HasMaxLength(400);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId, x.At });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalIcuInfusion>(e =>
			{
				e.ToTable("HospitalIcuInfusions");
				e.Property(x => x.Drug).HasMaxLength(100); e.Property(x => x.DoseUnit).HasMaxLength(20); e.Property(x => x.Target).HasMaxLength(100);
				e.Property(x => x.ConcPerMl).HasPrecision(12, 4); e.Property(x => x.WeightKg).HasPrecision(5, 1);
				e.Property(x => x.MinDose).HasPrecision(12, 4); e.Property(x => x.MaxDose).HasPrecision(12, 4); e.Property(x => x.MaxStep).HasPrecision(12, 4); e.Property(x => x.Dose).HasPrecision(12, 4);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalIcuTitration>(e =>
			{
				e.ToTable("HospitalIcuTitrations");
				e.Property(x => x.FromDose).HasPrecision(12, 4); e.Property(x => x.ToDose).HasPrecision(12, 4); e.Property(x => x.Reason).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.InfusionId, x.At });
			});
			b.Entity<HospitalIcuDevice>(e =>
			{
				e.ToTable("HospitalIcuDevices");
				e.Property(x => x.Kind).HasMaxLength(15); e.Property(x => x.Site).HasMaxLength(60);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalIcuBundleCheck>(e =>
			{
				e.ToTable("HospitalIcuBundleChecks");
				e.Property(x => x.Day).HasColumnType("date"); e.Property(x => x.Items).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.DeviceId, x.Day }).IsUnique();
			});
		}
	}
}
