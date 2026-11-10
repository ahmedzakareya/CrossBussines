using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H68 — ON-CALL (the prototype's "on-call"): who a department calls outside the sessions. A shift is a
	// department, a doctor, a level (first on call, or the backup above them) and a span of ward time — a night shift
	// runs past midnight. A department holds at most one first and one backup at any moment, a doctor is never on two
	// shifts at once, and a doctor on leave or behind an expired licence is not put on call. The lab's critical-result
	// escalation reads it: an alert nobody acknowledged goes to the department's on-call doctor first.
	// CrossBuy/deploy/sql/hospital_h68_on_call.sql builds the table.
	// ==========================================================================================
	public static class HospitalOnCallLevel
	{
		public const string First = "First";
		public const string Backup = "Backup";
		public static readonly IReadOnlyList<string> All = new[] { First, Backup };
	}

	public class HospitalOnCallShift
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int DepartmentId { get; set; }
		public int PractitionerId { get; set; }
		public string Level { get; set; } = HospitalOnCallLevel.First;
		/// Ward time (the roster is read on the ward's clock, as the sessions are).
		public DateTime StartsAt { get; set; }
		/// Ward time; after StartsAt, at most a day later.
		public DateTime EndsAt { get; set; }
		public string? Note { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? CancelledAt { get; set; }
		public int? CancelledByEmployeeId { get; set; }
		public string? CancelReason { get; set; }
	}

	public static class HospitalOnCallModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalOnCallShift>(e =>
			{
				e.ToTable("HospitalOnCallShifts");   // no DbSet: it names its table
				e.Property(x => x.Level).HasMaxLength(10);
				e.Property(x => x.Note).HasMaxLength(300);
				e.Property(x => x.CancelReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.DepartmentId, x.StartsAt });
				e.HasIndex(x => new { x.CompanyId, x.PractitionerId, x.StartsAt });
			});
		}
	}
}
