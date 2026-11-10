using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H78 — FLUID BALANCE (INTAKE AND OUTPUT CHART). Phase 2 of the 2026-11-09 gap research: every inpatient
	// nursing system keeps it. Each entry is one measured volume in (oral, IV, tube feed, blood) or out (urine, drain,
	// vomit, nasogastric, stool, blood loss), at the time it happened. The fluid day runs 07:00 to 07:00 on the ward clock;
	// its totals and balance are computed, never stored. A wrong entry is voided with a reason — it stays on the record.
	// CrossBuy/deploy/sql/hospital_h78_fluid_balance.sql builds the table.
	// ==========================================================================================
	public static class HospitalFluidDirection
	{
		public const string In = "In";
		public const string Out = "Out";
	}

	public static class HospitalFluidKind
	{
		public const string Oral = "Oral";
		public const string Iv = "IV";
		public const string Enteral = "Enteral";
		public const string Blood = "Blood";
		public const string OtherIn = "OtherIn";
		public const string Urine = "Urine";
		public const string Drain = "Drain";
		public const string Vomit = "Vomit";
		public const string Ng = "NG";
		public const string Stool = "Stool";
		public const string BloodLoss = "BloodLoss";
		public const string OtherOut = "OtherOut";
		public static readonly IReadOnlyList<string> In = new[] { Oral, Iv, Enteral, Blood, OtherIn };
		public static readonly IReadOnlyList<string> Out = new[] { Urine, Drain, Vomit, Ng, Stool, BloodLoss, OtherOut };
		public static readonly IReadOnlyList<string> All = In.Concat(Out).ToArray();
		public static string DirectionOf(string kind) => In.Contains(kind) ? HospitalFluidDirection.In : HospitalFluidDirection.Out;
	}

	public class HospitalFluidEntry
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string Direction { get; set; } = HospitalFluidDirection.In;
		public string Kind { get; set; } = HospitalFluidKind.Oral;
		public int VolumeMl { get; set; }
		public string? Note { get; set; }
		/// When it went in or came out (UTC).
		public DateTime At { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? VoidedAt { get; set; }
		public int? VoidedByEmployeeId { get; set; }
		public string? VoidReason { get; set; }
	}

	public static class HospitalFluidModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalFluidEntry>(e =>
			{
				e.ToTable("HospitalFluidEntries");   // no DbSet: it names its table
				e.Property(x => x.Direction).HasMaxLength(3);
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Note).HasMaxLength(200);
				e.Property(x => x.VoidReason).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId, x.At });
			});
		}
	}
}
