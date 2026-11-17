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
	// HOSPITAL H171: the clinical rule register. What must never happen:
	//   · a rule without its source, its version, what it covers and its limits;
	//   · a clinic value outside what the rule allows, or a published default outside it;
	//   · an approval of one version silently covering the next one (its values must not carry over);
	//   · an approval by someone without `manage`, or without a person on it.
	// ==========================================================================================
	public class HospitalClinicalRuleTests
	{
		private static string Root()
		{
			for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
				if (Directory.Exists(Path.Combine(d.FullName, "CrossBuy", "Resources"))) return d.FullName;
			throw new DirectoryNotFoundException("CrossBuy/Resources");
		}

		private static BusinessContext Ctx(int employeeId = 7) => new() { CompanyId = 1, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		[Fact]
		public void Every_rule_names_its_source_version_cover_and_limits_with_defaults_in_bounds()
		{
			Assert.Equal(HospitalClinicalRuleCatalog.All.Count, HospitalClinicalRuleCatalog.All.Select(r => r.Code).Distinct().Count());
			foreach (var r in HospitalClinicalRuleCatalog.All)
			{
				Assert.False(string.IsNullOrWhiteSpace(r.Reference), r.Code);
				Assert.False(string.IsNullOrWhiteSpace(r.Version), r.Code);
				Assert.StartsWith("rule:" + r.Code + ":", r.Covers);
				Assert.StartsWith("rule:" + r.Code + ":", r.Limits);
				foreach (var p in r.Params) Assert.InRange(p.Default, p.Min, p.Max);
			}
		}

		[Fact]
		public void Every_label_of_the_register_is_translated()
		{
			var keys = HospitalClinicalRuleCatalog.All.SelectMany(r => new[] { r.Name, r.Covers, r.Limits, "area:" + r.Area }
				.Concat(r.Params.SelectMany(p => new[] { "param:" + p.Key, "unit:" + p.Unit }))).Distinct().ToList();
			foreach (var lang in new[] { "", ".ar", ".en", ".fr" })
			{
				var path = Path.Combine(Root(), "CrossBuy", "Resources", $"HospitalResources{lang}.resx");
				var xml = File.ReadAllText(path);
				foreach (var k in keys) Assert.True(xml.Contains($"<data name=\"{System.Security.SecurityElement.Escape(k)}\""), $"{k} missing in HospitalResources{lang}.resx");
			}
		}

		[Fact]
		public void An_approval_covers_its_version_only_and_bounded_values()
		{
			var def = HospitalClinicalRuleCatalog.Of(HospitalClinicalRuleCatalog.Immunotherapy)!;
			Assert.Equal("This rule changed since the page was opened: approve the version shown now", HospitalClinicalRuleCatalog.Refusal(def, "0", new Dictionary<string, decimal?> { ["restartStepsDown"] = 2, ["largeLocalMm"] = 25 }));
			Assert.Equal("Enter every value of the rule", HospitalClinicalRuleCatalog.Refusal(def, def.Version, new Dictionary<string, decimal?> { ["restartStepsDown"] = 2 }));
			Assert.Equal("A value is outside what the rule allows", HospitalClinicalRuleCatalog.Refusal(def, def.Version, new Dictionary<string, decimal?> { ["restartStepsDown"] = 9, ["largeLocalMm"] = 25 }));
			var old = new HospitalClinicalRuleApproval { Version = "0", ParamsJson = "{\"restartStepsDown\":3,\"largeLocalMm\":25}" };
			Assert.Equal(2, HospitalClinicalRuleCatalog.Values(def, old)["restartStepsDown"]);   // an older version's value does not carry over
			old.Version = def.Version;
			Assert.Equal(3, HospitalClinicalRuleCatalog.Values(def, old)["restartStepsDown"]);
		}

		[Fact]
		public async Task The_lead_approves_and_the_rule_reads_the_clinics_value()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var svc = new HospitalClinicalRuleService(host.Db, access);
			var def = HospitalClinicalRuleCatalog.Of(HospitalClinicalRuleCatalog.EgfrDosing)!;
			Assert.Equal(90, await HospitalClinicalRuleCatalog.ParamAsync(host.Db, 1, def.Code, "recencyDays", default));   // unapproved: the published default
			Assert.False((await svc.RegisterAsync(Ctx())).Single(r => r.Rule.Code == def.Code).Current);
			Assert.Equal("forbidden", (await svc.ApproveAsync(Ctx(), new HospitalRuleApproveInput { Code = def.Code, Version = def.Version, Values = new() { ["recencyDays"] = 30 } }, null, DateTime.UtcNow)).Error);
			Assert.True((await svc.ApproveAsync(Ctx(), new HospitalRuleApproveInput { Code = def.Code, Version = def.Version, Values = new() { ["recencyDays"] = 30 }, Note = "Ward policy 2026" }, 7, DateTime.UtcNow)).Ok);
			Assert.Equal(30, await HospitalClinicalRuleCatalog.ParamAsync(host.Db, 1, def.Code, "recencyDays", default));
			var row = (await svc.RegisterAsync(Ctx())).Single(r => r.Rule.Code == def.Code);
			Assert.True(row.Current);
			Assert.Equal(7, row.Latest!.ApprovedByEmployeeId);
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var model = HospitalSliceParity.Model(HospitalClinicalRulesModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h171_clinical_rules.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalClinicalRuleApproval), "HospitalClinicalRuleApprovals");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalClinicalRuleApproval), "HospitalClinicalRuleApprovals");
		}
	}
}
