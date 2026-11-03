using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H2 — the patient registry.
	//
	// What must never happen: a second file for the same national ID, a file without both names,
	// another company's patient showing up in a list, a search or a duplicate check, a caller
	// without `register` writing anything, or a file opened without the access log knowing.
	// Company counts are ASYMMETRIC (three files against one), so an inverted company predicate
	// cannot pass by returning the same number from the wrong rows.
	// ==========================================================================================
	public class HospitalPatientTests
	{
		private const int CompanyOne = 1, CompanyTwo = 2;
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);

		private static BusinessContext Ctx(int employeeId, int companyId = CompanyOne) => new()
		{
			CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
			Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
		};

		private static HospitalPatientService Patients(PlatformTestHost host) => new(host.Db, new HospitalAccessService(
			new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance));

		private static async Task GrantAsync(PlatformTestHost host, int employeeId, string role, int companyId = CompanyOne)
		{
			host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
			{
				CompanyID = companyId, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee,
				PrincipalId = employeeId, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow,
			});
			await host.Seed.SaveChangesAsync();
		}

		private static HospitalPatientInput Person(string en, string? nid = null, string? phone = null, DateTime? dob = null, string sex = HospitalPatientSex.Male)
			=> new() { Name = "مريض " + en, NameEn = en, NationalId = nid, Phone = phone, BirthDate = dob, Sex = sex };

		private static async Task<int> RegisterAsync(HospitalPatientService svc, BusinessContext ctx, HospitalPatientInput input)
		{
			var r = await svc.SaveAsync(ctx, input, ctx.EmployeeId, Now);
			Assert.True(r.Ok, r.Error);
			return r.Id!.Value;
		}

		// ---- identity -----------------------------------------------------------------------------

		[Fact]
		public async Task The_mrn_is_generated_in_sequence_and_each_company_counts_its_own()
		{
			using var host = new PlatformTestHost();
			var svc = Patients(host);
			var a = await RegisterAsync(svc, Ctx(7), Person("Omar Ali"));
			var b = await RegisterAsync(svc, Ctx(7), Person("Sara Hassan", sex: HospitalPatientSex.Female));
			var c = await RegisterAsync(svc, Ctx(9, CompanyTwo), Person("Khaled Nour"));

			Assert.Equal("MRN-100001", (await host.Db.HospitalPatients.FindAsync(a))!.Mrn);
			Assert.Equal("MRN-100002", (await host.Db.HospitalPatients.FindAsync(b))!.Mrn);
			Assert.Equal("MRN-100001", (await host.Db.HospitalPatients.FindAsync(c))!.Mrn);
			Assert.Equal("MRN-100003", await svc.NextMrnAsync(Ctx(7)));
		}

		[Fact]
		public async Task A_second_file_for_the_same_national_id_is_refused_and_names_the_existing_file()
		{
			using var host = new PlatformTestHost();
			var svc = Patients(host);
			await RegisterAsync(svc, Ctx(7), Person("Omar Ali", nid: "29001011234567"));

			var r = await svc.SaveAsync(Ctx(7), Person("Omar A.", nid: "29001011234567"), 7, Now);
			Assert.False(r.Ok);
			Assert.Equal("This ID number already has a file: {0}", r.Error);
			Assert.Equal("MRN-100001", r.Args![0]);
			Assert.Equal(1, await host.Db.HospitalPatients.CountAsync());

			// The same ID in ANOTHER company is that company's own person, not a duplicate.
			Assert.True((await svc.SaveAsync(Ctx(9, CompanyTwo), Person("Omar Ali", nid: "29001011234567"), 9, Now)).Ok);
		}

		[Theory]
		[InlineData("", "Omar", "Enter the Arabic name")]
		[InlineData("عمر", "", "Enter the English name")]
		public async Task Both_names_are_required(string ar, string en, string error)
		{
			using var host = new PlatformTestHost();
			var r = await Patients(host).SaveAsync(Ctx(7), new HospitalPatientInput { Name = ar, NameEn = en }, 7, Now);
			Assert.False(r.Ok);
			Assert.Equal(error, r.Error);
			Assert.Equal(0, await host.Db.HospitalPatients.CountAsync());
		}

		[Fact]
		public async Task Malformed_identity_fields_are_refused_before_anything_is_written()
		{
			using var host = new PlatformTestHost();
			var svc = Patients(host);
			Assert.Equal("The ID number must be 5 to 20 digits", (await svc.SaveAsync(Ctx(7), Person("A B", nid: "12-34"), 7, Now)).Error);
			Assert.Equal("Enter a real date of birth", (await svc.SaveAsync(Ctx(7), Person("A B", dob: Now.AddDays(1)), 7, Now)).Error);
			Assert.Equal("Enter a valid mobile number", (await svc.SaveAsync(Ctx(7), Person("A B", phone: "call me"), 7, Now)).Error);
			Assert.Equal("Choose the sex", (await svc.SaveAsync(Ctx(7), Person("A B", sex: "X"), 7, Now)).Error);
			Assert.Equal("Unknown blood group", (await svc.SaveAsync(Ctx(7), new HospitalPatientInput { Name = "أ", NameEn = "A", BloodGroup = "Z+" }, 7, Now)).Error);
			Assert.Equal(0, await host.Db.HospitalPatients.CountAsync());
		}

		// ---- authorization ------------------------------------------------------------------------

		[Fact]
		public async Task Staff_register_patients_but_a_caller_with_no_hospital_role_writes_nothing()
		{
			using var host = new PlatformTestHost();
			await GrantAsync(host, 1, HospitalRoles.HospitalAdministrator);   // closes bootstrap
			await GrantAsync(host, 2, HospitalRoles.HospitalStaff);
			var svc = Patients(host);

			Assert.True((await svc.SaveAsync(Ctx(2), Person("Omar Ali"), 2, Now)).Ok);
			var refused = await svc.SaveAsync(Ctx(3), Person("Sara Hassan"), 3, Now);
			Assert.False(refused.Ok);
			Assert.Equal("forbidden", refused.Error);
			Assert.Equal(1, await host.Db.HospitalPatients.CountAsync());
		}

		// ---- isolation ----------------------------------------------------------------------------

		[Fact]
		public async Task Another_companys_patients_never_appear_in_the_list_the_file_or_the_duplicate_check()
		{
			using var host = new PlatformTestHost();
			var svc = Patients(host);
			await RegisterAsync(svc, Ctx(7), Person("Omar Ali", phone: "0100000001"));
			await RegisterAsync(svc, Ctx(7), Person("Sara Hassan", phone: "0100000002"));
			await RegisterAsync(svc, Ctx(7), Person("Mona Adel", phone: "0100000003"));
			var foreign = await RegisterAsync(svc, Ctx(9, CompanyTwo), Person("Khaled Nour", nid: "11112222", phone: "0100000009"));

			var one = await svc.ListAsync(Ctx(7), null, 1, Now);
			Assert.Equal(3, one.Total);
			Assert.DoesNotContain(one.Rows, r => r.Id == foreign);
			Assert.Equal(1, (await svc.ListAsync(Ctx(9, CompanyTwo), null, 1, Now)).Total);

			Assert.Null(await svc.GetAsync(Ctx(7), foreign));
			Assert.Empty(await svc.SimilarAsync(Ctx(7), "11112222", "0100000009", null, "Khaled Nour", null));
			Assert.Empty((await svc.ListAsync(Ctx(7), "Khaled", 1, Now)).Rows);

			// Editing another company's file by id is "not found", and changes nothing.
			var r = await svc.SaveAsync(Ctx(7), new HospitalPatientInput { Id = foreign, Name = "تغيير", NameEn = "Changed", Sex = "M" }, 7, Now);
			Assert.Equal("not_found", r.Error);
			Assert.Equal("Khaled Nour", (await host.Db.HospitalPatients.FindAsync(foreign))!.NameEn);
		}

		// ---- duplicate check ----------------------------------------------------------------------

		[Fact]
		public async Task The_duplicate_check_finds_a_file_by_id_mobile_or_name_and_leaves_out_the_file_being_edited()
		{
			using var host = new PlatformTestHost();
			var svc = Patients(host);
			var omar = await RegisterAsync(svc, Ctx(7), Person("Omar Ali", nid: "55556666", phone: "0100000001"));
			await RegisterAsync(svc, Ctx(7), Person("Sara Hassan", phone: "0100000002"));

			Assert.Equal("national-id", Assert.Single(await svc.SimilarAsync(Ctx(7), "55556666", null, null, null, null)).Reason);
			Assert.Equal("phone", Assert.Single(await svc.SimilarAsync(Ctx(7), null, "0100000002", null, null, null)).Reason);
			Assert.Equal("name", Assert.Single(await svc.SimilarAsync(Ctx(7), null, null, null, "Omar Ali", null)).Reason);
			Assert.Empty(await svc.SimilarAsync(Ctx(7), "55556666", null, null, null, omar));
			Assert.Empty(await svc.SimilarAsync(Ctx(7), null, null, null, null, null));
		}

		[Fact]
		public async Task Two_files_with_the_same_name_and_birth_date_count_as_possible_duplicates()
		{
			using var host = new PlatformTestHost();
			var svc = Patients(host);
			var dob = new DateTime(1990, 3, 4);
			await RegisterAsync(svc, Ctx(7), Person("Omar Ali", dob: dob));
			await RegisterAsync(svc, Ctx(7), Person("Omar Ali", nid: "12345678", dob: dob));
			await RegisterAsync(svc, Ctx(7), Person("Omar Ali", dob: new DateTime(1991, 3, 4)));
			await RegisterAsync(svc, Ctx(9, CompanyTwo), Person("Omar Ali", dob: dob));

			var stats = await svc.StatsAsync(Ctx(7), Now);
			Assert.Equal(3, stats.Total);
			Assert.Equal(2, stats.PossibleDuplicates);
		}

		// ---- record-access log --------------------------------------------------------------------

		[Fact]
		public async Task Opening_creating_and_changing_a_file_are_each_written_to_the_access_log()
		{
			using var host = new PlatformTestHost();
			var svc = Patients(host);
			var id = await RegisterAsync(svc, Ctx(7), Person("Omar Ali"));
			Assert.NotNull(await svc.OpenAsync(Ctx(8), id, 8));
			Assert.True((await svc.SaveAsync(Ctx(7), new HospitalPatientInput { Id = id, Name = "عمر علي", NameEn = "Omar Ali", Sex = "M" }, 7, Now)).Ok);

			var log = await svc.AccessLogAsync(Ctx(7), id);
			Assert.Equal(new[] { "update", "open", "create" }, log.Select(a => a.Action).ToArray());
			Assert.Equal(new int?[] { 7, 8, 7 }, log.Select(a => a.EmployeeId).ToArray());
		}

		[Fact]
		public async Task Opening_another_companys_file_returns_nothing_and_logs_nothing()
		{
			using var host = new PlatformTestHost();
			var svc = Patients(host);
			var foreign = await RegisterAsync(svc, Ctx(9, CompanyTwo), Person("Khaled Nour"));
			Assert.Null(await svc.OpenAsync(Ctx(7), foreign, 7));
			Assert.Equal(0, await host.Db.HospitalPatientAccessLogs.CountAsync(a => a.Action == "open"));
		}

		// ---- platform registration ----------------------------------------------------------------

		[Fact]
		public void A_patient_is_a_conversation_subject_but_is_never_offered_in_the_record_picker()
		{
			using var host = new PlatformTestHost();
			var def = new EntityRegistry(host.Db).GetDefinitions().Single(d => d.Code == EntityRegistry.HospitalPatient);
			Assert.True(def.SupportsComments);
			Assert.False(def.ListedInRecordPicker);
			Assert.Equal(EntityRegistry.ScopeHospital, def.PermissionScope);
		}

		[Fact]
		public void Age_is_whole_years_and_turns_on_the_birthday()
		{
			var today = new DateTime(2026, 11, 15);
			Assert.Equal(36, HospitalPatientService.AgeOn(new DateTime(1990, 11, 15), today));
			Assert.Equal(35, HospitalPatientService.AgeOn(new DateTime(1990, 11, 16), today));
			Assert.Null(HospitalPatientService.AgeOn(null, today));
		}
	}
}
