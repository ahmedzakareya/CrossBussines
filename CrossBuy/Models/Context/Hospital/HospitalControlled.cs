using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — the controlled-drugs register (unit H39), as the approved prototype draws it (#/requisitions,
	// "Controlled drugs register — Ward 3, every movement signed by two nurses"). Which items are controlled is a
	// setting; each dose given or amount wasted carries two signatures — the nurse and a witness who confirmed with her
	// own password — and leaves the ward store through the inventory. CrossBuy/deploy/sql/hospital_h39_controlled_drugs.sql.
	// ==========================================================================================

	public static class HospitalControlledKind
	{
		public const string Given = "Given";
		public const string Wasted = "Wasted";
		public static readonly IReadOnlyList<string> All = new[] { Given, Wasted };
	}

	public class HospitalControlledItem
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ItemId { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	public class HospitalControlledEntry
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		/// The ward store the drug left (a supply unit's store).
		public int WarehouseId { get; set; }
		public int ItemId { get; set; }
		public string Kind { get; set; } = HospitalControlledKind.Given;
		public decimal Qty { get; set; }
		/// The patient a dose was given to (none for a waste).
		public int? PatientId { get; set; }
		public string? Note { get; set; }
		public int RecordedByEmployeeId { get; set; }
		/// The second nurse, who confirmed with her own password.
		public int WitnessEmployeeId { get; set; }
		public DateTime RecordedAt { get; set; }
		/// H52: the bill line a dose given to a patient was charged as (the pharmacy's item price).
		public int? BillLineId { get; set; }
	}

	public static class HospitalControlledModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalControlledItem>(e =>
			{
				e.ToTable("HospitalControlledItems");   // no DbSet: each names its table
				e.HasIndex(x => new { x.CompanyId, x.ItemId }).IsUnique();
			});
			b.Entity<HospitalControlledEntry>(e =>
			{
				e.ToTable("HospitalControlledEntries");
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Qty).HasPrecision(18, 3);
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.WarehouseId, x.ItemId, x.RecordedAt });
			});
		}
	}
}
