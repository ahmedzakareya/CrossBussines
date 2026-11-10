using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — what the patient portal shows of the record (unit H27): results, bills, medicines.
	//
	//   · A RELEASE is the doctor's "the patient may see this": a verified lab order or a signed imaging report
	//     shows its values on the portal only once released (the approved prototype: "a result shows here once
	//     your doctor has reviewed it"). Until then the portal lists it as awaiting the doctor's review, with no
	//     value. A release is once per order.
	//   · A REFILL is a patient asking the doctor to renew a prescribed medicine. The doctor (or the clinic, with
	//     `consult`) approves or declines it with an answer the patient reads ("collect it from the pharmacy
	//     tomorrow"); approving does not dispense — the pharmacy dispenses as for any prescription.
	//   · Bills need nothing new: the portal reads H6's bills and H18's frozen split.
	// CrossBuy/deploy/sql/hospital_h27_portal_records.sql builds the tables.
	// ==========================================================================================

	public static class HospitalPortalResultSource
	{
		public const string Lab = "Lab";
		public const string Radiology = "Radiology";
		public static readonly IReadOnlyList<string> All = new[] { Lab, Radiology };
	}

	public static class HospitalPortalRefillStatus
	{
		public const string Open = "Open";
		public const string Approved = "Approved";
		public const string Declined = "Declined";
		public const string Withdrawn = "Withdrawn";
		public static readonly IReadOnlyList<string> All = new[] { Open, Approved, Declined, Withdrawn };
	}

	public class HospitalPortalRelease
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Source { get; set; } = HospitalPortalResultSource.Lab;
		public int OrderId { get; set; }
		public DateTime ReleasedAt { get; set; }
		public int? ReleasedByEmployeeId { get; set; }
		/// The doctor's word to the patient with the result ("normal, no action needed").
		public string? Note { get; set; }
		/// H33: the note's English twin.
		public string? NoteEn { get; set; }
	}

	public class HospitalPortalRefill
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AccountId { get; set; }
		public int PatientId { get; set; }
		public int PrescriptionLineId { get; set; }
		public string Status { get; set; } = HospitalPortalRefillStatus.Open;
		public string? Note { get; set; }
		public DateTime CreatedAt { get; set; }
		public DateTime? HandledAt { get; set; }
		public int? HandledByEmployeeId { get; set; }
		/// What the doctor tells the patient (why declined, or where and when to collect).
		public string? Answer { get; set; }
		/// H33: the answer's English twin.
		public string? AnswerEn { get; set; }
	}

	public static class HospitalPortalRecordsModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPortalRelease>(e =>
			{
				e.ToTable("HospitalPortalReleases");   // no DbSet: each names its table
				e.Property(x => x.Source).HasMaxLength(20);
				e.Property(x => x.Note).HasMaxLength(500);
				e.Property(x => x.NoteEn).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.Source, x.OrderId }).IsUnique();   // released once
			});
			b.Entity<HospitalPortalRefill>(e =>
			{
				e.ToTable("HospitalPortalRefills");
				e.Property(x => x.Status).HasMaxLength(20);
				e.Property(x => x.Note).HasMaxLength(500);
				e.Property(x => x.Answer).HasMaxLength(500);
				e.Property(x => x.AnswerEn).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.Status });
				e.HasIndex(x => new { x.CompanyId, x.PrescriptionLineId });
			});
		}
	}
}
