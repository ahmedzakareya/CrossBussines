using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H226 — a scheduled report runs as its OWNER. The ward's downtime copies are a clinical
	// report on a schedule, and the module gate refused every worker context outright, so they could never
	// be sent. What must hold now:
	//   · a worker that carries an employee is judged by that employee's STORED grants — allowed with one,
	//     refused without one once the module is configured;
	//   · a worker with no employee is still refused, even where the module is bootstrap-open;
	//   · the same answer reaches the reporting key (hospital.reports.clinical), not only the module action.
	// ==========================================================================================
	public class HospitalScheduledReportAccessTests
	{
		private static BusinessContext Worker(int company, int? employee) => new()
		{
			CompanyId = company, EmployeeId = employee, UserId = employee == null ? null : "u" + employee,
			Roles = Array.Empty<string>(), Source = BusinessContextSource.Worker, CorrelationId = Guid.NewGuid(),
		};

		private static HospitalAccessService Access(PlatformTestHost host) =>
			new(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);

		private static async Task GrantAsync(PlatformTestHost host, int company, int employee, string role)
		{
			host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
			{
				CompanyID = company, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee,
				PrincipalId = employee, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow,
			});
			await host.Seed.SaveChangesAsync();
		}

		[Fact]
		public async Task A_schedule_owner_is_judged_by_their_stored_grants()
		{
			using var host = new PlatformTestHost(companyId: 1);
			await GrantAsync(host, 1, 7, HospitalRoles.HospitalDoctor);
			await GrantAsync(host, 1, 9, HospitalRoles.HospitalReception);
			var access = Access(host);

			Assert.True(await access.CanAsync(Worker(1, 7), HospitalActions.ClinicalRead));
			Assert.False(await access.CanAsync(Worker(1, 9), HospitalActions.ClinicalRead));   // reception reads no clinical content
			Assert.False(await access.CanAsync(Worker(1, 8), HospitalActions.ClinicalRead));   // configured company, no grant
			Assert.False(await access.CanAsync(Worker(1, null), HospitalActions.ClinicalRead));
		}

		[Fact]
		public async Task A_worker_with_no_employee_is_refused_even_where_the_module_is_open()
		{
			using var host = new PlatformTestHost(companyId: 2);
			var access = Access(host);
			Assert.False(await access.CanAsync(Worker(2, null), HospitalActions.Read));
			Assert.False(await access.CanAsync(Worker(2, null), HospitalActions.ClinicalRead));
		}

		[Fact]
		public async Task The_clinical_report_key_follows_the_owner()
		{
			using var host = new PlatformTestHost(companyId: 1);
			await GrantAsync(host, 1, 7, HospitalRoles.HospitalNurse);
			var authority = new HospitalReportModuleAuthority(new IModuleAccessService[] { Access(host) });

			Assert.True(await authority.EvaluateAsync(HospitalReportPermissions.Clinical, Worker(1, 7)));
			Assert.False(await authority.EvaluateAsync(HospitalReportPermissions.Clinical, Worker(1, 8)));
			Assert.False(await authority.EvaluateAsync(HospitalReportPermissions.Clinical, Worker(1, null)));
		}
	}
}
