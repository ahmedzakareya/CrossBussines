using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — a bill's insurance split, frozen when the bill closes (unit H18).
	//
	// An open bill shows its split live, from the patient's cover today (HospitalCoverage). Closing it posts the
	// journal on that split — the patient's receivable, the payer's receivable, revenue net of the contract's
	// discount — and stores it here, so the claim later asks the payer for exactly what was posted, whatever
	// happens to the contract's terms afterwards. A bill closed with no row here was self-pay (or closed before
	// H18): the patient owed the whole of it, as it was posted.
	// CrossBuy/deploy/sql/hospital_h18_bill_coverage.sql builds the tables (prepared, not applied).
	// ==========================================================================================

	public class HospitalBillCoverage
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int BillId { get; set; }
		public int PayerId { get; set; }
		public int PolicyId { get; set; }
		public decimal CopayPct { get; set; }
		public decimal CopayCap { get; set; }
		public decimal DiscountPct { get; set; }
		/// Net of the contract's discount.
		public decimal Amount { get; set; }
		public decimal Covered { get; set; }
		public decimal Copay { get; set; }
		public decimal InsurerShare { get; set; }
		public decimal PatientShare { get; set; }
		public DateTime FrozenAt { get; set; }
	}

	public class HospitalBillCoverageLine
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int CoverageId { get; set; }
		public int BillLineId { get; set; }
		/// The line's worth to the split: its price less the contract's discount.
		public decimal Amount { get; set; }
		public bool Covered { get; set; }
		public decimal InsurerAmount { get; set; }
	}

	public static class HospitalBillCoverageModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalBillCoverage>(e =>
			{
				e.ToTable("HospitalBillCoverages");   // no DbSet: without this EF would look for dbo.HospitalBillCoverage
				foreach (var p in new[] { nameof(HospitalBillCoverage.Amount), nameof(HospitalBillCoverage.Covered), nameof(HospitalBillCoverage.Copay),
					nameof(HospitalBillCoverage.InsurerShare), nameof(HospitalBillCoverage.PatientShare), nameof(HospitalBillCoverage.CopayCap) })
					e.Property<decimal>(p).HasPrecision(18, 3);
				e.Property(x => x.CopayPct).HasPrecision(5, 2);
				e.Property(x => x.DiscountPct).HasPrecision(5, 2);
				e.HasIndex(x => x.BillId).IsUnique();   // a bill is split once
			});
			b.Entity<HospitalBillCoverageLine>(e =>
			{
				e.ToTable("HospitalBillCoverageLines");
				e.Property(x => x.Amount).HasPrecision(18, 3);
				e.Property(x => x.InsurerAmount).HasPrecision(18, 3);
				e.HasIndex(x => x.BillLineId).IsUnique();
				e.HasIndex(x => x.CoverageId);
			});
		}
	}
}
