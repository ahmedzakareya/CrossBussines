using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalInterfaceKind
	{
		/// An analyser or its middleware sends results (HL7 v2 ORU^R01) and gets the HL7 acknowledgement back.
		public const string LabResults = "LabResults";
		/// A modality worklist broker reads the scheduled imaging orders (HL7 v2 ORM^O01 each).
		public const string ImagingWorklist = "ImagingWorklist";
		public static readonly IReadOnlyList<string> All = new[] { LabResults, ImagingWorklist };
	}

	// HOSPITAL H215 — a machine allowed to talk to the hospital without a person at a screen: its key (kept only as a
	// hash; shown once), what it may do, and the employee it acts for — every message is checked against that
	// person's rights, so a machine never has more than the one who answers for it.
	public class HospitalInterface
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string Kind { get; set; } = HospitalInterfaceKind.LabResults;
		public string KeyHash { get; set; } = "";
		public string KeyHint { get; set; } = "";
		public int ActsAsEmployeeId { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? LastUsedAt { get; set; }
		public int Uses { get; set; }
	}

	public static class HospitalInterfaceModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalInterface>(e =>
			{
				e.ToTable("HospitalInterfaces");
				e.Property(x => x.Code).HasMaxLength(30);
				e.Property(x => x.Name).HasMaxLength(100);
				e.Property(x => x.Kind).HasMaxLength(20);
				e.Property(x => x.KeyHash).HasMaxLength(64);
				e.Property(x => x.KeyHint).HasMaxLength(8);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
				e.HasIndex(x => x.KeyHash).IsUnique();
			});
		}
	}
}
