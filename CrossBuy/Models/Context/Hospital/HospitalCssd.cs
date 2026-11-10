using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — central sterile supply (unit H42), as the approved prototype draws it (#/cssd: "Instrument-set
	// tracking" — set, description, status, sterile until). An INSTRUMENT SET goes round: awaiting wash → washed and
	// packed → in the autoclave (a CYCLE: its number, the autoclave, passed or failed) → sterile until its shelf life
	// ends → in use on a theatre case (H29) → awaiting wash again. Every step is a SET EVENT, so the set used on a case
	// names the cycle that sterilised it (a recall reads it back). CrossBuy/deploy/sql/hospital_h42_cssd.sql.
	// ==========================================================================================

	public static class HospitalSetStatus
	{
		public const string AwaitingWash = "AwaitingWash";
		public const string Packed = "Packed";
		public const string InAutoclave = "InAutoclave";
		public const string Sterile = "Sterile";
		public const string InUse = "InUse";
		public static readonly IReadOnlyList<string> All = new[] { AwaitingWash, Packed, InAutoclave, Sterile, InUse };
	}

	public static class HospitalCycleResult
	{
		public const string Running = "Running";
		public const string Passed = "Passed";
		public const string Failed = "Failed";
		public static readonly IReadOnlyList<string> All = new[] { Running, Passed, Failed };
	}

	public static class HospitalSetEventKind
	{
		public const string Washed = "Washed";
		public const string Loaded = "Loaded";
		public const string Sterilised = "Sterilised";
		public const string CycleFailed = "CycleFailed";
		public const string Issued = "Issued";
		public const string Returned = "Returned";
		public const string Reprocess = "Reprocess";
		public static readonly IReadOnlyList<string> All = new[] { Washed, Loaded, Sterilised, CycleFailed, Issued, Returned, Reprocess };
	}

	public class HospitalInstrumentSet
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		/// How many days a sterilised set stays sterile on the shelf.
		public int ShelfLifeDays { get; set; } = 30;
		public string Status { get; set; } = HospitalSetStatus.AwaitingWash;
		public DateTime? SterileUntil { get; set; }
		/// The cycle it is in, or that last sterilised it.
		public int? CycleId { get; set; }
		/// The theatre case it is in use on.
		public int? SurgeryId { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
	}

	public class HospitalSterilisationCycle
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string CycleNo { get; set; } = "";
		public string Autoclave { get; set; } = "";
		public string Result { get; set; } = HospitalCycleResult.Running;
		/// Why it failed (the indicator, the printout).
		public string? Note { get; set; }
		public DateTime StartedAt { get; set; }
		public int? StartedByEmployeeId { get; set; }
		public DateTime? EndedAt { get; set; }
		public int? EndedByEmployeeId { get; set; }
	}

	public class HospitalSetEvent
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int SetId { get; set; }
		public string Kind { get; set; } = HospitalSetEventKind.Washed;
		public int? CycleId { get; set; }
		public int? SurgeryId { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalCssdModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalInstrumentSet>(e =>
			{
				e.ToTable("HospitalInstrumentSets");   // no DbSet: each names its table
				e.Property(x => x.Code).HasMaxLength(30);
				e.Property(x => x.Name).HasMaxLength(150);
				e.Property(x => x.NameEn).HasMaxLength(150);
				e.Property(x => x.Status).HasMaxLength(20);
				e.Property(x => x.SterileUntil).HasColumnType("date");
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalSterilisationCycle>(e =>
			{
				e.ToTable("HospitalSterilisationCycles");
				e.Property(x => x.CycleNo).HasMaxLength(20);
				e.Property(x => x.Autoclave).HasMaxLength(60);
				e.Property(x => x.Result).HasMaxLength(10);
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.CycleNo }).IsUnique();
			});
			b.Entity<HospitalSetEvent>(e =>
			{
				e.ToTable("HospitalSetEvents");
				e.Property(x => x.Kind).HasMaxLength(20);
				e.HasIndex(x => new { x.SetId, x.At });
			});
		}
	}
}
