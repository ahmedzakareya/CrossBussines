using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalRestraintKind
	{
		public const string Physical = "Physical";
		public const string Chemical = "Chemical";
		public const string Seclusion = "Seclusion";
		public static readonly IReadOnlyList<string> All = new[] { Physical, Chemical, Seclusion };
	}

	// HOSPITAL H213 — a restraint or seclusion: a doctor's time-limited order with the reason and what was tried first,
	// the checks the nurses record at its interval, its renewals, and its end with the reason.
	public class HospitalRestraint
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public int AdmissionId { get; set; }
		public string Kind { get; set; } = HospitalRestraintKind.Physical;
		public string? Device { get; set; }
		public string Reason { get; set; } = "";
		public string Alternatives { get; set; } = "";
		public bool FamilyInformed { get; set; }
		public int CheckEveryMinutes { get; set; }
		public DateTime StartedAt { get; set; }
		public DateTime ExpiresAt { get; set; }
		public int Renewals { get; set; }
		public int? OrderedByEmployeeId { get; set; }
		public DateTime? EndedAt { get; set; }
		public string? EndReason { get; set; }
		public int? EndedByEmployeeId { get; set; }
	}

	public class HospitalRestraintCheck
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int RestraintId { get; set; }
		public int PatientId { get; set; }
		public DateTime At { get; set; }
		public bool CirculationOk { get; set; }
		public bool SkinOk { get; set; }
		public bool FluidsOffered { get; set; }
		public bool ToiletOffered { get; set; }
		public bool StillNeeded { get; set; }
		public string? Behaviour { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalRestraintModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalRestraint>(e =>
			{
				e.ToTable("HospitalRestraints");
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Device).HasMaxLength(100);
				e.Property(x => x.Reason).HasMaxLength(500);
				e.Property(x => x.Alternatives).HasMaxLength(500);
				e.Property(x => x.EndReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.EndedAt });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalRestraintCheck>(e =>
			{
				e.ToTable("HospitalRestraintChecks");
				e.Property(x => x.Behaviour).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.RestraintId, x.At });
			});
		}
	}
}
