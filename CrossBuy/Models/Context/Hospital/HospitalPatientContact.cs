using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H163 — how to reach the patient and who speaks for them, kept by the desk:
	//   · CONTACT PREFERENCES (one per patient): the channel (a message, a call), the language, the best time of day,
	//     and the help they need on arrival (a wheelchair, hearing, sight, an interpreter — with the language —, moving
	//     about, other with a word). "No messages at all" stays the SMS opt-out it already is.
	//   · REPRESENTATIVES: a parent, a guardian, a legal attorney, the next of kin — name, relation, phone, ID number,
	//     until when; stopped (never deleted) when no longer valid.
	// The scans (ID card, insurance card) are documents on the patient's record in the document platform.
	// ==========================================================================================
	public static class HospitalContactChannel
	{
		public const string Sms = "Sms", Call = "Call";
		public static readonly IReadOnlyList<string> All = new[] { Sms, Call };
	}

	public static class HospitalBestTime
	{
		public const string Any = "Any", Morning = "Morning", Afternoon = "Afternoon", Evening = "Evening";
		public static readonly IReadOnlyList<string> All = new[] { Any, Morning, Afternoon, Evening };
	}

	public static class HospitalAssistanceNeed
	{
		public const string Wheelchair = "Wheelchair", Hearing = "Hearing", Sight = "Sight", Interpreter = "Interpreter", Mobility = "Mobility", Other = "Other";
		public static readonly IReadOnlyList<string> All = new[] { Wheelchair, Hearing, Sight, Interpreter, Mobility, Other };
	}

	public static class HospitalRepresentativeAuthority
	{
		public const string Parent = "Parent", Guardian = "Guardian", Attorney = "Attorney", NextOfKin = "NextOfKin";
		public static readonly IReadOnlyList<string> All = new[] { Parent, Guardian, Attorney, NextOfKin };
	}

	public class HospitalContactPreference
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public string Channel { get; set; } = HospitalContactChannel.Sms;
		public string Language { get; set; } = "ar";
		public string BestTime { get; set; } = HospitalBestTime.Any;
		/// Comma-separated HospitalAssistanceNeed codes.
		public string? Needs { get; set; }
		public string? InterpreterLanguage { get; set; }
		public string? NeedsNote { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class HospitalPatientRepresentative
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public string Authority { get; set; } = HospitalRepresentativeAuthority.NextOfKin;
		public string Name { get; set; } = "";
		public string? Relation { get; set; }
		public string? Phone { get; set; }
		public string? NationalId { get; set; }
		public DateTime? ValidTo { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? StoppedAt { get; set; }
		public int? StoppedByEmployeeId { get; set; }
		public string? StopReason { get; set; }
	}

	public static class HospitalPatientContactModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalContactPreference>(e =>
			{
				e.ToTable("HospitalContactPreferences");   // no DbSet: it names its table
				e.Property(x => x.Channel).HasMaxLength(10);
				e.Property(x => x.Language).HasMaxLength(5);
				e.Property(x => x.BestTime).HasMaxLength(10);
				e.Property(x => x.Needs).HasMaxLength(100);
				e.Property(x => x.InterpreterLanguage).HasMaxLength(40);
				e.Property(x => x.NeedsNote).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.PatientId }).IsUnique();
			});
			b.Entity<HospitalPatientRepresentative>(e =>
			{
				e.ToTable("HospitalPatientRepresentatives");
				e.Property(x => x.Authority).HasMaxLength(10);
				e.Property(x => x.Name).HasMaxLength(120);
				e.Property(x => x.Relation).HasMaxLength(60);
				e.Property(x => x.Phone).HasMaxLength(30);
				e.Property(x => x.NationalId).HasMaxLength(30);
				e.Property(x => x.ValidTo).HasColumnType("date");
				e.Property(x => x.StopReason).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
