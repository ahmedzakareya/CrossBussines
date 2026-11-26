using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Crm
{
	// CRM C6 — follow-up sequences and meeting booking. Slice: deploy/sql/crm_c6_sequences.sql.

	public class CrmSequence
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string Target { get; set; } = "Lead";       // Lead | Contact
		public bool IsActive { get; set; } = true;
		public bool SkipNonWorkDays { get; set; } = true;  // a step due on the owner's day off or a public holiday waits
		public int? CreatedByEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	public class CrmSequenceStep
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int SequenceId { get; set; }
		public int StepNo { get; set; }
		public int DayOffset { get; set; }                 // days after enrolment
		public string Kind { get; set; } = "Email";        // Email | WhatsApp | Call | Task
		public string? Subject { get; set; }
		public string? Body { get; set; }
		public string? Template { get; set; }              // WhatsApp approved template
	}

	public class CrmSequenceEnrollment
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int SequenceId { get; set; }
		public string SubjectType { get; set; } = "Lead";  // Lead | Contact
		public int SubjectId { get; set; }
		public int? OwnerEmployeeId { get; set; }
		public DateTime EnrolledAt { get; set; }
		public int? EnrolledByEmployeeId { get; set; }
		public string Status { get; set; } = "Active";     // Active | Completed | Stopped
		public string? StopReason { get; set; }            // Replied | Booked | Converted | OptOut | Manual | Inactive
		public DateTime? StoppedAt { get; set; }
		public int? NextStepNo { get; set; }
		public DateTime? NextDueAt { get; set; }           // UTC
	}

	public class CrmSequenceEvent
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int EnrollmentId { get; set; }
		public int? StepNo { get; set; }
		public DateTime At { get; set; }
		public string Outcome { get; set; } = "Queued";    // Queued | Sent | Task | Skipped | Failed | Stopped | Enrolled | Completed
		public string? Detail { get; set; }
		public int? RefId { get; set; }
	}

	public class CrmBookingLink
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int EmployeeId { get; set; }
		public string PublicKey { get; set; } = "";
		public string CalendarToken { get; set; } = "";
		public string? PublicOrigin { get; set; }          // scheme://host the link was saved from
		public string? Title { get; set; }
		public int DurationMinutes { get; set; } = 30;
		public int BufferMinutes { get; set; } = 10;
		public int DaysAhead { get; set; } = 14;
		public int MinNoticeHours { get; set; } = 4;
		public bool IsActive { get; set; } = true;
	}

	public class CrmBooking
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int LinkId { get; set; }
		public int EmployeeId { get; set; }
		public DateTime StartsAt { get; set; }             // UTC
		public DateTime EndsAt { get; set; }
		public string Name { get; set; } = "";
		public string? Email { get; set; }
		public string? Phone { get; set; }
		public string? Note { get; set; }
		public string Status { get; set; } = "Booked";
		public int? ActivityId { get; set; }
		public int? LeadId { get; set; }
		public int? ContactId { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	public static class CrmSequenceModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<CrmSequence>(e =>
			{
				e.ToTable("CrmSequences");
				e.Property(x => x.Name).HasMaxLength(150); e.Property(x => x.NameEn).HasMaxLength(150);
				e.Property(x => x.Target).HasMaxLength(10).IsUnicode(false);
			});
			b.Entity<CrmSequenceStep>(e =>
			{
				e.ToTable("CrmSequenceSteps");
				e.Property(x => x.Kind).HasMaxLength(10).IsUnicode(false);
				e.Property(x => x.Subject).HasMaxLength(200); e.Property(x => x.Body).HasMaxLength(2000);
				e.Property(x => x.Template).HasMaxLength(100).IsUnicode(false);
				e.HasIndex(x => new { x.SequenceId, x.StepNo }).IsUnique();
			});
			b.Entity<CrmSequenceEnrollment>(e =>
			{
				e.ToTable("CrmSequenceEnrollments");
				e.Property(x => x.SubjectType).HasMaxLength(10).IsUnicode(false);
				e.Property(x => x.Status).HasMaxLength(10).IsUnicode(false);
				e.Property(x => x.StopReason).HasMaxLength(12).IsUnicode(false);
				// one ACTIVE walk per person and sequence: the slice's filtered unique index (a filter here breaks SQLite hosts)
				e.HasIndex(x => new { x.CompanyId, x.Status, x.NextDueAt });
			});
			b.Entity<CrmSequenceEvent>(e =>
			{
				e.ToTable("CrmSequenceEvents");
				e.Property(x => x.Outcome).HasMaxLength(10).IsUnicode(false);
				e.Property(x => x.Detail).HasMaxLength(300);
				e.HasIndex(x => new { x.EnrollmentId, x.ID });
			});
			b.Entity<CrmBookingLink>(e =>
			{
				e.ToTable("CrmBookingLinks");
				e.Property(x => x.PublicKey).HasMaxLength(32).IsUnicode(false);
				e.Property(x => x.CalendarToken).HasMaxLength(48).IsUnicode(false); e.Property(x => x.PublicOrigin).HasMaxLength(200).IsUnicode(false);
				e.Property(x => x.Title).HasMaxLength(150);
				e.HasIndex(x => x.PublicKey).IsUnique(); e.HasIndex(x => x.CalendarToken).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.EmployeeId }).IsUnique();
			});
			b.Entity<CrmBooking>(e =>
			{
				e.ToTable("CrmBookings");
				e.Property(x => x.Name).HasMaxLength(150); e.Property(x => x.Email).HasMaxLength(150); e.Property(x => x.Phone).HasMaxLength(50);
				e.Property(x => x.Note).HasMaxLength(1000); e.Property(x => x.Status).HasMaxLength(10).IsUnicode(false);
				e.HasIndex(x => new { x.EmployeeId, x.StartsAt });
			});
		}
	}
}
