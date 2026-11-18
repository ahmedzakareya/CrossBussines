using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H178 — closing the loop on results (from the review: "the order → result → alert loop, with an owner until
	// it is closed"). Every verified lab result and every signed imaging report has an OWNER — the doctor who ordered it,
	// or whoever it was handed to — and stays open in that owner's inbox until they record that they reviewed it and what
	// they did. A result left open past its time is overdue and shows on the clinical lead's list. Critical values keep
	// their own blocking read-back (H63); this is the follow-through after it.
	// A row is written when the result is handed over or reviewed; an inbox is the results with no review row.
	// deploy/sql/hospital_h178_result_reviews.sql.
	// ==========================================================================================
	public static class HospitalResultKind
	{
		public const string Lab = "Lab";
		public const string Rad = "Rad";
		public static readonly IReadOnlyList<string> All = new[] { Lab, Rad };
	}

	public static class HospitalResultAction
	{
		public const string NoAction = "NoAction";
		public const string InformedPatient = "InformedPatient";
		public const string ChangedTreatment = "ChangedTreatment";
		public const string FollowUpBooked = "FollowUpBooked";
		public const string Repeated = "Repeated";
		public const string Referred = "Referred";
		public static readonly IReadOnlyList<string> All = new[] { NoAction, InformedPatient, ChangedTreatment, FollowUpBooked, Repeated, Referred };
	}

	public class HospitalResultReview
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public string Kind { get; set; } = HospitalResultKind.Lab;
		public int OrderId { get; set; }
		/// Who owns it now: the ordering doctor, or whoever it was handed to.
		public int OwnerEmployeeId { get; set; }
		public DateTime? HandedAt { get; set; }
		public int? HandedByEmployeeId { get; set; }
		public string? HandReason { get; set; }
		public DateTime? ReviewedAt { get; set; }
		public int? ReviewedByEmployeeId { get; set; }
		public string? Action { get; set; }
		public string? Note { get; set; }
	}

	public static class HospitalResultReviewModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalResultReview>(e =>
			{
				e.ToTable("HospitalResultReviews");   // no DbSet: it names its table
				e.Property(x => x.Kind).HasMaxLength(4);
				e.Property(x => x.HandReason).HasMaxLength(300);
				e.Property(x => x.Action).HasMaxLength(20);
				e.Property(x => x.Note).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.Kind, x.OrderId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.OwnerEmployeeId, x.ReviewedAt });
			});
		}
	}
}
