using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H177 — addenda and corrections. What must never happen:
	//   · an addendum on a record that is still open (it is edited itself);
	//   · a correction with no reason, or an addendum with no author;
	//   · another company's record taking one;
	//   · the system editing or deleting one (there is no path to).
	// ==========================================================================================
	public class HospitalAddendumTests
	{
		private static BusinessContext Ctx(int company = 1) => new() { CompanyId = company, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		[Fact]
		public void The_rules()
		{
			var ok = new HospitalAddendumInput { Kind = HospitalAddendumKind.Addendum, Text = "Culture came back: E. coli" };
			Assert.Null(HospitalAddendumRules.Refusal(true, ok));
			Assert.Equal("This record is not signed yet: edit it itself", HospitalAddendumRules.Refusal(false, ok));
			Assert.Equal("A correction says why the record was wrong", HospitalAddendumRules.Refusal(true, new HospitalAddendumInput { Kind = HospitalAddendumKind.Correction, Text = "Left, not right" }));
			Assert.Equal("Write the addendum", HospitalAddendumRules.Refusal(true, new HospitalAddendumInput { Kind = HospitalAddendumKind.Addendum, Text = " " }));
			Assert.Equal("Choose the part of the record", HospitalAddendumRules.Refusal(true, new HospitalAddendumInput { Kind = HospitalAddendumKind.Addendum, Text = "x", Section = "Billing" }));
			Assert.Equal("An addendum is 2000 characters at most", HospitalAddendumRules.Refusal(true, new HospitalAddendumInput { Kind = HospitalAddendumKind.Addendum, Text = new string('x', 2001) }));
		}

		[Fact]
		public async Task Only_a_closed_labour_of_this_company_takes_one_and_it_lists_with_its_author()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var svc = new HospitalAddendumService(host.Db, access);
			var p = (await new HospitalPatientService(host.Db, access).SaveAsync(Ctx(), new HospitalPatientInput { Name = "م سلمى", NameEn = "Salma", Sex = "F" }, 7, DateTime.UtcNow)).Id!.Value;
			var l = new HospitalLabour { CompanyId = 1, AdmissionId = 1, PatientId = p, Status = HospitalLabourStatus.Active, StartedAt = DateTime.UtcNow };
			host.Db.Add(l); await host.Db.SaveChangesAsync();
			var input = new HospitalAddendumInput { Kind = HospitalAddendumKind.Correction, Section = "Results", Text = "The 10:00 fetal heart was 120, not 210", Reason = "typing error" };
			Assert.Equal("This record is not signed yet: edit it itself", (await svc.AddAsync(Ctx(), HospitalAddendumSubject.Labour, l.ID, input, 7, DateTime.UtcNow)).Error);
			l.Status = HospitalLabourStatus.Closed; l.ClosedAt = DateTime.UtcNow; l.Outcome = HospitalLabourOutcome.Vaginal; await host.Db.SaveChangesAsync();
			Assert.Equal("not_found", (await svc.AddAsync(Ctx(company: 2), HospitalAddendumSubject.Labour, l.ID, input, 7, DateTime.UtcNow)).Error);
			Assert.Equal("forbidden", (await svc.AddAsync(Ctx(), HospitalAddendumSubject.Labour, l.ID, input, null, DateTime.UtcNow)).Error);
			Assert.True((await svc.AddAsync(Ctx(), HospitalAddendumSubject.Labour, l.ID, input, 7, DateTime.UtcNow)).Ok);
			var block = await svc.BlockAsync(Ctx(), HospitalAddendumSubject.Labour, l.ID);
			Assert.True(block.Locked);
			var row = Assert.Single(block.Rows);
			Assert.Equal((p, 7, "typing error"), (row.Addendum.PatientId, row.Addendum.CreatedByEmployeeId, row.Addendum.Reason));
			Assert.Contains("typing error", HospitalAddendumRules.PaperLine(row, false, t => t.ToString("yyyy-MM-dd")));
		}

		[Fact]
		public void The_table_matches_its_slice_and_moves_in_a_merge()
		{
			var model = HospitalSliceParity.Model(HospitalClinicalAddendaModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h177_clinical_addenda.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalClinicalAddendum), "HospitalClinicalAddenda");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalClinicalAddendum), "HospitalClinicalAddenda");
			foreach (var x in HospitalAddendumSubject.All.Concat(HospitalAddendumKind.All)) Assert.Contains("'" + x + "'", s);
			Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == typeof(HospitalClinicalAddendum));
		}
	}
}
