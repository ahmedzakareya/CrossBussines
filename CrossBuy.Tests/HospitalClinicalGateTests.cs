using System.Reflection;
using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Controllers;
using CrossBuy.Models;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H156: the clinical gate and the other paths to clinical data. What must never happen:
	//   · a reception-only person reaching a clinical screen or data endpoint — including one added later and never marked;
	//   · a reception-only person printing a clinical paper or asking the assistant about a patient's medicines or ward;
	//   · the desk losing its own screens and papers, or any clinical role losing anything;
	//   · reception saving a patient form wiping the chronic conditions it is not shown.
	// ==========================================================================================
	public class HospitalClinicalGateTests
	{
		private static BusinessContext Ctx(int employeeId) => new() { CompanyId = 1, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private sealed class Fixed : IBusinessContextAccessor
		{
			private readonly BusinessContext? _c; public Fixed(BusinessContext? c) => _c = c;
			public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c!);
			public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c);
		}

		private static HospitalAccessService Access(PlatformTestHost host) =>
			new(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);

		private static async Task Grant(PlatformTestHost host, int employeeId, string role)
		{
			host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment { CompanyID = 1, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = employeeId, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow });
			await host.Seed.SaveChangesAsync();
		}

		/// Runs the gate for one action method of the hospital controller; true = the action ran.
		private static async Task<bool> Passes(HospitalAccessService access, BusinessContext ctx, string actionName)
		{
			var method = typeof(HospitalController).GetMethods().First(m => m.Name == actionName);
			var services = new ServiceCollection().AddSingleton<IBusinessContextAccessor>(new Fixed(ctx)).AddSingleton<IHospitalAccessService>(access).BuildServiceProvider();
			var http = new DefaultHttpContext { RequestServices = services };
			http.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
			var ad = new ActionDescriptor { EndpointMetadata = method.GetCustomAttributes(true).ToList() };
			var ac = new ActionContext(http, new RouteData(), ad);
			var exec = new ActionExecutingContext(ac, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
			bool ran = false;
			await new HospitalClinicalGateAttribute().OnActionExecutionAsync(exec, () => { ran = true; return Task.FromResult(new ActionExecutedContext(ac, new List<IFilterMetadata>(), new object())); });
			if (!ran) Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)exec.Result!).StatusCode);
			return ran;
		}

		[Fact]
		public async Task Reception_reaches_the_desk_and_no_clinical_screen_while_the_care_team_reaches_both()
		{
			using var host = new PlatformTestHost();
			await Grant(host, 20, HospitalRoles.HospitalReception);
			await Grant(host, 21, HospitalRoles.HospitalStaff);
			var a = Access(host);
			foreach (var desk in new[] { "FrontDesk", "FrontDeskPatient", "FrontDeskSearch", "Patient", "Bills", "Cashier", "IntakeWhere" })
				Assert.True(await Passes(a, Ctx(20), desk), desk);
			foreach (var clinical in new[] { "Consult", "Pharmacy", "Records", "Antenatal", "DentalChart", "Compounding", "PharmacyIndicators", "CanvasSheet" })
			{
				Assert.False(await Passes(a, Ctx(20), clinical), clinical);
				Assert.True(await Passes(a, Ctx(21), clinical), "staff: " + clinical);
			}
		}

		[Fact]
		public async Task While_no_hospital_role_is_configured_everyone_passes_as_before()
		{
			using var host = new PlatformTestHost();
			Assert.True(await Passes(Access(host), Ctx(30), "Consult"));
		}

		[Fact]
		public void The_gate_guards_the_whole_controller_and_marks_only_administrative_files()
		{
			Assert.NotNull(typeof(HospitalController).GetCustomAttribute<HospitalClinicalGateAttribute>());
			var marked = typeof(HospitalController).GetMethods().Where(m => m.GetCustomAttribute<HospitalReceptionAllowedAttribute>() != null).Select(m => m.Name).ToHashSet();
			Assert.Contains("FrontDeskPatient", marked);
			foreach (var clinical in new[] { "Consult", "Pharmacy", "Lab", "Radiology", "Nursing", "Emergency", "Theatre", "Oncology", "Records" })
				Assert.DoesNotContain(clinical, marked);
		}

		[Fact]
		public async Task Clinical_papers_and_analyses_need_clinical_read_and_the_desk_keeps_its_own()
		{
			var docs = HospitalDocumentDatasets.All().ToDictionary(d => d.DatasetCode, d => d.RequiredPermissionKey);
			foreach (var c in new[] { HospitalDocumentDatasetCodes.Prescription, HospitalDocumentDatasetCodes.DischargeSummary, HospitalDocumentDatasetCodes.SickLeave, HospitalDocumentDatasetCodes.RxLabel, HospitalDocumentDatasetCodes.CompoundLabel })
				Assert.Equal(HospitalReportPermissions.Clinical, docs[c]);
			foreach (var c in new[] { HospitalDocumentDatasetCodes.Bill, HospitalDocumentDatasetCodes.PatientStatement, HospitalDocumentDatasetCodes.VisitSlip, HospitalDocumentDatasetCodes.Wristband, HospitalDocumentDatasetCodes.DepositReceipt, HospitalDocumentDatasetCodes.CreditNote })
				Assert.Equal(HospitalReportPermissions.View, docs[c]);

			using var host = new PlatformTestHost();
			await Grant(host, 20, HospitalRoles.HospitalReception);
			await Grant(host, 21, HospitalRoles.HospitalDoctor);
			var authority = new HospitalReportModuleAuthority(new IModuleAccessService[] { Access(host) });
			Assert.False(await authority.EvaluateAsync(HospitalReportPermissions.Clinical, Ctx(20)));
			Assert.True(await authority.EvaluateAsync(HospitalReportPermissions.View, Ctx(20)));
			Assert.True(await authority.EvaluateAsync(HospitalReportPermissions.Clinical, Ctx(21)));
		}

		[Fact]
		public async Task Reception_saving_a_patient_form_never_wipes_the_chronic_conditions()
		{
			using var host = new PlatformTestHost();
			await Grant(host, 20, HospitalRoles.HospitalReception);
			await Grant(host, 21, HospitalRoles.HospitalStaff);
			var svc = new HospitalPatientService(host.Db, Access(host));
			var id = (await svc.SaveAsync(Ctx(21), new HospitalPatientInput { Name = "م", NameEn = "P", Sex = "M", ChronicConditions = "Diabetes" }, 21, DateTime.UtcNow)).Id!.Value;
			var saved = await svc.SaveAsync(Ctx(20), new HospitalPatientInput { Id = id, Name = "م", NameEn = "P", Sex = "M", Phone = "+96550000000", Allergies = "Penicillin" }, 20, DateTime.UtcNow);
			Assert.True(saved.Ok, saved.Error);
			var row = await host.Db.HospitalPatients.FindAsync(id);
			await host.Db.Entry(row!).ReloadAsync();
			Assert.Equal(("Diabetes", "Penicillin"), (row!.ChronicConditions, row.Allergies));   // the desk records allergies, never touches the conditions
		}
	}
}
