using System.Text;
using System.Text.Json;
using CrossBuy.BL.Hr;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Hr
{
	// ============================================================================================
	// HR RULES — the pure decisions the HR programme rests on (2026-11-09), pinned without a database:
	//   * HrLeavePay.PaidPct          — which pay tier the n-th day of a tiered leave falls in (Kuwait Art. 69 sick leave);
	//   * HrApprovalPathService.Shape — how a designed path shapes the org-tree chain;
	//   * HrApprovalHandover.Route*   — moving a pending step on (leaver handover, SLA escalation);
	//   * HrBankFileService           — the IBAN check and the bank salary file's every format setting;
	//   * HrBankDetailsImport         — normalising and masking what an Excel import brings in.
	// Each was verified once against CrossBuyDB2 by hand; these keep them true when the code moves.
	// ============================================================================================
	public class HrRulesLogicTests
	{
		// ---- tiered leave pay ------------------------------------------------------------------------------
		private static readonly List<HrPayTier> KuwaitSick = new()
		{
			new() { Days = 15, PaidPct = 100 }, new() { Days = 10, PaidPct = 75 }, new() { Days = 10, PaidPct = 50 },
			new() { Days = 10, PaidPct = 25 }, new() { Days = 30, PaidPct = 0 },
		};

		[Theory]
		[InlineData(1, 100)] [InlineData(15, 100)] [InlineData(16, 75)] [InlineData(25, 75)] [InlineData(26, 50)]
		[InlineData(35, 50)] [InlineData(36, 25)] [InlineData(45, 25)] [InlineData(46, 0)] [InlineData(75, 0)] [InlineData(76, 0)]
		public void Sick_day_takes_the_tier_its_position_in_the_year_gives_it(int day, int expectedPct) =>
			Assert.Equal(expectedPct, HrLeavePay.PaidPct(KuwaitSick, day));

		[Fact]
		public void No_tiers_means_unpaid_beyond_nothing() => Assert.Equal(0m, HrLeavePay.PaidPct(new List<HrPayTier>(), 1));

		// ---- approval path shaping ---------------------------------------------------------------------------
		private static HrApprovalPath Path(int levels = 0, int directDays = 0, bool active = true) =>
			new() { Kind = "Leave", ManagerLevels = levels, DirectOnlyUpToDays = directDays, IsActive = active };

		private static readonly int[] Chain = { 23, 19, 7 };

		[Fact] public void No_path_leaves_the_chain_alone() => Assert.Equal(Chain, HrApprovalPathService.Shape(Chain, null, 10, 20, 2046));
		[Fact] public void Inactive_path_leaves_the_chain_alone() => Assert.Equal(Chain, HrApprovalPathService.Shape(Chain, Path(1, active: false), 10, 20, 2046));
		[Fact] public void Levels_keep_the_first_n_managers() => Assert.Equal(new[] { 23, 19 }, HrApprovalPathService.Shape(Chain, Path(2), 10, 20, null));
		[Fact] public void Zero_levels_means_every_manager() => Assert.Equal(Chain, HrApprovalPathService.Shape(Chain, Path(0), 10, 20, null));
		[Fact] public void Short_leave_needs_the_direct_manager_only() => Assert.Equal(new[] { 23 }, HrApprovalPathService.Shape(Chain, Path(0, 3), 3, 20, null));
		[Fact] public void Longer_leave_keeps_the_full_chain() => Assert.Equal(Chain, HrApprovalPathService.Shape(Chain, Path(0, 3), 4, 20, null));
		[Fact] public void Days_rule_ignored_when_the_request_has_no_days() => Assert.Equal(Chain, HrApprovalPathService.Shape(Chain, Path(0, 3), null, 20, null));
		[Fact] public void Hr_final_approver_is_appended() => Assert.Equal(new[] { 23, 19, 7, 2046 }, HrApprovalPathService.Shape(Chain, Path(), 10, 20, 2046));
		[Fact] public void Final_approver_already_in_the_chain_is_not_added_twice() => Assert.Equal(Chain, HrApprovalPathService.Shape(Chain, Path(), 10, 20, 19));
		[Fact] public void Requester_is_never_their_own_final_approver() => Assert.Empty(HrApprovalPathService.Shape(Array.Empty<int>(), Path(), 10, 2046, 2046));
		[Fact] public void Final_approver_after_the_short_leave_cut() => Assert.Equal(new[] { 23, 2046 }, HrApprovalPathService.Shape(Chain, Path(0, 3), 2, 20, 2046));

		// ---- moving a pending step on (handover / escalation) ----------------------------------------------------
		// Building CrossDbContext's model and calling Remove opens no connection, so the server name is never resolved.
		private static CrossDbContext Ctx() => new(new DbContextOptionsBuilder<CrossDbContext>().UseSqlServer("Server=hr-rules-logic-tests-no-db;Database=none").Options);

		private static LeaveRequest Leave(int requester, int currentLevel, params (int approver, int status)[] steps)
		{
			var r = new LeaveRequest { ID = 1, EmployeeID = requester, Status = 0, CurrentLevel = currentLevel };
			int i = 0;
			foreach (var (a, s) in steps) r.ApprovalSteps.Add(new LeaveApprovalStep { ID = 100 + i, LeaveRequestID = 1, Level = ++i, ApproverEmployeeID = a, Status = s });
			r.CurrentApproverEmployeeID = r.ApprovalSteps.FirstOrDefault(s => s.Level == currentLevel)?.ApproverEmployeeID;
			return r;
		}

		[Fact]
		public void Step_moves_to_a_manager_who_holds_no_step()
		{
			var req = Leave(20, 1, (23, 0));
			Assert.Equal(ApprovalRoute.Reassigned, HrApprovalHandover.RouteLeave(null!, req, req.ApprovalSteps.First(), 19));
			Assert.Equal(19, req.ApprovalSteps.Single().ApproverEmployeeID);
			Assert.Equal(19, req.CurrentApproverEmployeeID);
		}

		[Fact]
		public void Step_collapses_onto_a_later_pending_step_of_the_same_manager()
		{
			using var db = Ctx();
			var req = Leave(20, 1, (23, 0), (19, 0), (2046, 0));
			var mine = req.ApprovalSteps.First(s => s.ApproverEmployeeID == 23);
			Assert.Equal(ApprovalRoute.Collapsed, HrApprovalHandover.RouteLeave(db, req, mine, 19));
			Assert.Equal(EntityState.Deleted, db.Entry(mine).State);
			var left = req.ApprovalSteps.Where(s => s != mine).OrderBy(s => s.Level).Select(s => (s.Level, s.ApproverEmployeeID)).ToList();
			Assert.Equal(new[] { (1, 19), (2, 2046) }, left);
			Assert.Equal(1, req.CurrentLevel);
			Assert.Equal(19, req.CurrentApproverEmployeeID);
		}

		[Fact]
		public void A_manager_who_already_decided_is_never_handed_the_request_again()
		{
			var req = Leave(20, 2, (19, 1), (23, 0));
			Assert.Equal(ApprovalRoute.Unrouted, HrApprovalHandover.RouteLeave(null!, req, req.ApprovalSteps.Last(), 19));
			Assert.Equal(23, req.ApprovalSteps.Last().ApproverEmployeeID);
		}

		[Fact]
		public void Nobody_above_or_the_requester_leaves_the_step_unrouted()
		{
			var req = Leave(20, 1, (23, 0));
			Assert.Equal(ApprovalRoute.Unrouted, HrApprovalHandover.RouteLeave(null!, req, req.ApprovalSteps.First(), null));
			Assert.Equal(ApprovalRoute.Unrouted, HrApprovalHandover.RouteLeave(null!, req, req.ApprovalSteps.First(), 20));
			Assert.Equal(23, req.CurrentApproverEmployeeID);
		}

		// ---- IBAN ------------------------------------------------------------------------------------------------
		[Theory]
		[InlineData("GB82WEST12345698765432", true)]          // the ISO 13616 example
		[InlineData("gb82 west 1234 5698 7654 32", true)]     // spaces and case do not matter
		[InlineData("GB82WEST12345698765433", false)]         // one digit changed
		[InlineData("KW00NBOK0000000000001234567891", false)] // made-up check digits
		[InlineData("GB82", false)]
		[InlineData("", false)]
		[InlineData(null, false)]
		public void Iban_check_digits(string? iban, bool valid) => Assert.Equal(valid, HrBankFileService.LooksLikeIban(iban));

		// ---- the bank salary file ----------------------------------------------------------------------------------
		private static HrBankFileFormat Format(Action<HrBankFileFormat>? tweak = null, params (string field, string header, string? value)[] cols)
		{
			var f = new HrBankFileFormat
			{
				Delimiter = ",", WithBom = false, HeaderRow = true, DateFormat = "dd/MM/yyyy", DecimalPlaces = 3, TrailerRow = "None",
				FileNamePattern = "SALARIES_{yyyyMM}.csv",
				ColumnsJson = JsonSerializer.Serialize((cols.Length > 0 ? cols : new[] { ("Seq", "SEQ", (string?)null), ("Iban", "IBAN", null), ("BankName", "BANK", null), ("Net", "AMOUNT", null), ("PaymentDate", "DATE", null) })
					.Select(c => new HrBankFileColumn { Field = c.Item1, Header = c.Item2, Value = c.Item3 }).ToList()),
			};
			tweak?.Invoke(f);
			return f;
		}

		private static HrBankFileLine Line(int seq, string? iban, decimal net, string? bank = "NBK", string? problem = null) =>
			new(seq, 100 + seq, "موظف " + seq, "Employee " + seq, "2900101" + seq, iban, bank, 1000, 300, 1300, 0, net, problem);

		private static string Text(HrBankFileResult r) => Encoding.UTF8.GetString(r.Content).TrimStart('﻿');

		[Fact]
		public void File_carries_header_lines_and_month_end_date()
		{
			var r = HrBankFileService.Build(Format(), new[] { Line(1, "GB82WEST12345698765432", 1345m) }, 2026, 11, "KWD");
			Assert.Equal("SEQ,IBAN,BANK,AMOUNT,DATE\r\n1,GB82WEST12345698765432,NBK,1345.000,30/11/2026\r\n", Text(r));
			Assert.Equal("SALARIES_202611.csv", r.FileName);
			Assert.Equal(1, r.Lines);
			Assert.Equal(1345m, r.Total);
		}

		[Fact]
		public void Left_out_employees_leave_no_gap_in_the_sequence()
		{
			var r = HrBankFileService.Build(Format(), new[] { Line(1, "X", 10m), Line(2, null, 20m, problem: "NoIban"), Line(3, "Y", 30m) }, 2026, 11, "KWD");
			var lines = Text(r).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
			Assert.Equal(new[] { "1", "2" }, lines.Skip(1).Select(l => l.Split(',')[0]));
			Assert.Equal(2, r.Lines);
			Assert.Equal(1, r.Problems);
			Assert.Equal(40m, r.Total);
		}

		[Fact]
		public void A_value_holding_the_delimiter_is_quoted()
		{
			var r = HrBankFileService.Build(Format(), new[] { Line(1, "X", 10m, bank: "KFH, Kuwait") }, 2026, 11, "KWD");
			Assert.Contains(",\"KFH, Kuwait\",", Text(r));
			var semi = HrBankFileService.Build(Format(f => f.Delimiter = ";"), new[] { Line(1, "X", 10m, bank: "KFH, Kuwait") }, 2026, 11, "KWD");
			Assert.Contains(";KFH, Kuwait;", Text(semi));   // not special under a semicolon
		}

		[Fact]
		public void Every_format_setting_is_honoured()
		{
			var f = Format(x =>
			{
				x.Delimiter = "\t"; x.HeaderRow = false; x.DecimalPlaces = 2; x.DateFormat = "yyyyMMdd"; x.TrailerRow = "CountTotal";
				x.PaymentDay = 25; x.FileNamePattern = "WPS_{yyyy}_{MM}.txt"; x.WithBom = true; x.EmployerCode = "EMP-77";
			}, ("Seq", "", null), ("EmployerCode", "", null), ("Constant", "", "SAL"), ("Net", "", null), ("PaymentDate", "", null), ("Currency", "", null), ("Month", "", null));
			var r = HrBankFileService.Build(f, new[] { Line(1, "X", 1345.555m), Line(2, "Y", 10m) }, 2026, 2, "KWD");
			Assert.True(r.Content.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
			Assert.Equal("1\tEMP-77\tSAL\t1345.56\t20260225\tKWD\t202602\r\n2\tEMP-77\tSAL\t10.00\t20260225\tKWD\t202602\r\nTOTAL\t2\t1355.56\r\n", Text(r));
			Assert.Equal("WPS_2026_02.txt", r.FileName);
		}

		[Fact]
		public void Payment_day_past_the_month_end_falls_on_the_last_day()
		{
			Assert.Equal(new DateTime(2026, 2, 28), HrBankFileService.PaymentDate(Format(x => x.PaymentDay = 31), 2026, 2));
			Assert.Equal(new DateTime(2026, 2, 28), HrBankFileService.PaymentDate(Format(x => x.PaymentDay = 0), 2026, 2));
		}

		[Fact]
		public void Insurance_file_writes_its_named_amounts_and_totals_the_contribution()
		{
			var f = Format(x => x.TrailerRow = "CountTotal", ("Seq", "SEQ", null), ("CivilId", "CIVIL_ID", null), ("Nationality", "NAT", null), ("Wage", "WAGE", null),
				("EmployeeShare", "EE", null), ("EmployerShare", "ER", null), ("TotalShare", "TOTAL", null));
			f.Kind = HrBankFileFields.Insurance;
			var line = new HrBankFileLine(1, 2046, "يوسف", "Yousef", "290031412345", null, null, 1350m, 0, 1350m, 204.25m, 484.5m, null,
				new Dictionary<string, decimal> { ["Wage"] = 1350m, ["EmployeeShare"] = 204.25m, ["EmployerShare"] = 280.25m, ["TotalShare"] = 484.5m }, "1");
			var r = HrBankFileService.Build(f, new[] { line }, 2026, 11, "KWD");
			Assert.Equal("SEQ,CIVIL_ID,NAT,WAGE,EE,ER,TOTAL\r\n1,290031412345,1,1350.000,204.250,280.250,484.500\r\nTOTAL,1,484.500\r\n", Text(r));
		}

		[Fact]
		public void Each_file_kind_offers_its_own_fields()
		{
			Assert.True(HrBankFileFields.Known("Insurance", "EmployerShare"));
			Assert.False(HrBankFileFields.Known("Insurance", "Iban"));
			Assert.True(HrBankFileFields.Known("Bank", "Iban"));
			Assert.False(HrBankFileFields.Known("Bank", "EmployerShare"));
			Assert.Equal(HrBankFileFields.Bank, HrBankFileFields.Norm(null));
			Assert.Equal(HrBankFileFields.Insurance, HrBankFileFields.Norm("insurance"));
		}

		// ---- who may read a payslip document (Hr.Payslip reads under the public key; the RECORD is enforced) --------
		private static CrossBuy.Models.Platform.BusinessContext Ctx(int company, int? employee, params string[] roles) =>
			new() { CompanyId = company, EmployeeId = employee, Roles = roles, UserId = "u" };

		[Fact] public void An_employee_reads_their_own_payslip() => Assert.True(CrossBuy.BL.Reporting.HrPayslipDataSource.MayRead(Ctx(1, 5), 1, 5));
		[Fact] public void An_employee_never_reads_a_colleagues_payslip() => Assert.False(CrossBuy.BL.Reporting.HrPayslipDataSource.MayRead(Ctx(1, 5), 1, 17));
		[Fact] public void A_caller_with_no_employee_reads_nothing() => Assert.False(CrossBuy.BL.Reporting.HrPayslipDataSource.MayRead(Ctx(1, null), 1, 5));
		[Fact] public void Hr_reads_any_payslip_of_its_company() => Assert.True(CrossBuy.BL.Reporting.HrPayslipDataSource.MayRead(Ctx(1, 2046, "HrOfficer"), 1, 17));
		[Fact] public void Nobody_reads_another_companys_payslip() => Assert.False(CrossBuy.BL.Reporting.HrPayslipDataSource.MayRead(Ctx(1, 5, "Admin"), 2, 5));
		[Fact] public void An_unresolved_company_reads_nothing() => Assert.False(CrossBuy.BL.Reporting.HrPayslipDataSource.MayRead(Ctx(0, 5), 0, 5));

		// ---- HR report parameters never take a name the platform reserves (it fills those itself and ignores the URL) ----
		[Fact]
		public void Hr_report_parameters_avoid_the_reserved_system_names()
		{
			var reserved = typeof(CrossBuy.BL.Reporting.ReportSystemParameters).GetFields()
				.Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToHashSet();
			var datasets = new[] { CrossBuy.BL.Reporting.HrPayslipDataset.Definition(), CrossBuy.BL.Reporting.HrDocumentDatasets.Letter(), CrossBuy.BL.Reporting.HrDocumentDatasets.EmployeeDocuments() };
			foreach (var d in datasets)
				foreach (var p in d.Parameters.Where(p => !p.SystemSupplied))
					Assert.False(reserved.Contains(p.Key), $"{d.DatasetCode}: parameter '{p.Key}' is a reserved system name");
		}

		// ---- the Excel import's normalisation ------------------------------------------------------------------------
		[Fact]
		public void Imported_values_are_normalised_and_an_iban_is_masked_for_history()
		{
			Assert.Equal("KW81CBKU0000000000001234560101", HrBankDetailsImport.NormIban(" kw81 cbku-0000 0000 0000 1234 5601 01 "));
			Assert.Null(HrBankDetailsImport.NormIban("   "));
			Assert.Equal("290010112345", HrBankDetailsImport.NormCivil("2900-1011 2345"));
			Assert.Equal("…0101", HrBankDetailsImport.Mask("KW81CBKU0000000000001234560101"));
			Assert.Equal("—", HrBankDetailsImport.Mask(null));
		}
	}
}
