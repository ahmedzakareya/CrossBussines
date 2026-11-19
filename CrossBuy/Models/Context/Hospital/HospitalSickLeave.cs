using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H56 — the SICK LEAVE certificate (the prototype's "medical records: sick leave"). A doctor issues it on
	// the patient's file: the days, the recommendation, and the diagnosis — printed on the certificate only when the
	// doctor ticks it (an employer is not owed the diagnosis). It is numbered (SL-000001) and final once issued; a wrong
	// one is VOIDED with a reason, never deleted or edited. It prints only through Report Studio (Hospital.SickLeave).
	// CrossBuy/deploy/sql/hospital_h56_sick_leave.sql builds the table.
	// ==========================================================================================
	public static class HospitalSickLeaveKind
	{
		/// Off work entirely.
		public const string Rest = "Rest";
		/// At work, on light duties.
		public const string LightDuty = "LightDuty";
		/// In hospital for those days.
		public const string Hospitalised = "Hospitalised";
		public static readonly IReadOnlyList<string> All = new[] { Rest, LightDuty, Hospitalised };
	}

	public class HospitalSickLeave
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string LeaveNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? EncounterId { get; set; }
		public int? PractitionerId { get; set; }
		public DateTime FromDate { get; set; }
		public DateTime ToDate { get; set; }
		public int Days { get; set; }
		public string Kind { get; set; } = HospitalSickLeaveKind.Rest;
		public string? Diagnosis { get; set; }
		public bool ShowDiagnosis { get; set; }
		public string? Note { get; set; }
		public DateTime IssuedAt { get; set; }
		public int IssuedByEmployeeId { get; set; }
		public DateTime? VoidedAt { get; set; }
		public int? VoidedByEmployeeId { get; set; }
		public string? VoidReason { get; set; }
		/// H208: the code the certificate's QR carries — anyone holding the paper checks it on the public verification page.
		public string? VerifyCode { get; set; }
		/// H208 (Kuwait): a private hospital's sick leave counts only once the Ministry of Health authenticates it; the
		/// authentication's reference, when, and who recorded it.
		public string? MohAuthRef { get; set; }
		public DateTime? MohAuthAt { get; set; }
		public int? MohAuthByEmployeeId { get; set; }
	}

	public static class HospitalSickLeaveModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalSickLeave>(e =>
			{
				e.ToTable("HospitalSickLeaves");   // no DbSet: it names its table
				e.Property(x => x.LeaveNo).HasMaxLength(20);
				e.Property(x => x.FromDate).HasColumnType("date");
				e.Property(x => x.ToDate).HasColumnType("date");
				e.Property(x => x.Kind).HasMaxLength(20);
				e.Property(x => x.Diagnosis).HasMaxLength(500);
				e.Property(x => x.Note).HasMaxLength(500);
				e.Property(x => x.VoidReason).HasMaxLength(300);
				e.Property(x => x.VerifyCode).HasMaxLength(16);   // H208
				e.Property(x => x.MohAuthRef).HasMaxLength(60);
				e.HasIndex(x => x.VerifyCode).IsUnique().HasFilter("[VerifyCode] IS NOT NULL");
				e.HasIndex(x => new { x.CompanyId, x.LeaveNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
