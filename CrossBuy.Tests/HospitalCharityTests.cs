using CrossBuy.BL.Hospital;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H63 — charity cases and the patient statement, with no database: a waiver only on a closed, local-currency
	// bill and never above what is left after other requests; the requester never decides; an approval re-checks the
	// balance; the bill's amount due nets the waiver; the tables against their slice; the statement in Report Studio.
	// ==========================================================================================
	public class HospitalCharityTests
	{
		private static HospitalCharityRequestInput Ask(decimal amount = 10, string? reason = "Unemployed, three children") => new() { FundId = 1, Amount = amount, Reason = reason };
		private static string? Refusal(HospitalCharityRequestInput i, string status = HospitalBillStatus.Closed, bool foreign = false, decimal due = 30, decimal pending = 0, bool fund = true)
			=> HospitalCharityRules.RequestRefusal(i, status, foreign, due, pending, fund);

		[Fact]
		public void A_waiver_is_asked_on_a_closed_local_bill_within_what_is_left()
		{
			Assert.Null(Refusal(Ask()));
			Assert.Equal("Charity is granted on a closed bill", Refusal(Ask(), status: HospitalBillStatus.Open));
			Assert.Equal("A foreign-currency bill is not waived here", Refusal(Ask(), foreign: true));
			Assert.Equal("Choose the charity fund", Refusal(Ask(), fund: false));
			Assert.Equal("Enter an amount above zero", Refusal(Ask(0)));
			Assert.Equal("Write why the patient needs the help", Refusal(Ask(reason: " ")));
			Assert.Equal("The waiver is more than the patient still owes ({0})", Refusal(Ask(31)));
			// another request already asks for 25 of the 30: only 5 is left to ask for
			Assert.Equal("The waiver is more than the patient still owes ({0})", Refusal(Ask(10), pending: 25));
			Assert.Null(Refusal(Ask(5), pending: 25));
		}

		[Fact]
		public void The_requester_never_decides_and_an_approval_rechecks_the_balance()
		{
			Assert.Null(HospitalCharityRules.DecideRefusal(HospitalCharityStatus.Requested, 7, 9, true, 10, 30));
			Assert.Equal("The person who asked cannot approve their own request", HospitalCharityRules.DecideRefusal(HospitalCharityStatus.Requested, 7, 7, false, 10, 30));
			Assert.Equal("This request was already decided", HospitalCharityRules.DecideRefusal(HospitalCharityStatus.Approved, 7, 9, true, 10, 30));
			// the patient paid in the meantime and owes only 4 now: approving 10 would over-credit him
			Assert.Equal("The patient now owes less than the waiver ({0}): refuse it and ask again", HospitalCharityRules.DecideRefusal(HospitalCharityStatus.Requested, 7, 9, true, 10, 4));
			Assert.Null(HospitalCharityRules.DecideRefusal(HospitalCharityStatus.Requested, 7, 9, false, 10, 4));   // a refusal always may
		}

		[Fact]
		public void The_amount_due_nets_the_waiver()
		{
			var t = new HospitalBillTotals(Gross: 100, Insurer: 70, Patient: 28, Vat: 2, Paid: 10, Waived: 15);
			Assert.Equal(5m, t.Due);
			Assert.Equal(20m, new HospitalBillTotals(100, 70, 28, 2, 10).Due);   // no charity: as before
		}

		[Fact]
		public void A_fund_has_its_names_and_an_expense_account()
		{
			Assert.Null(HospitalCharityRules.FundRefusal(new HospitalCharityFundInput { Code = "ZAKAT", Name = "الزكاة", NameEn = "Zakat", AccountId = 5 }));
			Assert.Equal("Choose the fund's expense account", HospitalCharityRules.FundRefusal(new HospitalCharityFundInput { Code = "ZAKAT", Name = "الزكاة", NameEn = "Zakat" }));
			Assert.Null(HospitalCharityRules.FundRefusal(new HospitalCharityFundInput { Code = "OLD", Name = "قديم", NameEn = "Old", IsActive = false }));   // an inactive fund may lose its account
			Assert.Equal("Give the fund a code of up to 20 characters", HospitalCharityRules.FundRefusal(new HospitalCharityFundInput { Code = new string('A', 21), Name = "x", NameEn = "x", AccountId = 1 }));
		}

		[Fact]
		public void The_tables_match_their_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h63_charity.sql");
			var m = HospitalSliceParity.Model(HospitalCharityModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalCharityFund), "HospitalCharityFunds");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalCharityFund), "HospitalCharityFunds");
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalCharityGrant), "HospitalCharityGrants");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalCharityGrant), "HospitalCharityGrants");
			foreach (var st in HospitalCharityStatus.All) Assert.Contains("'" + st + "'", s);
			Assert.Contains("DecidedByEmployeeId <> RequestedByEmployeeId", s);
			Assert.Contains("CHECK (Status <> 'Approved' OR JournalEntryId IS NOT NULL)", s);
			Assert.Equal(new[] { "HospitalBills", "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
		}

		[Fact]
		public void The_statement_prints_through_report_studio_and_grants_move_in_a_merge()
		{
			var d = HospitalDocumentDatasets.All().Single(x => x.DatasetCode == HospitalDocumentDatasetCodes.PatientStatement);
			Assert.Contains(d.Parameters, p => p.Key == "PatientId" && p.Required);
			foreach (var k in new[] { "EntryDate", "Kind", "Reference", "Debit", "Credit", "Balance", "ClosingBalance", "PatientName", "PatientMrn" }) Assert.Contains(d.Fields, f => f.Key == k);
			Assert.Contains(ReportScreenBindings.For(ReportScreenKeys.HospitalPatientStatement), b => b.ReportCode == HospitalDocumentDatasetCodes.PatientStatement && b.IdParameter == "PatientId");
			Assert.Contains(HospitalDocumentDatasets.All().Single(x => x.DatasetCode == HospitalDocumentDatasetCodes.Bill).Fields, f => f.Key == "Waived");
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalCharityGrant));
		}
	}
}
