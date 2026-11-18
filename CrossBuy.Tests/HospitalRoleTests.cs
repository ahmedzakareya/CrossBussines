using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.ModulePermissions;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H192 — the finer hospital roles, and the vocabulary that makes every hospital role assignable. What must
	// never happen: a nurse writing the doctor's note or touching the cash, a cashier reading the clinical file, or the
	// hospital's roles missing from the platform's grants screen (which left the module open to everyone).
	// ==========================================================================================
	public class HospitalRoleTests
	{
		private static BusinessContext Ctx(int employeeId) => new() { CompanyId = 1, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private static HospitalAccessService Access(PlatformTestHost host) =>
			new(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);

		private static async Task Grant(PlatformTestHost host, int employeeId, string role)
		{
			host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment { CompanyID = 1, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = employeeId, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow });
			await host.Seed.SaveChangesAsync();
		}

		[Fact]
		public async Task A_nurse_gives_care_and_reads_the_clinical_file_but_never_writes_the_doctors_note_or_takes_money()
		{
			using var host = new PlatformTestHost();
			await Grant(host, 40, HospitalRoles.HospitalNurse);
			var a = Access(host);
			foreach (var yes in new[] { HospitalActions.Read, HospitalActions.ClinicalRead, HospitalActions.Admit })
				Assert.True(await a.CanAsync(Ctx(40), yes, null), yes);
			foreach (var no in new[] { HospitalActions.Consult, HospitalActions.Bill, HospitalActions.Register, HospitalActions.Manage, HospitalActions.Pharmacy, HospitalActions.Lab, HospitalActions.ConfidentialView, HospitalActions.CallRecordings })
				Assert.False(await a.CanAsync(Ctx(40), no, null), no);
		}

		[Fact]
		public async Task A_cashier_bills_and_reads_the_administrative_file_only()
		{
			using var host = new PlatformTestHost();
			await Grant(host, 41, HospitalRoles.HospitalCashier);
			var a = Access(host);
			foreach (var yes in new[] { HospitalActions.Read, HospitalActions.Bill })
				Assert.True(await a.CanAsync(Ctx(41), yes, null), yes);
			foreach (var no in new[] { HospitalActions.ClinicalRead, HospitalActions.Consult, HospitalActions.Admit, HospitalActions.Register, HospitalActions.Book, HospitalActions.Manage, HospitalActions.Pharmacy })
				Assert.False(await a.CanAsync(Ctx(41), no, null), no);
		}

		[Fact]
		public async Task The_existing_roles_keep_what_they_had()
		{
			using var host = new PlatformTestHost();
			await Grant(host, 42, HospitalRoles.HospitalStaff);
			await Grant(host, 43, HospitalRoles.HospitalDoctor);
			var a = Access(host);
			foreach (var yes in new[] { HospitalActions.Admit, HospitalActions.Bill, HospitalActions.Register, HospitalActions.Book, HospitalActions.ClinicalRead })
				Assert.True(await a.CanAsync(Ctx(42), yes, null), "staff " + yes);
			Assert.True(await a.CanAsync(Ctx(43), HospitalActions.Consult, null));
			Assert.False(await a.CanAsync(Ctx(43), HospitalActions.Bill, null));
		}

		[Fact]
		public void Every_hospital_role_is_assignable_on_the_platform()
		{
			var r = new PlatformPermissionVocabularyRegistry(new IPlatformPermissionVocabulary[] { new HospitalPermissionVocabulary() });
			foreach (var role in HospitalRoles.All) Assert.True(r.IsKnownRole(EntityRegistry.ScopeHospital, role), role);
			Assert.Equal(HospitalActions.Manage, r.ManageActionFor(EntityRegistry.ScopeHospital));
			Assert.Contains(HospitalRoles.HospitalNurse, HospitalRoles.All);
			Assert.Contains(HospitalRoles.HospitalCashier, HospitalRoles.All);
		}

		[Fact]
		public void The_startup_registers_the_hospital_vocabulary()
		{
			string? program = null;
			for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && program == null; d = d.Parent)
				if (File.Exists(Path.Combine(d.FullName, "CrossBuy", "Program.cs"))) program = File.ReadAllText(Path.Combine(d.FullName, "CrossBuy", "Program.cs"));
			Assert.NotNull(program);
			Assert.Contains("IPlatformPermissionVocabulary, CrossBuy.BL.ModulePermissions.HospitalPermissionVocabulary>()", program);
		}
	}
}
