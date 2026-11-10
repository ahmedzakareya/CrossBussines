using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — the central kitchen (unit H44), as the approved prototype draws it (#/kitchen: production today,
	// patient meals, cafeteria, costing & allocation). One kitchen, three consumers: patients (the trays of H43, per
	// ward), staff and the cafeteria (meals the kitchen counts per destination). Every meal is built on a RECIPE — an
	// inventory item with a bill of materials — so its standard cost is known. The kitchen's ingredients leave its
	// store into the KITCHEN COST POOL account (payroll, utilities and depreciation reach that account from their own
	// modules); at month end the whole pool is ALLOCATED to the consumers by meals × standard cost, and posted as one
	// journal with cost centres on its lines (owner ruling, 2026-11-05: a real journal). Its accounts are a setting.
	// CrossBuy/deploy/sql/hospital_h44_kitchen.sql.
	// ==========================================================================================

	public static class HospitalKitchenStream
	{
		public const string Patient = "Patient";
		public const string Staff = "Staff";
		public const string Cafeteria = "Cafeteria";
		public static readonly IReadOnlyList<string> All = new[] { Patient, Staff, Cafeteria };
	}

	public static class HospitalKitchenIssueKind
	{
		public const string Production = "HospitalKitchenIssue";
		public const string Waste = "HospitalKitchenWaste";
	}

	/// The kitchen's setting: its store and the four accounts of the allocation journal (one row per company).
	public class HospitalKitchenSetting
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int? KitchenWarehouseId { get; set; }
		public int? PoolAccountId { get; set; }
		public int? PatientMealsAccountId { get; set; }
		public int? StaffMealsAccountId { get; set; }
		public int? CafeteriaCogsAccountId { get; set; }
		public DateTime UpdatedAt { get; set; }
	}

	/// The recipe a diet's tray is built on.
	public class HospitalDietRecipe
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Diet { get; set; } = HospitalDiet.Regular;
		public int RecipeItemId { get; set; }
	}

	/// Who eats from the kitchen besides the trays: a staff group or a cafeteria outlet — or a ward, to name its cost centre.
	public class HospitalKitchenDestination
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string Stream { get; set; } = HospitalKitchenStream.Staff;
		/// For a patient destination: the ward whose trays it is.
		public int? WardId { get; set; }
		/// The standard meal of a staff group or outlet.
		public int? RecipeItemId { get; set; }
		public int? CostCenterId { get; set; }
		/// A cafeteria outlet's point-of-sale branch (its sales and payments).
		public int? PosBranchId { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
	}

	/// Meals the kitchen served a staff group or outlet in one round of one day.
	public class HospitalKitchenCount
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int DestinationId { get; set; }
		public DateTime Day { get; set; }
		public string Round { get; set; } = HospitalMealRound.Lunch;
		public int Meals { get; set; }
		public int? RecordedByEmployeeId { get; set; }
		public DateTime RecordedAt { get; set; }
	}

	/// One month's allocation, posted once.
	public class HospitalKitchenAllocation
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		/// The first day of the month allocated.
		public DateTime Month { get; set; }
		public decimal PoolTotal { get; set; }
		public int Meals { get; set; }
		public int? JournalEntryId { get; set; }
		public DateTime PostedAt { get; set; }
		public int? PostedByEmployeeId { get; set; }
	}

	public class HospitalKitchenAllocationLine
	{
		public int ID { get; set; }
		public int AllocationId { get; set; }
		public string Stream { get; set; } = HospitalKitchenStream.Patient;
		public int? WardId { get; set; }
		public int? DestinationId { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int Meals { get; set; }
		public decimal StandardCost { get; set; }
		public decimal Allocated { get; set; }
		public int? CostCenterId { get; set; }
	}

	public static class HospitalKitchenModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalKitchenSetting>(e =>
			{
				e.ToTable("HospitalKitchenSettings");   // no DbSet: each names its table
				e.HasIndex(x => x.CompanyId).IsUnique();
			});
			b.Entity<HospitalDietRecipe>(e =>
			{
				e.ToTable("HospitalDietRecipes");
				e.Property(x => x.Diet).HasMaxLength(20);
				e.HasIndex(x => new { x.CompanyId, x.Diet }).IsUnique();
			});
			b.Entity<HospitalKitchenDestination>(e =>
			{
				e.ToTable("HospitalKitchenDestinations");
				e.Property(x => x.Name).HasMaxLength(150);
				e.Property(x => x.NameEn).HasMaxLength(150);
				e.Property(x => x.Stream).HasMaxLength(20);
			});
			b.Entity<HospitalKitchenCount>(e =>
			{
				e.ToTable("HospitalKitchenCounts");
				e.Property(x => x.Day).HasColumnType("date");
				e.Property(x => x.Round).HasMaxLength(10);
				e.HasIndex(x => new { x.DestinationId, x.Day, x.Round }).IsUnique();
			});
			b.Entity<HospitalKitchenAllocation>(e =>
			{
				e.ToTable("HospitalKitchenAllocations");
				e.Property(x => x.Month).HasColumnType("date");
				e.Property(x => x.PoolTotal).HasPrecision(18, 3);
				e.HasIndex(x => new { x.CompanyId, x.Month }).IsUnique();
			});
			b.Entity<HospitalKitchenAllocationLine>(e =>
			{
				e.ToTable("HospitalKitchenAllocationLines");
				e.Property(x => x.Stream).HasMaxLength(20);
				e.Property(x => x.Name).HasMaxLength(150);
				e.Property(x => x.NameEn).HasMaxLength(150);
				e.Property(x => x.StandardCost).HasPrecision(18, 3);
				e.Property(x => x.Allocated).HasPrecision(18, 3);
			});
		}
	}
}
