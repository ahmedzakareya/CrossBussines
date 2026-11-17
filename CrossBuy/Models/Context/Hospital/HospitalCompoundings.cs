using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H153 — compounding: the batch record of a sterile preparation made in the pharmacy for one patient — a
	// chemotherapy dose, an IV admixture or parenteral nutrition (TPN). It records what was made (the prescription line,
	// the dose, the diluent and final volume, the drug's batch), how it is kept (room or fridge) and until when it may be
	// given (the beyond-use time, capped by USP <797> category 1: 12 h at room temperature, 24 h refrigerated), who made
	// it and who checked it — an independent second pharmacist, never the preparer. A chemotherapy dose given by injection
	// does not leave the pharmacy until its record is checked and RELEASED; a rejected one is made again.
	// CrossBuy/deploy/sql/hospital_h153_compounding.sql builds it.
	// ==========================================================================================
	public static class HospitalCompoundKind
	{
		public const string Chemo = "Chemo", IV = "IV", TPN = "TPN";
		public static readonly IReadOnlyList<string> All = new[] { Chemo, IV, TPN };
	}

	public static class HospitalCompoundStorage
	{
		public const string Room = "Room", Fridge = "Fridge";
		public static readonly IReadOnlyList<string> All = new[] { Room, Fridge };
	}

	public static class HospitalCompoundStatus
	{
		public const string Prepared = "Prepared", Released = "Released", Rejected = "Rejected";
		public static readonly IReadOnlyList<string> All = new[] { Prepared, Released, Rejected };
	}

	public class HospitalCompounding
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string CompoundNo { get; set; } = "";
		public string Kind { get; set; } = HospitalCompoundKind.Chemo;
		public int PatientId { get; set; }
		public int DispenseId { get; set; }
		public int LineId { get; set; }
		/// What the prescription line asks (its own words).
		public string Product { get; set; } = "";
		public decimal? DoseMg { get; set; }
		public string? Route { get; set; }
		public string? Diluent { get; set; }
		public decimal? FinalVolumeMl { get; set; }
		/// The batch of the drug drawn up (and of the diluent, as written).
		public string? DrugBatch { get; set; }
		public string? DiluentBatch { get; set; }
		public string Storage { get; set; } = HospitalCompoundStorage.Room;
		public DateTime BeyondUseAt { get; set; }
		public string? Notes { get; set; }
		public string Status { get; set; } = HospitalCompoundStatus.Prepared;
		public string? RejectReason { get; set; }
		public DateTime PreparedAt { get; set; }
		public int? PreparedByEmployeeId { get; set; }
		public DateTime? CheckedAt { get; set; }
		public int? CheckedByEmployeeId { get; set; }
	}

	public static class HospitalCompoundingModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalCompounding>(e =>
			{
				e.ToTable("HospitalCompoundings");   // no DbSet: it names its table
				e.Property(x => x.CompoundNo).HasMaxLength(20);
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Product).HasMaxLength(200);
				e.Property(x => x.DoseMg).HasPrecision(18, 4);
				e.Property(x => x.Route).HasMaxLength(10);
				e.Property(x => x.Diluent).HasMaxLength(100);
				e.Property(x => x.FinalVolumeMl).HasPrecision(18, 2);
				e.Property(x => x.DrugBatch).HasMaxLength(40);
				e.Property(x => x.DiluentBatch).HasMaxLength(40);
				e.Property(x => x.Storage).HasMaxLength(10);
				e.Property(x => x.Notes).HasMaxLength(300);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.RejectReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.CompoundNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.DispenseId });
				e.HasIndex(x => new { x.CompanyId, x.Status });
			});
		}
	}
}
