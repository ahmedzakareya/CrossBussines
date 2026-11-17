using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H158 — the reception desk's HANDOFF: why someone came and where the desk sent them, until the receiving
	// team took them. One row per person sent on: the reason they came (a clinic visit, a test, a scan, a planned stay,
	// a planned operation, an urgent call for help, a referral, a visitor, an administrative errand), the destination
	// and what it is for (the appointment, the order, the operation it is tied to), and its course —
	//   Sent (the desk handed over) → Accepted (the receiving team took responsibility) → Arrived (they are there),
	//   or Failed (the team could not take them, with the reason), or Closed (answered at the desk: a visitor, an errand).
	// A handoff not accepted in time is OVERDUE on the desk's board and can be chased (a reminder is counted).
	// CrossBuy/deploy/sql/hospital_h158_desk_handoffs.sql builds it.
	// ==========================================================================================
	public static class HospitalArrivalReason
	{
		public const string Clinic = "Clinic", Lab = "Lab", Imaging = "Imaging", Admission = "Admission", Operation = "Operation",
			Urgent = "Urgent", Referral = "Referral", Visitor = "Visitor", Admin = "Admin";
		public static readonly IReadOnlyList<string> All = new[] { Clinic, Lab, Imaging, Admission, Operation, Urgent, Referral, Visitor, Admin };
	}

	public static class HospitalHandoffDestination
	{
		public const string Clinic = "Clinic", Lab = "Lab", Imaging = "Imaging", Ward = "Ward", Theatre = "Theatre", Emergency = "Emergency", Billing = "Billing", Desk = "Desk";
		public static readonly IReadOnlyList<string> All = new[] { Clinic, Lab, Imaging, Ward, Theatre, Emergency, Billing, Desk };
	}

	public static class HospitalHandoffStatus
	{
		public const string Sent = "Sent", Accepted = "Accepted", Arrived = "Arrived", Failed = "Failed", Closed = "Closed";
		public static readonly IReadOnlyList<string> All = new[] { Sent, Accepted, Arrived, Failed, Closed };
		public static bool Open(string s) => s is Sent or Accepted;
	}

	public static class HospitalHandoffLink
	{
		public const string Appointment = "Appointment", LabOrder = "LabOrder", RadOrder = "RadOrder", Surgery = "Surgery", Admission = "Admission";
	}

	public class HospitalDeskHandoff
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string HandoffNo { get; set; } = "";
		/// Null only for an urgent call for help before anyone is identified.
		public int? PatientId { get; set; }
		public string Reason { get; set; } = HospitalArrivalReason.Clinic;
		public string Destination { get; set; } = HospitalHandoffDestination.Clinic;
		public string? LinkKind { get; set; }
		public int? LinkId { get; set; }
		/// What it is for, in the words the receiving team reads (the order, the clinic, the operation).
		public string? Detail { get; set; }
		public string? DetailEn { get; set; }
		public string? ReferralFrom { get; set; }
		public string? VisitorName { get; set; }
		public string? VisitorRelation { get; set; }
		public string? Note { get; set; }
		public string Status { get; set; } = HospitalHandoffStatus.Sent;
		public DateTime SentAt { get; set; }
		public int? SentByEmployeeId { get; set; }
		public DateTime? AcceptedAt { get; set; }
		public int? AcceptedByEmployeeId { get; set; }
		public DateTime? ArrivedAt { get; set; }
		public int? ArrivedByEmployeeId { get; set; }
		public DateTime? FailedAt { get; set; }
		public int? FailedByEmployeeId { get; set; }
		public string? FailReason { get; set; }
		public DateTime? RemindedAt { get; set; }
		public int Reminders { get; set; }
	}

	public static class HospitalDeskHandoffModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDeskHandoff>(e =>
			{
				e.ToTable("HospitalDeskHandoffs");   // no DbSet: it names its table
				e.Property(x => x.HandoffNo).HasMaxLength(20);
				e.Property(x => x.Reason).HasMaxLength(20);
				e.Property(x => x.Destination).HasMaxLength(20);
				e.Property(x => x.LinkKind).HasMaxLength(20);
				e.Property(x => x.Detail).HasMaxLength(200);
				e.Property(x => x.DetailEn).HasMaxLength(200);
				e.Property(x => x.ReferralFrom).HasMaxLength(200);
				e.Property(x => x.VisitorName).HasMaxLength(120);
				e.Property(x => x.VisitorRelation).HasMaxLength(60);
				e.Property(x => x.Note).HasMaxLength(300);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.FailReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.HandoffNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
