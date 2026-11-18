using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H194 — the hospital team's roles, given together. What must never happen: a suggestion that gives a doctor
	// the nurse's or the desk's role on top of their own, a request that grants a role outside the hospital's list, or a
	// grant that repeats one already held.
	// ==========================================================================================
	public class HospitalStaffRolesTests
	{
		private static HospitalStaffSignal S(string role, int n) => new(role, n);

		[Fact]
		public void Each_persons_work_suggests_their_roles()
		{
			Assert.Equal(new[] { HospitalRoles.HospitalNurse }, HospitalStaffRoleRules.Suggest(new[] { S(HospitalRoles.HospitalNurse, 40) }));
			Assert.Equal(new[] { HospitalRoles.HospitalReception, HospitalRoles.HospitalCashier },
				HospitalStaffRoleRules.Suggest(new[] { S(HospitalRoles.HospitalCashier, 12), S(HospitalRoles.HospitalReception, 30) }));   // in the hospital's own order
			// a doctor stays a doctor: the ward and desk work a doctor does is part of the doctor's role
			Assert.Equal(new[] { HospitalRoles.HospitalDoctor }, HospitalStaffRoleRules.Suggest(new[] { S(HospitalRoles.HospitalDoctor, 1), S(HospitalRoles.HospitalNurse, 5), S(HospitalRoles.HospitalReception, 3) }));
			// the senior role covers the bench's
			Assert.Equal(new[] { HospitalRoles.HospitalLabSpecialist }, HospitalStaffRoleRules.Suggest(new[] { S(HospitalRoles.HospitalLabTechnician, 9), S(HospitalRoles.HospitalLabSpecialist, 4) }));
			Assert.Equal(new[] { HospitalRoles.HospitalRadiologist }, HospitalStaffRoleRules.Suggest(new[] { S(HospitalRoles.HospitalRadiographer, 2), S(HospitalRoles.HospitalRadiologist, 2) }));
			Assert.Empty(HospitalStaffRoleRules.Suggest(new[] { S(HospitalRoles.HospitalNurse, 0) }));
			// the work of four roles or more: the administrator, alone
			Assert.Equal(new[] { HospitalRoles.HospitalAdministrator }, HospitalStaffRoleRules.Suggest(new[] { S(HospitalRoles.HospitalDoctor, 1), S(HospitalRoles.HospitalCashier, 1), S(HospitalRoles.HospitalPharmacist, 5), S(HospitalRoles.HospitalLabSpecialist, 7) }));
		}

		[Fact]
		public void The_grant_plan_skips_what_is_held_and_refuses_an_unknown_role()
		{
			var held = new HashSet<(int, string)> { (7, HospitalRoles.HospitalDoctor) };
			var (plan, why) = HospitalStaffRoleRules.Plan(new[]
			{
				new HospitalStaffGrantLine { EmployeeId = 7, Roles = { HospitalRoles.HospitalDoctor } },
				new HospitalStaffGrantLine { EmployeeId = 8, Roles = { HospitalRoles.HospitalNurse, HospitalRoles.HospitalNurse } },
				new HospitalStaffGrantLine { EmployeeId = 9, Roles = { HospitalRoles.HospitalCashier } },
			}, held);
			Assert.Null(why);
			Assert.Equal(new[] { (8, HospitalRoles.HospitalNurse), (9, HospitalRoles.HospitalCashier) }, plan);
			Assert.Equal("Choose the roles from the hospital's list", HospitalStaffRoleRules.Plan(new[] { new HospitalStaffGrantLine { EmployeeId = 8, Roles = { "Emperor" } } }, held).Refusal);
			Assert.Equal("Nothing new to grant", HospitalStaffRoleRules.Plan(new[] { new HospitalStaffGrantLine { EmployeeId = 7, Roles = { HospitalRoles.HospitalDoctor } } }, held).Refusal);
		}

		[Fact]
		public void Every_role_has_a_name_on_the_screen()
		{
			var keys = System.Xml.Linq.XDocument.Load(Path.Combine(Root(), "CrossBuy", "Resources", "HospitalResources.ar.resx")).Root!.Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet();
			foreach (var r in HospitalRoles.All) Assert.Contains("hrole:" + r, keys);
		}

		private static string Root()
		{
			for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
				if (File.Exists(Path.Combine(d.FullName, "CrossBuy", "Program.cs"))) return d.FullName;
			throw new DirectoryNotFoundException("CrossBuy not found above " + AppContext.BaseDirectory);
		}
	}
}
