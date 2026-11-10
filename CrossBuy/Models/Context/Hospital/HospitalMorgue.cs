using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H90 — THE MORTUARY (phase 4, the last specialty module). It begins where H59's death certificate ends.
	//   * COMPARTMENTS are the cold chambers' places (A1, A2 … or a freezer); one body in each.
	//   * A CASE is a body received: from a death certified here (H59), or a body brought in whose identity is unknown
	//     (described, and always a police case). It carries a tag, the place it lies, the belongings found with it, a
	//     police hold when the death is medico-legal, and the autopsy when one is asked for.
	//   * The body is RELEASED only when nothing holds it: a certificate that is not voided, the police hold cleared, a
	//     requested autopsy done — and to a named person with their relation and identity number, the belongings
	//     handed over with it. Or it is transferred to another facility (forensic medicine, another hospital).
	// CrossBuy/deploy/sql/hospital_h90_morgue.sql builds the tables.
	// ==========================================================================================
	public static class HospitalMorgueKind
	{
		public const string Cold = "Cold";
		public const string Freezer = "Freezer";
		public static readonly IReadOnlyList<string> All = new[] { Cold, Freezer };
	}

	public static class HospitalMorgueStatus
	{
		public const string InStorage = "InStorage";
		public const string Released = "Released";
		public const string Transferred = "Transferred";
		public static readonly IReadOnlyList<string> All = new[] { InStorage, Released, Transferred };
	}

	public static class HospitalAutopsyStatus
	{
		public const string None = "None";
		public const string Requested = "Requested";
		public const string Done = "Done";
		public static readonly IReadOnlyList<string> All = new[] { None, Requested, Done };
	}

	public class HospitalMorgueCompartment
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Kind { get; set; } = HospitalMorgueKind.Cold;
		public bool IsActive { get; set; } = true;
		public string? Note { get; set; }
	}

	public class HospitalMorgueCase
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string CaseNo { get; set; } = "";
		/// Null for a body whose identity is not known.
		public int? PatientId { get; set; }
		/// H59's certificate, when the death was certified here.
		public int? DeathId { get; set; }
		public string? Description { get; set; }
		public string TagNo { get; set; } = "";
		public int CompartmentId { get; set; }
		public string Status { get; set; } = HospitalMorgueStatus.InStorage;
		public DateTime ReceivedAt { get; set; }
		public int? ReceivedByEmployeeId { get; set; }
		public string? BroughtFrom { get; set; }
		public bool MedicoLegal { get; set; }
		public string? PoliceRef { get; set; }
		public DateTime? PoliceClearedAt { get; set; }
		public int? PoliceClearedByEmployeeId { get; set; }
		public string AutopsyStatus { get; set; } = HospitalAutopsyStatus.None;
		public DateTime? AutopsyDoneAt { get; set; }
		public string? AutopsyNote { get; set; }
		public DateTime? ReleasedAt { get; set; }
		public int? ReleasedByEmployeeId { get; set; }
		public string? ReleasedTo { get; set; }
		public string? ReleasedToRelation { get; set; }
		public string? ReleasedToIdNo { get; set; }
		public string? ReleasedToPhone { get; set; }
		/// The funeral home, or the facility a transfer went to.
		public string? Destination { get; set; }
		public string? Note { get; set; }
	}

	public class HospitalMorgueBelonging
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int CaseId { get; set; }
		public string Item { get; set; } = "";
		public int Quantity { get; set; } = 1;
		public string? Note { get; set; }
		public DateTime RecordedAt { get; set; }
		public int? RecordedByEmployeeId { get; set; }
	}

	public static class HospitalMorgueModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalMorgueCompartment>(e =>
			{
				e.ToTable("HospitalMorgueCompartments");   // no DbSet: each names its table
				e.Property(x => x.Code).HasMaxLength(10);
				e.Property(x => x.Kind).HasMaxLength(8);
				e.Property(x => x.Note).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalMorgueCase>(e =>
			{
				e.ToTable("HospitalMorgueCases");
				e.Property(x => x.CaseNo).HasMaxLength(20);
				e.Property(x => x.Description).HasMaxLength(500);
				e.Property(x => x.TagNo).HasMaxLength(30);
				e.Property(x => x.Status).HasMaxLength(12);
				e.Property(x => x.BroughtFrom).HasMaxLength(150);
				e.Property(x => x.PoliceRef).HasMaxLength(60);
				e.Property(x => x.AutopsyStatus).HasMaxLength(10);
				e.Property(x => x.AutopsyNote).HasMaxLength(500);
				e.Property(x => x.ReleasedTo).HasMaxLength(150);
				e.Property(x => x.ReleasedToRelation).HasMaxLength(50);
				e.Property(x => x.ReleasedToIdNo).HasMaxLength(30);
				e.Property(x => x.ReleasedToPhone).HasMaxLength(30);
				e.Property(x => x.Destination).HasMaxLength(150);
				e.Property(x => x.Note).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.CaseNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status });
			});
			b.Entity<HospitalMorgueBelonging>(e =>
			{
				e.ToTable("HospitalMorgueBelongings");
				e.Property(x => x.Item).HasMaxLength(150);
				e.Property(x => x.Note).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.CaseId });
			});
		}
	}
}
