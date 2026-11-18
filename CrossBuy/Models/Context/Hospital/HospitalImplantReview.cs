using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H186 — the peri-implant maintenance review (from the dental review: "an implant register ends at the crown;
	// no peri-implant follow-up"). A restored implant is reviewed at least yearly: the deepest probing, bleeding and
	// suppuration on probing, the bone lost since the restoration (radiograph), and mobility. Each review is classified
	// by the 2017 World Workshop case definitions (HospitalImplantReviewRules).
	// deploy/sql/hospital_h186_implant_reviews.sql.
	// ==========================================================================================
	public static class HospitalPeriImplantStatus
	{
		public const string Health = "Health";
		public const string Mucositis = "Mucositis";
		public const string Periimplantitis = "Periimplantitis";
		/// Mobility: the implant is not integrated — failure suspected.
		public const string Failing = "Failing";
		public static readonly IReadOnlyList<string> All = new[] { Health, Mucositis, Periimplantitis, Failing };
	}

	public class HospitalImplantReview
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ImplantId { get; set; }
		public int PatientId { get; set; }
		public DateTime ReviewedOn { get; set; }
		public decimal MaxProbingMm { get; set; }
		public bool Bleeding { get; set; }
		public bool Suppuration { get; set; }
		/// Radiographic bone loss since the restoration was fitted (null: no radiograph taken).
		public decimal? BoneLossMm { get; set; }
		public bool Mobility { get; set; }
		public string Status { get; set; } = HospitalPeriImplantStatus.Health;
		public string? Note { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public static class HospitalImplantReviewModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalImplantReview>(e =>
			{
				e.ToTable("HospitalImplantReviews");   // no DbSet: it names its table
				e.Property(x => x.ReviewedOn).HasColumnType("date");
				e.Property(x => x.MaxProbingMm).HasPrecision(4, 1);
				e.Property(x => x.BoneLossMm).HasPrecision(4, 1);
				e.Property(x => x.Status).HasMaxLength(16);
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.ImplantId, x.ReviewedOn });
			});
		}
	}
}
