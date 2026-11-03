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
	// HOSPITAL H3 — accommodation.
	//
	// What must never happen: two patients in one bed, one patient in two beds, a man in a women's
	// ward or in a same-sex room a woman already shares, another company's patient or bed reached by
	// id, or a caller without `admit` moving anyone. And the bed must always tell the truth: Occupied
	// while someone lies in it, Cleaning once they leave.
	// ==========================================================================================
	public class HospitalAdmissionTests
	{
		private const int CompanyOne = 1, CompanyTwo = 2;
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);

		private static BusinessContext Ctx(int employeeId, int companyId = CompanyOne) => new()
		{
			CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
			Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
		};

		private sealed class World
		{
			public required PlatformTestHost Host;
			public required HospitalFacilityService Facility;
			public required HospitalPatientService Patients;
			public required HospitalAdmissionService Admissions;
		}

		private static World Make(PlatformTestHost host)
		{
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			return new World { Host = host, Facility = new(host.Db, access), Patients = new(host.Db, access), Admissions = new(host.Db, access) };
		}

		private static async Task<int> PatientAsync(World w, string en, string sex, int companyId = CompanyOne)
		{
			var r = await w.Patients.SaveAsync(Ctx(7, companyId), new HospitalPatientInput { Name = "م " + en, NameEn = en, Sex = sex }, 7, Now);
			Assert.True(r.Ok, r.Error);
			return r.Id!.Value;
		}

		/// A floor with: a men's ward holding room 101 (private, 1 bed), a mixed ward holding room 102
		/// (semi, 2 beds, same-sex type) and room 103 (ward4, 4 beds). Returns bed ids by code.
		private static async Task<Dictionary<string, int>> BuildingAsync(World w, int companyId = CompanyOne)
		{
			var ctx = Ctx(7, companyId);
			Assert.True((await w.Facility.SeedDefaultRoomTypesAsync(ctx, 7)).Ok);
			var floor = (await w.Facility.SaveFloorAsync(ctx, new HospitalFloorInput { Code = "F1", Name = "الأول" }, 7)).Id!.Value;
			var men = (await w.Facility.SaveWardAsync(ctx, new HospitalWardInput { FloorId = floor, Code = "W1", Name = "رجال", SexPolicy = HospitalSexPolicy.Male }, 7)).Id!.Value;
			var mixed = (await w.Facility.SaveWardAsync(ctx, new HospitalWardInput { FloorId = floor, Code = "W2", Name = "مختلط", SexPolicy = HospitalSexPolicy.Any }, 7)).Id!.Value;
			async Task<int> Type(string code) => await w.Host.Db.HospitalRoomTypes.Where(t => t.CompanyId == companyId && t.Code == code).Select(t => t.ID).SingleAsync();
			Assert.True((await w.Facility.CreateRoomAsync(ctx, new HospitalRoomInput { FloorId = floor, WardId = men, RoomTypeId = await Type("private"), Number = "101" }, 7)).Ok);
			Assert.True((await w.Facility.CreateRoomAsync(ctx, new HospitalRoomInput { FloorId = floor, WardId = mixed, RoomTypeId = await Type("semi"), Number = "102" }, 7)).Ok);
			Assert.True((await w.Facility.CreateRoomAsync(ctx, new HospitalRoomInput { FloorId = floor, WardId = mixed, RoomTypeId = await Type("ward4"), Number = "103" }, 7)).Ok);
			return await w.Host.Db.HospitalBeds.Where(b => b.CompanyId == companyId).ToDictionaryAsync(b => b.Code, b => b.ID);
		}

		private static async Task<string> BedStatusAsync(World w, int bedId)
			=> await w.Host.Db.HospitalBeds.AsNoTracking().Where(b => b.ID == bedId).Select(b => b.Status).SingleAsync();

		[Fact]
		public async Task Admitting_fills_the_bed_and_shows_the_patient_in_it()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var beds = await BuildingAsync(w);
			var omar = await PatientAsync(w, "Omar Ali", HospitalPatientSex.Male);

			var r = await w.Admissions.AdmitAsync(Ctx(7), new HospitalAdmitInput { PatientId = omar, BedId = beds["101"], Diagnosis = "Pneumonia" }, 7, Now);
			Assert.True(r.Ok, r.Error);
			Assert.Equal(HospitalBedStatus.Occupied, await BedStatusAsync(w, beds["101"]));
			var occupant = Assert.Single(await w.Admissions.OccupantsAsync(Ctx(7)));
			Assert.Equal((beds["101"], omar, "Pneumonia"), (occupant.Key, occupant.Value.PatientId, occupant.Value.Diagnosis));
			Assert.Equal(("101", "101"), ((await w.Admissions.CurrentAsync(Ctx(7), omar))!.Value.RoomNumber, (await w.Admissions.CurrentAsync(Ctx(7), omar))!.Value.BedCode));
		}

		[Fact]
		public async Task A_patient_cannot_be_in_two_beds_and_a_bed_cannot_hold_two_patients()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var beds = await BuildingAsync(w);
			var omar = await PatientAsync(w, "Omar Ali", HospitalPatientSex.Male);
			var khaled = await PatientAsync(w, "Khaled Nour", HospitalPatientSex.Male);
			Assert.True((await w.Admissions.AdmitAsync(Ctx(7), new HospitalAdmitInput { PatientId = omar, BedId = beds["103-A"] }, 7, Now)).Ok);

			Assert.Equal("This patient is already admitted", (await w.Admissions.AdmitAsync(Ctx(7), new HospitalAdmitInput { PatientId = omar, BedId = beds["103-B"] }, 7, Now)).Error);
			var taken = await w.Admissions.AdmitAsync(Ctx(7), new HospitalAdmitInput { PatientId = khaled, BedId = beds["103-A"] }, 7, Now);
			Assert.Equal("Bed {0} is not vacant", taken.Error);
			Assert.Equal(1, await host.Db.HospitalAdmissions.CountAsync());
			Assert.Equal(1, await host.Db.HospitalBedStays.CountAsync());
		}

		[Fact]
		public async Task A_ward_for_men_refuses_a_woman_and_a_same_sex_room_refuses_the_other_sex()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var beds = await BuildingAsync(w);
			var sara = await PatientAsync(w, "Sara Hassan", HospitalPatientSex.Female);
			var mona = await PatientAsync(w, "Mona Adel", HospitalPatientSex.Female);
			var omar = await PatientAsync(w, "Omar Ali", HospitalPatientSex.Male);

			Assert.Equal("Ward {0} takes the other sex", (await w.Admissions.AdmitAsync(Ctx(7), new HospitalAdmitInput { PatientId = sara, BedId = beds["101"] }, 7, Now)).Error);

			Assert.True((await w.Admissions.AdmitAsync(Ctx(7), new HospitalAdmitInput { PatientId = sara, BedId = beds["102-A"] }, 7, Now)).Ok);
			Assert.Equal("Room {0} is shared with the other sex", (await w.Admissions.AdmitAsync(Ctx(7), new HospitalAdmitInput { PatientId = omar, BedId = beds["102-B"] }, 7, Now)).Error);
			Assert.True((await w.Admissions.AdmitAsync(Ctx(7), new HospitalAdmitInput { PatientId = mona, BedId = beds["102-B"] }, 7, Now)).Ok);

			// The suitable list is the same rules, read: no women's beds for Omar in 102, no 101 for Sara.
			var forOmar = await w.Admissions.SuitableBedsAsync(Ctx(7), omar);
			Assert.DoesNotContain(forOmar, b => b.RoomNumber == "102");
			Assert.Contains(forOmar, b => b.BedCode == "101");
			Assert.Equal(4, forOmar.Count(b => b.RoomNumber == "103"));
		}

		[Fact]
		public async Task A_transfer_closes_one_stay_opens_the_next_and_sends_the_old_bed_to_cleaning()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var beds = await BuildingAsync(w);
			var omar = await PatientAsync(w, "Omar Ali", HospitalPatientSex.Male);
			var adm = (await w.Admissions.AdmitAsync(Ctx(7), new HospitalAdmitInput { PatientId = omar, BedId = beds["103-A"] }, 7, Now)).Id!.Value;

			Assert.True((await w.Admissions.TransferAsync(Ctx(7), adm, beds["101"], 7, Now.AddHours(5))).Ok);
			Assert.Equal(HospitalBedStatus.Cleaning, await BedStatusAsync(w, beds["103-A"]));
			Assert.Equal(HospitalBedStatus.Occupied, await BedStatusAsync(w, beds["101"]));
			var stays = await w.Admissions.StaysAsync(Ctx(7), omar);
			Assert.Equal(new[] { "101", "103-A" }, stays.Select(s => s.BedCode).ToArray());
			Assert.Equal(HospitalStayEndReasons.Transfer, stays[1].EndReason);
			Assert.Null(stays[0].EndedAt);
			Assert.Equal("The patient is already in this bed", (await w.Admissions.TransferAsync(Ctx(7), adm, beds["101"], 7, Now.AddHours(6))).Error);
		}

		[Fact]
		public async Task Discharge_empties_the_bed_into_cleaning_and_closes_the_admission()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var beds = await BuildingAsync(w);
			var omar = await PatientAsync(w, "Omar Ali", HospitalPatientSex.Male);
			var adm = (await w.Admissions.AdmitAsync(Ctx(7), new HospitalAdmitInput { PatientId = omar, BedId = beds["101"] }, 7, Now)).Id!.Value;

			Assert.True((await w.Admissions.DischargeAsync(Ctx(8), adm, 8, Now.AddDays(3))).Ok);
			Assert.Equal(HospitalBedStatus.Cleaning, await BedStatusAsync(w, beds["101"]));
			var a = await host.Db.HospitalAdmissions.AsNoTracking().SingleAsync();
			Assert.Equal((HospitalAdmissionStatus.Discharged, (int?)8), (a.Status, a.DischargedByEmployeeId));
			Assert.Empty(await w.Admissions.OccupantsAsync(Ctx(7)));
			Assert.Null(await w.Admissions.CurrentAsync(Ctx(7), omar));
			// A bed in cleaning cannot take a patient until housekeeping marks it vacant.
			Assert.Equal("Bed {0} is not vacant", (await w.Admissions.AdmitAsync(Ctx(7), new HospitalAdmitInput { PatientId = omar, BedId = beds["101"] }, 7, Now.AddDays(3))).Error);
			Assert.Equal("not_found", (await w.Admissions.DischargeAsync(Ctx(7), adm, 7, Now.AddDays(4))).Error);
		}

		[Fact]
		public async Task Housekeeping_cannot_mark_an_occupied_bed_vacant()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var beds = await BuildingAsync(w);
			var omar = await PatientAsync(w, "Omar Ali", HospitalPatientSex.Male);
			Assert.True((await w.Admissions.AdmitAsync(Ctx(7), new HospitalAdmitInput { PatientId = omar, BedId = beds["101"] }, 7, Now)).Ok);
			Assert.Equal("Bed {0} holds a patient", (await w.Facility.SetBedStatusAsync(Ctx(7), beds["101"], HospitalBedStatus.Vacant, 7)).Error);
		}

		[Fact]
		public async Task Another_companys_patient_or_bed_is_not_found_and_never_offered()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var mine = await BuildingAsync(w);
			var theirs = await BuildingAsync(w, CompanyTwo);
			var omar = await PatientAsync(w, "Omar Ali", HospitalPatientSex.Male);
			var foreign = await PatientAsync(w, "Khaled Nour", HospitalPatientSex.Male, CompanyTwo);

			Assert.Equal("not_found", (await w.Admissions.AdmitAsync(Ctx(7), new HospitalAdmitInput { PatientId = omar, BedId = theirs["101"] }, 7, Now)).Error);
			Assert.Equal("not_found", (await w.Admissions.AdmitAsync(Ctx(7), new HospitalAdmitInput { PatientId = foreign, BedId = mine["101"] }, 7, Now)).Error);
			Assert.DoesNotContain(await w.Admissions.SuitableBedsAsync(Ctx(7), omar), b => theirs.ContainsValue(b.BedId));
			Assert.Empty(await w.Admissions.SuitableBedsAsync(Ctx(7), foreign));

			var theirAdm = (await w.Admissions.AdmitAsync(Ctx(9, CompanyTwo), new HospitalAdmitInput { PatientId = foreign, BedId = theirs["103-A"] }, 9, Now)).Id!.Value;
			Assert.Empty(await w.Admissions.OccupantsAsync(Ctx(7)));
			Assert.Equal("not_found", (await w.Admissions.DischargeAsync(Ctx(7), theirAdm, 7, Now)).Error);
			Assert.Equal(HospitalBedStatus.Occupied, await BedStatusAsync(w, theirs["103-A"]));
		}

		[Fact]
		public async Task A_caller_without_admit_moves_nobody()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var beds = await BuildingAsync(w);
			var omar = await PatientAsync(w, "Omar Ali", HospitalPatientSex.Male);
			host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
			{
				CompanyID = CompanyOne, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee,
				PrincipalId = 1, Role = HospitalRoles.HospitalAdministrator, IsActive = true, CreatedAt = DateTime.UtcNow,
			});
			await host.Seed.SaveChangesAsync();

			Assert.Equal("forbidden", (await w.Admissions.AdmitAsync(Ctx(3), new HospitalAdmitInput { PatientId = omar, BedId = beds["101"] }, 3, Now)).Error);
			Assert.Equal(0, await host.Db.HospitalAdmissions.CountAsync());
			Assert.Equal(HospitalBedStatus.Vacant, await BedStatusAsync(w, beds["101"]));
		}
	}
}
