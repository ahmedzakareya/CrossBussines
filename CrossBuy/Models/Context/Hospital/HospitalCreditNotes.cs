using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H148 — a CREDIT NOTE on a closed bill. A closed bill is frozen: its lines and its posted revenue never
	// change. What the patient no longer owes after closing (today: medicine brought back to the pharmacy, H146) is a
	// document of its own:
	//   · its lines name the bill lines they credit, how much of each, at the bill line's own price and VAT rate;
	//   · its ledger entry reverses that revenue and that VAT against the patients' receivable;
	//   · the bill's amount due falls by its total (like a charity waiver); a bill already paid leaves an excess, which
	//     goes to the patient's deposit balance exactly as on an open bill (H146).
	// One credit note per return. A bill line can never be credited past its quantity.
	// CrossBuy/deploy/sql/hospital_h148_credit_notes.sql builds it.
	// ==========================================================================================

	public static class HospitalCreditNoteSource
	{
		public const string DispenseReturn = "HospitalDispenseReturn";
		/// H184: dental work marked done by mistake, taken back (the source is the HospitalDentalCorrections row).
		public const string DentalCorrection = "HospitalDentalCorrection";
	}

	public class HospitalCreditNote
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string NoteNo { get; set; } = "";
		public int BillId { get; set; }
		public int PatientId { get; set; }
		/// What it is for: the source document (a dispense return today) and its id.
		public string SourceType { get; set; } = HospitalCreditNoteSource.DispenseReturn;
		public int SourceId { get; set; }
		public decimal Net { get; set; }
		public decimal Vat { get; set; }
		/// Net + VAT: what the patient no longer owes.
		public decimal Total { get; set; }
		public string Reason { get; set; } = "";
		public int? JournalEntryId { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public class HospitalCreditNoteLine
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int CreditNoteId { get; set; }
		public int BillLineId { get; set; }
		public decimal Quantity { get; set; }
		public decimal UnitPrice { get; set; }
		public decimal Net { get; set; }
		public decimal Vat { get; set; }
		/// The revenue account the bill line posted to (its own, or the billing default at the time).
		public int RevenueAccountId { get; set; }
	}

	public static class HospitalCreditNoteModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalCreditNote>(e =>
			{
				e.ToTable("HospitalCreditNotes");   // no DbSet: it names its table
				e.Property(x => x.NoteNo).HasMaxLength(20);
				e.Property(x => x.SourceType).HasMaxLength(40);
				e.Property(x => x.Reason).HasMaxLength(300);
				foreach (var p in new[] { nameof(HospitalCreditNote.Net), nameof(HospitalCreditNote.Vat), nameof(HospitalCreditNote.Total) }) e.Property(p).HasPrecision(18, 4);
				e.HasIndex(x => new { x.CompanyId, x.NoteNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.BillId });
			});
			b.Entity<HospitalCreditNoteLine>(e =>
			{
				e.ToTable("HospitalCreditNoteLines");
				foreach (var p in new[] { nameof(HospitalCreditNoteLine.Quantity), nameof(HospitalCreditNoteLine.UnitPrice), nameof(HospitalCreditNoteLine.Net), nameof(HospitalCreditNoteLine.Vat) }) e.Property(p).HasPrecision(18, 4);
				e.HasIndex(x => new { x.CompanyId, x.CreditNoteId });
				e.HasIndex(x => new { x.CompanyId, x.BillLineId });
			});
		}
	}
}
