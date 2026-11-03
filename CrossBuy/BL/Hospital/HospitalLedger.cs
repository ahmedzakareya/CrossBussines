using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Hospital
{
	/// One balanced posting the hospital asks the general ledger to make.
	public sealed record HospitalLedgerLine(int AccountId, decimal Debit, decimal Credit, string? Memo = null);

	/// The hospital's only door into the general ledger. Production posts through the accounting
	/// module's JournalEntryService (the ONE writer of the GL: period checks, balance, numbering);
	/// tests swap in a recorder so every posting can be asserted line by line.
	public interface IHospitalLedger
	{
		Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn,
			string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default);
	}

	public sealed class HospitalJournalLedger : IHospitalLedger
	{
		private readonly IJournalEntryService _journals;
		public HospitalJournalLedger(IJournalEntryService journals) { _journals = journals; }

		public async Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn,
			string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
		{
			// NoTx: the hospital service already owns the transaction, so the bill and its entry commit together.
			var (ok, error, entry) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput
			{
				CompanyID = ctx.CompanyId, EntryDate = date.Date, JournalType = "Auto", SourceType = sourceType, SourceId = sourceId,
				CurrencyId = 0, Description = description, DescriptionEn = descriptionEn,
				Lines = lines.Select(l => new JournalLineInput { AccountId = l.AccountId, Debit = l.Debit, Credit = l.Credit, Description = l.Memo, DescriptionEn = l.Memo }).ToList(),
			}, ctx.EmployeeId);
			return (ok, error, entry?.ID);
		}
	}
}
