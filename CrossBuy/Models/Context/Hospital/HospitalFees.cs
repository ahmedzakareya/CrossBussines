using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — doctors' fees (unit H19).
	//
	// A practitioner's FEE RULES say what he earns on the work he does: a percent of what was collected, or a fixed
	// amount per unit (a visit), for one service or a whole category (the service's rule wins). His monthly FEE
	// STATEMENT is calculated on what was actually COLLECTED in the month — the patient's part when the patient
	// has paid the bill in full, the payer's part when its claim line is accepted (H11) — so an uncollected
	// service moves to the month it is collected. Each part of a bill line is counted once, ever.
	// A statement is generated, approved by the medical director, then by finance: a doctor paid as a vendor gets
	// the journal (Dr accrued doctor fees, Cr his payable) at finance approval; one paid through payroll is marked
	// for payroll (the payroll's variable lines are HR's).
	// CrossBuy/deploy/sql/hospital_h19_doctor_fees.sql builds the tables (prepared, not applied).
	// ==========================================================================================

	public static class HospitalFeeBasis
	{
		public const string Percent = "Percent";
		public const string PerUnit = "PerUnit";
		public static readonly IReadOnlyList<string> All = new[] { Percent, PerUnit };
	}

	public static class HospitalFeeStatus
	{
		public const string Generated = "Generated";
		public const string DirectorApproved = "DirectorApproved";
		public const string FinanceApproved = "FinanceApproved";
		public static readonly IReadOnlyList<string> All = new[] { Generated, DirectorApproved, FinanceApproved };
	}

	/// The part of a bill line a statement line counts.
	public static class HospitalFeePart
	{
		public const string Patient = "Patient";
		public const string Payer = "Payer";
		/// H82: the payer took back what it had paid on a line whose payer part was already counted — a negative line.
		public const string Recoup = "Recoup";
	}

	public class HospitalFeeRule
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PractitionerId { get; set; }
		/// A service category (HospitalServiceCategories)…
		public string Category { get; set; } = HospitalServiceCategories.Consultation;
		/// …or, when set, one service of it (wins over the category's rule).
		public int? ServiceId { get; set; }
		public string Basis { get; set; } = HospitalFeeBasis.Percent;
		/// The percent (Percent) or the amount per unit in the facility's currency (PerUnit).
		public decimal Value { get; set; }
	}

	/// Where an approved statement posts (one row per company).
	public class HospitalFeeSettings
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int? ExpenseAccountId { get; set; }
		public int? PayableAccountId { get; set; }
	}

	public class HospitalFeeStatement
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string StatementNo { get; set; } = "";
		public int PractitionerId { get; set; }
		/// "2026-10".
		public string Period { get; set; } = "";
		public string Status { get; set; } = HospitalFeeStatus.Generated;
		public decimal Billed { get; set; }
		public decimal Collected { get; set; }
		public decimal Share { get; set; }
		public DateTime GeneratedAt { get; set; }
		public int? GeneratedByEmployeeId { get; set; }
		public DateTime? DirectorApprovedAt { get; set; }
		public int? DirectorEmployeeId { get; set; }
		public DateTime? FinanceApprovedAt { get; set; }
		public int? FinanceEmployeeId { get; set; }
		public int? JournalEntryId { get; set; }
	}

	public class HospitalFeeStatementLine
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int StatementId { get; set; }
		public int BillLineId { get; set; }
		/// HospitalFeePart: the patient's part of the line, or the payer's.
		public string Part { get; set; } = HospitalFeePart.Patient;
		public int PatientId { get; set; }
		public int? PayerId { get; set; }
		public DateTime CollectedOn { get; set; }
		public string Description { get; set; } = "";
		public string DescriptionEn { get; set; } = "";
		public decimal Billed { get; set; }
		public decimal Collected { get; set; }
		public string Basis { get; set; } = HospitalFeeBasis.Percent;
		public decimal Rate { get; set; }
		public decimal Share { get; set; }
	}

	public static class HospitalFeesModel
	{
		public static void Configure(ModelBuilder b)
		{
			// no DbSet for any of them: each names its table, or EF would look for the singular name
			b.Entity<HospitalFeeRule>(e =>
			{
				e.ToTable("HospitalFeeRules");
				e.Property(x => x.Value).HasPrecision(18, 3);
				e.HasIndex(x => new { x.CompanyId, x.PractitionerId });
			});
			b.Entity<HospitalFeeSettings>(e =>
			{
				e.ToTable("HospitalFeeSettings");
				e.HasIndex(x => x.CompanyId).IsUnique();
			});
			b.Entity<HospitalFeeStatement>(e =>
			{
				e.ToTable("HospitalFeeStatements");
				foreach (var p in new[] { nameof(HospitalFeeStatement.Billed), nameof(HospitalFeeStatement.Collected), nameof(HospitalFeeStatement.Share) })
					e.Property<decimal>(p).HasPrecision(18, 3);
				e.HasIndex(x => new { x.CompanyId, x.StatementNo }).IsUnique();
				// one statement per practitioner and month
				e.HasIndex(x => new { x.CompanyId, x.PractitionerId, x.Period }).IsUnique();
			});
			b.Entity<HospitalFeeStatementLine>(e =>
			{
				e.ToTable("HospitalFeeStatementLines");
				foreach (var p in new[] { nameof(HospitalFeeStatementLine.Billed), nameof(HospitalFeeStatementLine.Collected), nameof(HospitalFeeStatementLine.Rate), nameof(HospitalFeeStatementLine.Share) })
					e.Property<decimal>(p).HasPrecision(18, 3);
				e.Property(x => x.CollectedOn).HasColumnType("date");
				// each part of a bill line is counted once, ever
				e.HasIndex(x => new { x.BillLineId, x.Part }).IsUnique();
				e.HasIndex(x => x.StatementId);
			});
		}
	}
}
