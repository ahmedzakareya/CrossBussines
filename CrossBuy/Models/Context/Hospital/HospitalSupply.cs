using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — ward requisitions to stores (unit H32), as the approved prototype draws them (#/requisitions).
	//
	//   · A SUPPLY UNIT is a place that keeps stock and asks for it — a ward, the ICU, the theatre, the ER: its own
	//     store (an inventory warehouse) and the store it draws from (the main store or the pharmacy).
	//   · A PAR is how much of an item the unit keeps; the suggested quantity of a requisition line is
	//     par − what the unit's store holds now (the prototype: "suggested quantity = ward par − ward balance").
	//   · A REQUISITION moves Draft → Submitted → Approved → Picking → Dispatched (or is cancelled before it is
	//     dispatched). The store approves each line's quantity; Dispatched POSTS the inventory transfer from the
	//     source store to the unit's store (StockService.TransferAsync, the inventory module's own transfer — the
	//     prototype's "store transfers with approval"), and keeps its id.
	// CrossBuy/deploy/sql/hospital_h32_supply.sql builds the tables.
	// ==========================================================================================

	public static class HospitalRequisitionStatus
	{
		public const string Draft = "Draft";
		public const string Submitted = "Submitted";
		public const string Approved = "Approved";
		public const string Picking = "Picking";
		public const string Dispatched = "Dispatched";
		public const string Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Draft, Submitted, Approved, Picking, Dispatched, Cancelled };
	}

	/// H36: a person's duty on a unit — a SETTING (owner ruling), not a role fixed in code.
	public static class HospitalSupplyDuty
	{
		public const string Request = "Request";
		public const string Approve = "Approve";
		public static readonly IReadOnlyList<string> All = new[] { Request, Approve };
	}

	public static class HospitalRequisitionPriority
	{
		public const string Routine = "Routine";
		public const string Urgent = "Urgent";
		public const string Stat = "Stat";
		public static readonly IReadOnlyList<string> All = new[] { Routine, Urgent, Stat };
	}

	public class HospitalSupplyUnit
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public int? WardId { get; set; }
		/// The unit's own store (an inventory warehouse).
		public int StoreWarehouseId { get; set; }
		/// The store it draws from.
		public int SourceWarehouseId { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public class HospitalSupplyPar
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int UnitId { get; set; }
		public int ItemId { get; set; }
		public decimal ParQty { get; set; }
	}

	public class HospitalRequisition
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string ReqNo { get; set; } = "";
		public int UnitId { get; set; }
		public string Priority { get; set; } = HospitalRequisitionPriority.Routine;
		public string Status { get; set; } = HospitalRequisitionStatus.Draft;
		public string? Note { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? RequestedByEmployeeId { get; set; }
		public DateTime? SubmittedAt { get; set; }
		public DateTime? ApprovedAt { get; set; }
		public int? ApprovedByEmployeeId { get; set; }
		public DateTime? DispatchedAt { get; set; }
		public int? DispatchedByEmployeeId { get; set; }
		/// The inventory transfer Dispatched posted.
		public int? StockTransferId { get; set; }
		public string? CancelReason { get; set; }
	}

	public class HospitalRequisitionLine
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int RequisitionId { get; set; }
		public int ItemId { get; set; }
		public decimal RequestedQty { get; set; }
		/// What the store gives (null until approved; 0 = not given).
		public decimal? ApprovedQty { get; set; }
	}

	/// H36: one person's duty on one unit (who asks for it, who approves and dispatches for it).
	public class HospitalSupplyUnitPerson
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int UnitId { get; set; }
		public int EmployeeId { get; set; }
		public string Duty { get; set; } = HospitalSupplyDuty.Request;
		public DateTime CreatedAt { get; set; }
	}

	public static class HospitalSupplyModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalSupplyUnit>(e =>
			{
				e.ToTable("HospitalSupplyUnits");   // no DbSet: each names its table
				e.Property(x => x.Code).HasMaxLength(20);
				e.Property(x => x.Name).HasMaxLength(100);
				e.Property(x => x.NameEn).HasMaxLength(100);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalSupplyPar>(e =>
			{
				e.ToTable("HospitalSupplyPars");
				e.Property(x => x.ParQty).HasPrecision(18, 3);
				e.HasIndex(x => new { x.UnitId, x.ItemId }).IsUnique();
			});
			b.Entity<HospitalRequisition>(e =>
			{
				e.ToTable("HospitalRequisitions");
				e.Property(x => x.ReqNo).HasMaxLength(30);
				e.Property(x => x.Priority).HasMaxLength(20);
				e.Property(x => x.Status).HasMaxLength(20);
				e.Property(x => x.Note).HasMaxLength(500);
				e.Property(x => x.CancelReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.ReqNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status });
			});
			b.Entity<HospitalRequisitionLine>(e =>
			{
				e.ToTable("HospitalRequisitionLines");
				e.Property(x => x.RequestedQty).HasPrecision(18, 3);
				e.Property(x => x.ApprovedQty).HasPrecision(18, 3);
				e.HasIndex(x => new { x.RequisitionId, x.ItemId }).IsUnique();   // an item once per requisition
			});
			b.Entity<HospitalSupplyUnitPerson>(e =>
			{
				e.ToTable("HospitalSupplyUnitPeople");
				e.Property(x => x.Duty).HasMaxLength(10);
				e.HasIndex(x => new { x.CompanyId, x.UnitId, x.EmployeeId, x.Duty }).IsUnique();
			});
		}
	}
}
