using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H244 — the operating theatre's PREFERENCE CARDS and BLOCK TIME.
	//   · A preference card is a surgeon's way of doing a procedure: position, skin preparation, gloves, instruments,
	//     equipment, sutures and notes. The case card shows it for the booked surgeon and procedure. (The consumables
	//     that leave the store stay the procedure kit's, H37 — a card moves no stock.)
	//   · A BLOCK reserves a theatre on a weekday for a surgeon (or a named service line) between two times. UTILISATION
	//     is the minutes of that owner's cases inside the block over the block's minutes, for the weeks looked at.
	// deploy/sql/hospital_h244_theatre_blocks.sql builds the tables.
	// ==========================================================================================
	public class HospitalPreferenceCard
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int SurgeonId { get; set; }
		public int ServiceId { get; set; }
		public string? Position { get; set; }
		public string? SkinPrep { get; set; }
		public string? Gloves { get; set; }
		public string? Instruments { get; set; }
		public string? Equipment { get; set; }
		public string? Sutures { get; set; }
		public string? Notes { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class HospitalTheatreBlock
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int RoomId { get; set; }
		/// 0 = Sunday … 6 = Saturday (System.DayOfWeek).
		public int Weekday { get; set; }
		public int StartMinute { get; set; }
		public int EndMinute { get; set; }
		public int? SurgeonId { get; set; }
		public string? Label { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime UpdatedAt { get; set; }
	}

	public static class HospitalTheatreBlockModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPreferenceCard>(e =>
			{
				e.ToTable("HospitalPreferenceCards");
				e.Property(x => x.Position).HasMaxLength(200); e.Property(x => x.SkinPrep).HasMaxLength(200); e.Property(x => x.Gloves).HasMaxLength(100);
				e.Property(x => x.Instruments).HasMaxLength(1000); e.Property(x => x.Equipment).HasMaxLength(500); e.Property(x => x.Sutures).HasMaxLength(500); e.Property(x => x.Notes).HasMaxLength(1000);
				e.HasIndex(x => new { x.CompanyId, x.SurgeonId, x.ServiceId }).IsUnique();
			});
			b.Entity<HospitalTheatreBlock>(e =>
			{
				e.ToTable("HospitalTheatreBlocks");
				e.Property(x => x.Label).HasMaxLength(100);
				e.HasIndex(x => new { x.CompanyId, x.RoomId, x.Weekday });
			});
		}
	}
}
