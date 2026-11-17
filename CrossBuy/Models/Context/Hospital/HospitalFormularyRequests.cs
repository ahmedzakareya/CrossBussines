using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H152 — formulary governance: a request to give one patient a medicine the formulary restricts or leaves
	// out, decided by the pharmacy and therapeutics committee (a manager, never the person who asked). It is tied to the
	// prescription line it is for: an approved request lets that line be dispensed (every fill of it); a refused one may
	// name a formulary alternative — the committee's therapeutic interchange — for the prescriber to switch to.
	// CrossBuy/deploy/sql/hospital_h152_formulary_requests.sql builds it.
	// ==========================================================================================
	public static class HospitalFormularyRequestStatus
	{
		public const string Pending = "Pending", Approved = "Approved", Refused = "Refused";
		public static readonly IReadOnlyList<string> All = new[] { Pending, Approved, Refused };
	}

	public class HospitalFormularyRequest
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string RequestNo { get; set; } = "";
		public int PatientId { get; set; }
		public int DispenseId { get; set; }
		public int LineId { get; set; }
		public int ItemId { get; set; }
		/// The formulary status the medicine had when asked (Restricted / NonFormulary).
		public string FormularyStatus { get; set; } = "";
		public string Justification { get; set; } = "";
		public string Status { get; set; } = HospitalFormularyRequestStatus.Pending;
		public string? DecisionNote { get; set; }
		/// The committee's therapeutic interchange: the formulary medicine to use instead (on a refusal).
		public int? AlternativeItemId { get; set; }
		public DateTime RequestedAt { get; set; }
		public int? RequestedByEmployeeId { get; set; }
		public DateTime? DecidedAt { get; set; }
		public int? DecidedByEmployeeId { get; set; }
	}

	public static class HospitalFormularyRequestModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalFormularyRequest>(e =>
			{
				e.ToTable("HospitalFormularyRequests");   // no DbSet: it names its table
				e.Property(x => x.RequestNo).HasMaxLength(20);
				e.Property(x => x.FormularyStatus).HasMaxLength(20);
				e.Property(x => x.Justification).HasMaxLength(500);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.DecisionNote).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.RequestNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.DispenseId });
				e.HasIndex(x => new { x.CompanyId, x.Status });
			});
		}
	}
}
