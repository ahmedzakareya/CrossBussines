using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H249 — SECURE MESSAGING on the patient portal. A portal account writes about one of its people (self or a
	// family member it holds); the clinic answers from its inbox. A THREAD has a topic and stays open until the clinic
	// closes it; the clinic owes a reply within the response time (two days), so an unanswered patient message shows as
	// overdue. Read times are kept on both sides.
	// deploy/sql/hospital_h249_portal_messages.sql builds the tables.
	// ==========================================================================================
	public static class HospitalPortalTopic
	{
		public const string Clinical = "Clinical", Appointment = "Appointment", Billing = "Billing", Other = "Other";
		public static readonly IReadOnlyList<string> All = new[] { Clinical, Appointment, Billing, Other };
	}

	public static class HospitalPortalThreadStatus
	{
		public const string Open = "Open", Closed = "Closed";
	}

	public class HospitalPortalThread
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AccountId { get; set; }
		public int PatientId { get; set; }
		public string Topic { get; set; } = HospitalPortalTopic.Other;
		public string Subject { get; set; } = "";
		public string Status { get; set; } = HospitalPortalThreadStatus.Open;
		public int? AssignedEmployeeId { get; set; }
		/// True while the last word is the patient's (the clinic owes a reply).
		public bool AwaitingClinic { get; set; }
		public DateTime CreatedAt { get; set; }
		public DateTime LastMessageAt { get; set; }
		public DateTime? ClosedAt { get; set; }
	}

	public class HospitalPortalMessage
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ThreadId { get; set; }
		public bool FromPatient { get; set; }
		public int? EmployeeId { get; set; }
		public string Body { get; set; } = "";
		public DateTime At { get; set; }
		public DateTime? ReadAt { get; set; }
	}

	public static class HospitalPortalMessagesModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPortalThread>(e =>
			{
				e.ToTable("HospitalPortalThreads");
				e.Property(x => x.Topic).HasMaxLength(12); e.Property(x => x.Subject).HasMaxLength(150); e.Property(x => x.Status).HasMaxLength(8);
				e.HasIndex(x => new { x.CompanyId, x.AccountId, x.LastMessageAt });
				e.HasIndex(x => new { x.CompanyId, x.Status, x.AwaitingClinic });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalPortalMessage>(e =>
			{
				e.ToTable("HospitalPortalMessages");
				e.Property(x => x.Body).HasMaxLength(2000);
				e.HasIndex(x => new { x.CompanyId, x.ThreadId, x.At });
			});
		}
	}
}
