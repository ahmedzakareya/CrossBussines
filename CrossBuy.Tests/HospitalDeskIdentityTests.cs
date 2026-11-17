using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H157: identity at the counter. What must never happen:
	//   · two people whose names read the same (Mohamed / Muhammad, folded Arabic) shown without a warning;
	//   · a name sharing one common part (Mohamed …) flagged as a lookalike of everyone;
	//   · the patient themself, a merged-away file or another company's file offered as a lookalike.
	// ==========================================================================================
	public class HospitalDeskIdentityTests
	{
		private static BusinessContext Ctx(int companyId = 1) => new() { CompanyId = companyId, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		[Theory]
		[InlineData("محمد أحمد علي", "Mohamed Ahmed Ali", "محمد احمد على", "Muhammad Ahmad Ali", true)]
		[InlineData("محمد أحمد علي", "Mohamed Ahmed Ali", "محمد سامي حسن", "Mohamed Sami Hassan", false)]   // one common part is not a match
		[InlineData("سارة علي", "Sara Ali", "ساره على", "Sarah Ali", true)]
		public void Names_that_read_the_same_are_flagged_and_a_shared_first_name_is_not(string a, string aEn, string b, string bEn, bool similar)
			=> Assert.Equal(similar, HospitalIdentityRules.Similar(a, aEn, b, bEn));

		[Fact]
		public async Task The_desk_is_shown_the_lookalikes_with_their_file_and_birth_date_and_nobody_else()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var patients = new HospitalPatientService(host.Db, access);
			async Task<int> New(string ar, string en, int company = 1, DateTime? born = null)
				=> (await patients.SaveAsync(Ctx(company), new HospitalPatientInput { Name = ar, NameEn = en, Sex = "M", BirthDate = born }, 7, DateTime.UtcNow)).Id!.Value;
			var me = await New("محمد أحمد علي", "Mohamed Ahmed Ali", born: new DateTime(1980, 1, 5));
			var twin = await New("محمد احمد على", "Muhammad Ahmad Ali", born: new DateTime(1992, 7, 1));
			await New("محمد سامي حسن", "Mohamed Sami Hassan");
			await New("محمد احمد علي", "Mohammed Ahmed Ali", company: 2);
			var merged = await New("محمد أحمد علي", "Mohamad Ahmed Ali");
			var row = await host.Db.HospitalPatients.FindAsync(merged); row!.MergedIntoPatientId = me; await host.Db.SaveChangesAsync();

			var looks = await new HospitalDeskIdentityService(host.Db).LookalikesAsync(Ctx(), me, 3);
			var one = Assert.Single(looks);
			Assert.Equal((twin, new DateTime(1992, 7, 1)), (one.Id, one.BirthDate!.Value));
		}
	}
}
