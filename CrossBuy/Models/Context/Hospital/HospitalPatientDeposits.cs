using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H143 — the patient's deposit: money taken at the counter before there is a bill to put it on (a procedure,
	// an admission, or simply ahead of a visit). It is the hospital's debt to the patient until it is used or refunded:
	//   · TAKEN into the cashier's open shift (cash or card…): debit cash or bank, credit the patient-deposits account;
	//   · USED on a bill: a payment on that bill of method "Deposit" (no money moves): debit the patient-deposits
	//     account, credit patient receivables;
	//   · REFUNDED from the cashier's open shift: debit the patient-deposits account, credit cash or bank.
	// What is left = Amount − Used − Refunded, never below zero. The shift counts deposits in and refunds out, so a
	// shift closes on what is really in the drawer. The patient-deposits account is the owner's to map in billing
	// settings (PatientDepositAccountId); unmapped, a deposit is refused.
	// CrossBuy/deploy/sql/hospital_h143_deposits.sql builds the two tables and the settings column.
	// ==========================================================================================

	public static class HospitalDepositPurpose
	{
		public const string General = "General";
		public const string Procedure = "Procedure";
		public const string Admission = "Admission";
		public static readonly IReadOnlyList<string> All = new[] { General, Procedure, Admission };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	public static class HospitalDepositMoveKind
	{
		public const string Use = "Use";
		public const string Refund = "Refund";
	}

	public class HospitalPatientDeposit
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string DepositNo { get; set; } = "";
		public int PatientId { get; set; }
		public string Purpose { get; set; } = HospitalDepositPurpose.General;
		public string? Note { get; set; }
		public decimal Amount { get; set; }
		public string Method { get; set; } = HospitalPaymentMethods.Cash;
		public int ShiftId { get; set; }
		public int CashierEmployeeId { get; set; }
		public DateTime ReceivedAt { get; set; }
		public int? JournalEntryId { get; set; }
		public decimal Used { get; set; }
		public decimal Refunded { get; set; }
	}

	/// One use of a deposit on a bill, or one refund of it.
	public class HospitalDepositMove
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int DepositId { get; set; }
		public string Kind { get; set; } = HospitalDepositMoveKind.Use;
		public decimal Amount { get; set; }
		public int? BillId { get; set; }
		public int? PaymentId { get; set; }
		public string? Method { get; set; }
		public int? ShiftId { get; set; }
		public string? Reason { get; set; }
		public DateTime At { get; set; }
		public int ByEmployeeId { get; set; }
		public int? JournalEntryId { get; set; }
	}

	public static class HospitalPatientDepositModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPatientDeposit>(e =>
			{
				e.ToTable("HospitalPatientDeposits");   // no DbSet: it names its table
				e.Property(x => x.DepositNo).HasMaxLength(20);
				e.Property(x => x.Purpose).HasMaxLength(20);
				e.Property(x => x.Note).HasMaxLength(300);
				e.Property(x => x.Method).HasMaxLength(20);
				foreach (var p in new[] { nameof(HospitalPatientDeposit.Amount), nameof(HospitalPatientDeposit.Used), nameof(HospitalPatientDeposit.Refunded) }) e.Property(p).HasPrecision(18, 4);
				e.HasIndex(x => new { x.CompanyId, x.DepositNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
				e.HasIndex(x => new { x.CompanyId, x.ShiftId });
			});
			b.Entity<HospitalDepositMove>(e =>
			{
				e.ToTable("HospitalDepositMoves");
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Method).HasMaxLength(20);
				e.Property(x => x.Reason).HasMaxLength(300);
				e.Property(x => x.Amount).HasPrecision(18, 4);
				e.HasIndex(x => new { x.CompanyId, x.DepositId });
				e.HasIndex(x => new { x.CompanyId, x.ShiftId });
			});
		}
	}
}
