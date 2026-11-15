using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H122 — a unit's kit and its store. A unit that treats in sessions (the dialysis unit first) names the kit one
	// session uses — an inventory bundle whose bill of materials lists the consumables — and the store it draws from.
	// One row per unit per company. (The theatre's kit is per PROCEDURE and keeps its own H37 tables.)
	// CrossBuy/deploy/sql/hospital_h122_unit_kits.sql builds it.
	// ==========================================================================================
	/// The units with settings — the same names as the treatment-session kinds (H119), so a session finds its unit.
	public static class HospitalKitUnits
	{
		public const string Dialysis = "Dialysis";
		public const string Chemo = "Chemo";        // H123
		public const string Physio = "Physio";      // H123
		public static readonly IReadOnlyList<string> All = new[] { Dialysis, Chemo, Physio };
		/// The units whose sessions draw a kit from a store (H122); the others only name their session service.
		public static readonly IReadOnlyList<string> WithKit = new[] { Dialysis };
	}

	public class HospitalUnitKit
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Unit { get; set; } = HospitalKitUnits.Dialysis;
		/// The bundle one session uses; null until the unit sets it.
		public int? KitItemId { get; set; }
		/// The unit's store; null until set.
		public int? StoreWarehouseId { get; set; }
		/// H123: the catalogue service one completed session is charged as (priced by the bill's price list); null until set.
		public int? SessionServiceId { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalUnitKitModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalUnitKit>(e =>
			{
				e.ToTable("HospitalUnitKits");
				e.Property(x => x.Unit).HasMaxLength(20);
				e.HasIndex(x => new { x.CompanyId, x.Unit }).IsUnique();
			});
		}
	}
}
