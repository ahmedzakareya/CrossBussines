using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL ESTIMATES & PACKAGES (unit H12).
	//
	// A PACKAGE is a procedure sold at a fixed price: its main service (the one a payer approves), the nights
	// it includes in a room class, and what it covers in words. Anything beyond its limits — extra nights,
	// complications, a second implant — is billed outside it.
	// An ESTIMATE prices a planned admission for one patient before it happens: a package or a list of
	// services, the room class and the expected nights; it is split between the payer and the patient by the
	// patient's cover (H11), and states the deposit to ask. Issuing it sends the payer an approval request
	// when the contract needs one. An estimate moves no money: collecting the deposit is billing's.
	// CrossBuy/deploy/sql/hospital_h12_estimates.sql builds the tables (applied to CrossBuyDB2 2026-11-03).
	// ==========================================================================================

	public static class HospitalEstimateStatus
	{
		public const string Draft = "Draft";
		public const string Issued = "Issued";
		public const string Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Draft, Issued, Cancelled };
	}

	public class HospitalPackage
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		/// The procedure the package sells; the payer's approval is asked for this service.
		public int ServiceId { get; set; }
		/// The fixed price, in the facility's currency.
		public decimal Price { get; set; }
		/// The nights the price includes; more are billed outside at the room class's daily rate.
		public int IncludedNights { get; set; }
		/// The room class the price assumes (null = any).
		public int? RoomTypeId { get; set; }
		/// What the package covers and its limits, in both languages.
		public string? Inclusions { get; set; }
		public string? InclusionsEn { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public class HospitalEstimate
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string EstimateNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? PackageId { get; set; }
		public int? RoomTypeId { get; set; }
		public int ExpectedNights { get; set; }
		/// The cover the estimate was priced with (null = self-pay).
		public int? PolicyId { get; set; }
		public int? PayerId { get; set; }
		public decimal Gross { get; set; }
		public decimal PayerShare { get; set; }
		public decimal PatientShare { get; set; }
		/// The margin added to the patient's expected share to make the deposit, in percent.
		public decimal MarginPct { get; set; }
		public decimal Deposit { get; set; }
		public string Status { get; set; } = HospitalEstimateStatus.Draft;
		public string? Note { get; set; }
		/// The approval request issuing sent to the payer, when its contract asks for one.
		public int? PreauthId { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? IssuedAt { get; set; }
		public int? IssuedByEmployeeId { get; set; }
	}

	public class HospitalEstimateLine
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int EstimateId { get; set; }
		/// package | stay | service
		public string Kind { get; set; } = "";
		public int? ServiceId { get; set; }
		public string Code { get; set; } = "";
		public string Category { get; set; } = "";
		public string Description { get; set; } = "";
		public string DescriptionEn { get; set; } = "";
		public decimal Quantity { get; set; }
		public decimal UnitPrice { get; set; }
		public decimal Amount { get; set; }
		public bool Covered { get; set; }
		public decimal PayerAmount { get; set; }
	}

	public static class HospitalEstimatesModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPackage>(e =>
			{
				e.ToTable("HospitalPackages");   // no DbSet: without this EF would look for dbo.HospitalPackage
				e.Property(x => x.Price).HasPrecision(18, 4);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalEstimate>(e =>
			{
				e.ToTable("HospitalEstimates");   // no DbSet: without this EF would look for dbo.HospitalEstimate
				foreach (var p in new[] { nameof(HospitalEstimate.Gross), nameof(HospitalEstimate.PayerShare), nameof(HospitalEstimate.PatientShare), nameof(HospitalEstimate.Deposit) }) e.Property(p).HasPrecision(18, 4);
				e.Property(x => x.MarginPct).HasPrecision(9, 4);
				e.HasOne<HospitalPackage>().WithMany().HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.CompanyId, x.EstimateNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalEstimateLine>(e =>
			{
				e.ToTable("HospitalEstimateLines");   // no DbSet: without this EF would look for dbo.HospitalEstimateLine
				foreach (var p in new[] { nameof(HospitalEstimateLine.Quantity), nameof(HospitalEstimateLine.UnitPrice), nameof(HospitalEstimateLine.Amount), nameof(HospitalEstimateLine.PayerAmount) }) e.Property(p).HasPrecision(18, 4);
				e.HasOne<HospitalEstimate>().WithMany().HasForeignKey(x => x.EstimateId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.CompanyId, x.EstimateId });
			});
		}
	}
}
