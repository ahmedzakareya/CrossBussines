using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H134 — a batch recall. A supplier's or the regulator's notice, or the pharmacy's own suspicion, names one batch
	// of one medicine. Opening the recall moves every unit of that batch, from every store that holds it, into the quarantine
	// store chosen in the pharmacy settings: out of reach of dispensing (FEFO never looks there). Each move is an ordinary
	// inventory transfer, so the inventory's own documents record it.
	// The recall then answers the two questions a recall asks:
	//   · where the batch is now;
	//   · which patients received it (dispensed, delivered to the ward, used in a session's kit), net of doses returned.
	// It ends RELEASED (the notice was withdrawn: the batch goes back to the pharmacy store) or CLOSED (returned to the
	// supplier or destroyed, with a note).
	// CrossBuy/deploy/sql/hospital_h134_recalls.sql builds the table and the settings' quarantine store.
	// ==========================================================================================

	public static class HospitalLotRecallStatus
	{
		public const string Quarantined = "Quarantined", Released = "Released", Closed = "Closed";
		public static readonly IReadOnlyList<string> All = new[] { Quarantined, Released, Closed };
	}

	/// The recall class, as the regulators grade it: I — may cause serious harm or death, II — temporary or reversible harm,
	/// III — unlikely to cause harm.
	public static class HospitalLotRecallClass
	{
		public const string I = "I", II = "II", III = "III";
		public static readonly IReadOnlyList<string> All = new[] { I, II, III };
	}

	public static class HospitalLotRecallSource
	{
		public const string Regulator = "Regulator", Supplier = "Supplier", Internal = "Internal";
		public static readonly IReadOnlyList<string> All = new[] { Regulator, Supplier, Internal };
	}

	public class HospitalLotRecall
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string RecallNo { get; set; } = "";
		public int ItemId { get; set; }
		public string BatchNo { get; set; } = "";
		public string RecallClass { get; set; } = HospitalLotRecallClass.II;
		public string Source { get; set; } = HospitalLotRecallSource.Supplier;
		/// The notice's own number (the regulator's circular, the supplier's letter).
		public string? Reference { get; set; }
		public string Reason { get; set; } = "";
		public string Status { get; set; } = HospitalLotRecallStatus.Quarantined;
		public int QuarantineWarehouseId { get; set; }
		/// Every unit moved into quarantine so far, and the transfers that moved them (TRF numbers, comma-separated).
		public decimal QuarantinedQty { get; set; }
		public string? Transfers { get; set; }
		public DateTime OpenedAt { get; set; }
		public int? OpenedByEmployeeId { get; set; }
		public DateTime? EndedAt { get; set; }
		public int? EndedByEmployeeId { get; set; }
		public string? EndNote { get; set; }
	}

	public static class HospitalLotRecallModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalLotRecall>(e =>
			{
				e.ToTable("HospitalLotRecalls");   // no DbSet: it names its table
				e.Property(x => x.RecallNo).HasMaxLength(20);
				e.Property(x => x.BatchNo).HasMaxLength(80);   // as StockBatches.BatchNo
				e.Property(x => x.RecallClass).HasMaxLength(5);
				e.Property(x => x.Source).HasMaxLength(20);
				e.Property(x => x.Reference).HasMaxLength(100);
				e.Property(x => x.Reason).HasMaxLength(400);
				e.Property(x => x.Status).HasMaxLength(20);
				e.Property(x => x.QuarantinedQty).HasPrecision(18, 4);
				e.Property(x => x.Transfers).HasMaxLength(400);
				e.Property(x => x.EndNote).HasMaxLength(400);
				e.HasIndex(x => new { x.CompanyId, x.RecallNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.ItemId, x.BatchNo });
				e.HasIndex(x => new { x.CompanyId, x.Status });
			});
		}
	}
}
