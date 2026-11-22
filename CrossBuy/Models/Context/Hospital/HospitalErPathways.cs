using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H237 — TIME-CRITICAL EMERGENCY PATHWAYS and AMBULANCE PRE-ARRIVAL.
	//   · A PATHWAY (sepsis, stroke, STEMI, trauma) is activated on an emergency visit; its STEPS run against the clock
	//     from the door (the visit's arrival): each is done (on time or late) or set aside as not indicated, with why.
	//     The board shows the next step and anything overdue.
	//   · A PRE-ARRIVAL is the ambulance's call before the patient is at the door: expected time, complaint, estimated
	//     ESI and the pathway to pre-activate. When the patient arrives it opens the emergency visit (arrival by
	//     ambulance) with that pathway already running from the real door time.
	// deploy/sql/hospital_h237_er_pathways.sql builds the tables.
	// ==========================================================================================
	public static class HospitalErPathwayKind
	{
		public const string Sepsis = "Sepsis", Stroke = "Stroke", Stemi = "Stemi", Trauma = "Trauma";
		public static readonly IReadOnlyList<string> All = new[] { Sepsis, Stroke, Stemi, Trauma };
	}

	public static class HospitalPreArrivalStatus
	{
		public const string Expected = "Expected", Arrived = "Arrived", Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Expected, Arrived, Cancelled };
	}

	public class HospitalErPathway
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ErVisitId { get; set; }
		public int? PatientId { get; set; }
		public string Kind { get; set; } = HospitalErPathwayKind.Sepsis;
		public DateTime ActivatedAt { get; set; }
		public int? ActivatedByEmployeeId { get; set; }
		public DateTime? ClosedAt { get; set; }
		public string? Note { get; set; }
	}

	public class HospitalErPathwayStep
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PathwayId { get; set; }
		public string Code { get; set; } = "";
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
		/// Set aside rather than done: not indicated, with the reason.
		public bool NotIndicated { get; set; }
		public string? Note { get; set; }
	}

	public class HospitalErPreArrival
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public DateTime ExpectedAt { get; set; }
		public string? Unit { get; set; }
		public string Complaint { get; set; } = "";
		public string? AgeText { get; set; }
		public string? Sex { get; set; }
		public int? EsiEstimate { get; set; }
		public string? Pathway { get; set; }
		public string Status { get; set; } = HospitalPreArrivalStatus.Expected;
		public int? ErVisitId { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public static class HospitalErPathwayModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalErPathway>(e =>
			{
				e.ToTable("HospitalErPathways");
				e.Property(x => x.Kind).HasMaxLength(10); e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.ErVisitId, x.Kind }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalErPathwayStep>(e =>
			{
				e.ToTable("HospitalErPathwaySteps");
				e.Property(x => x.Code).HasMaxLength(20); e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.PathwayId, x.Code }).IsUnique();
			});
			b.Entity<HospitalErPreArrival>(e =>
			{
				e.ToTable("HospitalErPreArrivals");
				e.Property(x => x.Unit).HasMaxLength(60); e.Property(x => x.Complaint).HasMaxLength(300); e.Property(x => x.AgeText).HasMaxLength(20);
				e.Property(x => x.Sex).HasMaxLength(1); e.Property(x => x.Pathway).HasMaxLength(10); e.Property(x => x.Status).HasMaxLength(10);
				e.HasIndex(x => new { x.CompanyId, x.Status, x.ExpectedAt });
			});
		}
	}
}
