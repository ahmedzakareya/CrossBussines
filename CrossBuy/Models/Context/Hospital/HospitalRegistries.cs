using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalRegistryKind
	{
		public const string Diabetes = "Diabetes", Hypertension = "Hypertension", Ckd = "Ckd";
		public static readonly IReadOnlyList<string> All = new[] { Diabetes, Hypertension, Ckd };
	}

	// ==========================================================================================
	// HOSPITAL H262 — DISEASE REGISTRIES (population health). Membership and care gaps are COMPUTED from what the
	// record already holds — the problem list, signed diagnoses, the chronic-conditions note, laboratory results and
	// recorded blood pressure — so there is nothing to keep in step. The one thing stored is the OUTREACH: who called
	// or messaged a patient about a gap, when, and what was agreed.
	// deploy/sql/hospital_h262_registries.sql builds the table.
	// ==========================================================================================
	public class HospitalRegistryOutreach
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public string Registry { get; set; } = HospitalRegistryKind.Diabetes;
		public string Note { get; set; } = "";
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalRegistryModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalRegistryOutreach>(e =>
			{
				e.ToTable("HospitalRegistryOutreach");
				e.Property(x => x.Registry).HasMaxLength(15); e.Property(x => x.Note).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.Registry, x.At });
			});
		}
	}
}
