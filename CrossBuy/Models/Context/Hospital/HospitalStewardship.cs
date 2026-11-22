using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H235 — ANTIMICROBIAL STEWARDSHIP. Every running antimicrobial order (a medicine the drug master marks
	// IsAntimicrobial) is watched:
	//   · a RESTRICTED one (formulary status Restricted) needs an approval by the stewardship team within 24 h; the
	//     first doses are never held — an empirical dose in sepsis must not wait — but an unapproved one turns red;
	//   · every one gets its TIME-OUT at 48–72 h: continue, narrow, switch IV→PO or stop, with the reason. "Stop" stops
	//     the order through the ward's own stop.
	// Each decision is a review row. deploy/sql/hospital_h235_stewardship.sql builds it.
	// ==========================================================================================
	public static class HospitalAbxReviewKind
	{
		public const string Approval = "Approval";
		public const string TimeOut = "TimeOut";
		public static readonly IReadOnlyList<string> All = new[] { Approval, TimeOut };
	}

	public static class HospitalAbxDecision
	{
		public const string Approved = "Approved", Rejected = "Rejected", Continue = "Continue", Narrow = "Narrow", IvToPo = "IvToPo", Stop = "Stop";
		public static readonly IReadOnlyList<string> ForApproval = new[] { Approved, Rejected };
		public static readonly IReadOnlyList<string> ForTimeOut = new[] { Continue, Narrow, IvToPo, Stop };
		public static readonly IReadOnlyList<string> All = ForApproval.Concat(ForTimeOut).ToArray();
	}

	public class HospitalAbxReview
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int MedOrderId { get; set; }
		public int PatientId { get; set; }
		public int AdmissionId { get; set; }
		public string Kind { get; set; } = HospitalAbxReviewKind.TimeOut;
		public string Decision { get; set; } = HospitalAbxDecision.Continue;
		public string? Note { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalStewardshipModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalAbxReview>(e =>
			{
				e.ToTable("HospitalAbxReviews");
				e.Property(x => x.Kind).HasMaxLength(10); e.Property(x => x.Decision).HasMaxLength(10); e.Property(x => x.Note).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.MedOrderId });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
