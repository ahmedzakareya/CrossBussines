using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H168 — implant stability by protocol, not by one number. Loading readiness depends on the treatment
	// protocol, the site, the insertion torque and the patient (ITI); thresholds differ between contexts. So:
	//   · every stability measurement is kept with its date and moment (at placement, before loading, follow-up),
	//     the ISQ and the insertion torque;
	//   · a restoration records how it is loaded (immediate, early, conventional) and what is fitted (provisional,
	//     definitive);
	//   · the thresholds per loading kind are the CLINIC's protocol, entered and approved by the clinical lead with its
	//     reference — the system invents none;
	//   · below the protocol's threshold a restoration needs a dentist's documented exception (who, why).
	// ==========================================================================================
	public static class HospitalImplantMoment
	{
		public const string Placement = "Placement", BeforeLoading = "BeforeLoading", FollowUp = "FollowUp";
		public static readonly IReadOnlyList<string> All = new[] { Placement, BeforeLoading, FollowUp };
	}

	public static class HospitalImplantLoading
	{
		public const string Immediate = "Immediate", Early = "Early", Conventional = "Conventional";
		public static readonly IReadOnlyList<string> All = new[] { Immediate, Early, Conventional };
	}

	public static class HospitalImplantRestoration
	{
		public const string Provisional = "Provisional", Definitive = "Definitive";
		public static readonly IReadOnlyList<string> All = new[] { Provisional, Definitive };
	}

	public class HospitalImplantMeasure
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ImplantId { get; set; }
		public int PatientId { get; set; }
		public DateTime MeasuredOn { get; set; }
		public string Moment { get; set; } = HospitalImplantMoment.BeforeLoading;
		public int? Isq { get; set; }
		/// Insertion torque, N·cm (at placement).
		public int? TorqueNcm { get; set; }
		public string? Note { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	/// The clinic's loading protocol (one per company): the least ISQ (and, for immediate loading, insertion torque) its
	/// clinical lead accepts for each loading kind. A blank threshold means the protocol sets none for that kind.
	public class HospitalImplantProtocol
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int? MinIsqImmediate { get; set; }
		public int? MinIsqEarly { get; set; }
		public int? MinIsqConventional { get; set; }
		public int? MinTorqueImmediate { get; set; }
		/// The guideline or document it follows (e.g. the ITI treatment guide, a local SOP and its version).
		public string Reference { get; set; } = "";
		public int ApprovedByEmployeeId { get; set; }
		public DateTime ApprovedAt { get; set; }
	}

	public static class HospitalImplantStabilityModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalImplantMeasure>(e =>
			{
				e.ToTable("HospitalImplantMeasures");   // no DbSet: it names its table
				e.Property(x => x.MeasuredOn).HasColumnType("date");
				e.Property(x => x.Moment).HasMaxLength(15);
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.ImplantId });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalImplantProtocol>(e =>
			{
				e.ToTable("HospitalImplantProtocols");
				e.Property(x => x.Reference).HasMaxLength(300);
				e.HasIndex(x => x.CompanyId).IsUnique();
			});
		}
	}
}
