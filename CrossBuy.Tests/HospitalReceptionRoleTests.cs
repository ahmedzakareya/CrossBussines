using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H155: the reception role. What must never happen:
	//   · a reception-only person reading a patient's clinical content (results, imaging, diagnoses);
	//   · a clinical role — staff (nurses), doctors, the lab, imaging, the pharmacy — losing it;
	//   · reception doing clinical or ward work (consult, admit/nursing, lab, pharmacy) or managing the hospital;
	//   · the desk unable to do its own work (read, register, book, take money).
	// While no hospital role is configured the module stays bootstrap-open, as before.
	// ==========================================================================================
	public class HospitalReceptionRoleTests
	{
		private static BusinessContext Ctx(int employeeId) => new() { CompanyId = 1, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private static HospitalAccessService Access(PlatformTestHost host) =>
			new(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);

		private static async Task Grant(PlatformTestHost host, int employeeId, string role)
		{
			host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
			{
				CompanyID = 1, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = employeeId, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow,
			});
			await host.Seed.SaveChangesAsync();
		}

		[Fact]
		public async Task Reception_works_the_desk_but_never_reads_the_clinical_file()
		{
			using var host = new PlatformTestHost();
			await Grant(host, 20, HospitalRoles.HospitalReception);
			await Grant(host, 21, HospitalRoles.HospitalStaff);
			await Grant(host, 22, HospitalRoles.HospitalDoctor);
			await Grant(host, 23, HospitalRoles.HospitalPharmacist);
			var a = Access(host);
			foreach (var act in new[] { HospitalActions.Read, HospitalActions.Register, HospitalActions.Book, HospitalActions.Bill })
				Assert.True(await a.CanAsync(Ctx(20), act, null), act);
			foreach (var act in new[] { HospitalActions.ClinicalRead, HospitalActions.Consult, HospitalActions.Admit, HospitalActions.Lab, HospitalActions.Rad, HospitalActions.Pharmacy, HospitalActions.Manage, HospitalActions.ConfidentialView })
				Assert.False(await a.CanAsync(Ctx(20), act, null), act);
			foreach (var who in new[] { 21, 22, 23 })
				Assert.True(await a.CanAsync(Ctx(who), HospitalActions.ClinicalRead, null), "employee " + who);   // nurses, doctors, the pharmacy keep the clinical file
		}

		[Fact]
		public async Task With_no_hospital_role_configured_the_module_stays_open_as_before()
		{
			using var host = new PlatformTestHost();
			Assert.True(await Access(host).CanAsync(Ctx(30), HospitalActions.ClinicalRead, null));
		}

		[Fact]
		public void The_role_and_the_action_are_published()
		{
			Assert.Contains(HospitalRoles.HospitalReception, HospitalRoles.All);
			Assert.Contains(HospitalActions.ClinicalRead, HospitalActions.All);
		}
	}
}
