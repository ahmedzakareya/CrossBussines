using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL FACILITY — the building as the admin draws it.
	//
	// Floor → (optional) ward → room → bed. A room has a TYPE (how many beds, its features, its
	// daily rate, whether a shared room is single-sex), a place on its floor (a 12-column grid) and
	// an interior drawing (LayoutJson). Every screen that shows a room — the bed board, the hospital
	// map, accommodation — reads the same rows; nothing about the building lives in code.
	//
	// COMPANY ISOLATION IS EXPLICIT, NOT A GLOBAL FILTER. These tables are not in
	// CompanyQueryFilters.PilotEntities, so every read in HospitalFacilityService carries its own
	// CompanyId predicate and the isolation tests pin it. Joining the pilot list is a TAB-1 change.
	// ==========================================================================================

	public static class HospitalBedStatus
	{
		public const string Vacant = "Vacant";
		public const string Occupied = "Occupied";
		public const string Cleaning = "Cleaning";
		public const string Reserved = "Reserved";
		public const string Blocked = "Blocked";

		public static readonly IReadOnlyList<string> All = new[] { Vacant, Occupied, Cleaning, Reserved, Blocked };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);

		/// A bed someone lies in, or is about to, can be neither deleted nor drawn out of its room.
		public static bool HoldsPatient(string? s) => s == Occupied || s == Reserved;
	}

	/// Which spaces a floor holds. Only `Ward`, `Icu` and `Theatre` rooms carry beds; the rest are
	/// places on the map (a clinic, the pharmacy) that people can be in without being admitted.
	public static class HospitalRoomKinds
	{
		public const string Ward = "Ward";
		public const string Icu = "Icu";
		public const string Clinic = "Clinic";
		public const string Emergency = "Emergency";
		public const string Imaging = "Imaging";
		public const string Laboratory = "Laboratory";
		public const string Pharmacy = "Pharmacy";
		public const string Theatre = "Theatre";
		public const string Service = "Service";

		public static readonly IReadOnlyList<string> All = new[] { Ward, Icu, Clinic, Emergency, Imaging, Laboratory, Pharmacy, Theatre, Service };
		public static bool IsKnown(string? k) => k != null && All.Contains(k, StringComparer.Ordinal);
		public static bool CarriesBeds(string? k) => k == Ward || k == Icu;
	}

	public static class HospitalSexPolicy
	{
		public const string Any = "Any";
		public const string Male = "Male";
		public const string Female = "Female";

		public static readonly IReadOnlyList<string> All = new[] { Any, Male, Female };
		public static bool IsKnown(string? p) => p != null && All.Contains(p, StringComparer.Ordinal);
	}

	/// The features a room type may list. Codes, never prose: a screen translates them.
	public static class HospitalRoomFeatures
	{
		public static readonly IReadOnlyList<string> All = new[]
		{
			"bath", "companion", "tv", "fridge", "lounge", "oxygen", "negative-pressure", "anteroom", "monitor",
		};
		public static bool IsKnown(string? f) => f != null && All.Contains(f, StringComparer.Ordinal);
	}

	public class HospitalRoomType
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }

		/// How many beds a NEW room of this type is created with. A drawn room may later hold more or
		/// fewer; the type is the starting point, not a constraint on the drawing.
		public int BedCount { get; set; } = 1;

		/// The daily stay price in the company's functional currency.
		public decimal DailyRate { get; set; }

		/// Comma-separated HospitalRoomFeatures codes.
		public string? Features { get; set; }

		/// A shared room of this type takes one sex at a time.
		public bool SameSexOnly { get; set; }

		public int SortOrder { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class HospitalFloor
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int SortOrder { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	/// A nursing unit. Its sex policy applies to every room in it, on top of the room type's rule.
	public class HospitalWard
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int FloorId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string SexPolicy { get; set; } = HospitalSexPolicy.Any;
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public class HospitalRoom
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int FloorId { get; set; }
		public int? WardId { get; set; }

		/// Null for a space that carries no beds (a clinic, the pharmacy).
		public int? RoomTypeId { get; set; }

		public string Number { get; set; } = "";
		/// Optional label for a non-bed space ("Pharmacy"); a ward room is named by its number.
		public string? Name { get; set; }
		public string? NameEn { get; set; }
		public string Kind { get; set; } = HospitalRoomKinds.Ward;

		// Place on the floor plan: a 12-column grid, 1-based, like the map renders it.
		public int GridColumn { get; set; } = 1;
		public int GridRow { get; set; } = 1;
		public int ColumnSpan { get; set; } = 2;
		public int RowSpan { get; set; } = 1;

		/// The interior drawing: [{ "k": "bed"|"bath"|"door"|..., "x", "y", "w", "h", "bed"?: code }],
		/// coordinates in percent of the room. Null means "draw the type's default".
		public string? LayoutJson { get; set; }

		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class HospitalBed
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int RoomId { get; set; }

		/// Unique in the company: the code the desk says out loud ("302-B").
		public string Code { get; set; } = "";
		public string Status { get; set; } = HospitalBedStatus.Vacant;
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalFacilityModel
	{
		public static void Configure(ModelBuilder b)
		{
			HospitalBiomedModel.Configure(b);   // H245: biomedical devices over the fixed assets
			b.Entity<HospitalRoomType>(e =>
			{
				e.Property(x => x.DailyRate).HasPrecision(18, 4);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalFloor>(e => e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique());
			b.Entity<HospitalWard>(e =>
			{
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.FloorId });
			});
			b.Entity<HospitalRoom>(e =>
			{
				// A room number is unique in the company — the desk and the patient say "room 302", not
				// "room 302 on floor 3".
				e.HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.FloorId });
			});
			b.Entity<HospitalBed>(e =>
			{
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.RoomId });
			});
		}
	}
}
