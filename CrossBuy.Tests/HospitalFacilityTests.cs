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
	// HOSPITAL H1 — the facility policy and the building's rules.
	//
	// The denials matter most: a caller without `manage` writes nothing, another company's building
	// is invisible, a drawing with two beds on top of each other is refused, and a bed someone lies
	// in can never be drawn out of its room. Counts are deliberately ASYMMETRIC between companies,
	// so an inverted company predicate cannot pass by returning the same number from the wrong rows.
	// ==========================================================================================
	public class HospitalFacilityTests
	{
		private const int CompanyOne = 1, CompanyTwo = 2;

		private static BusinessContext Ctx(int employeeId, int companyId = CompanyOne) => new()
		{
			CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
			Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
		};

		private static HospitalAccessService Access(PlatformTestHost host) => new(
			new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
			NullLogger<HospitalAccessService>.Instance);

		private static HospitalFacilityService Facility(PlatformTestHost host) => new(host.Db, Access(host));

		private static async Task GrantAsync(PlatformTestHost host, int employeeId, string role, int companyId = CompanyOne)
		{
			host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
			{
				CompanyID = companyId, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee,
				PrincipalId = employeeId, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow,
			});
			await host.Seed.SaveChangesAsync();
		}

		/// A floor with the standard room types, ready for rooms. Returns the floor id.
		private static async Task<int> BuildingAsync(HospitalFacilityService svc, BusinessContext ctx, string floorCode = "F2")
		{
			Assert.True((await svc.SeedDefaultRoomTypesAsync(ctx, ctx.EmployeeId)).Ok);
			var floor = await svc.SaveFloorAsync(ctx, new HospitalFloorInput { Code = floorCode, Name = "الدور الثاني", NameEn = "Second floor" }, ctx.EmployeeId);
			Assert.True(floor.Ok);
			return floor.Id!.Value;
		}

		private static async Task<int> TypeIdAsync(PlatformTestHost host, string code, int companyId = CompanyOne)
			=> await host.Db.HospitalRoomTypes.Where(t => t.CompanyId == companyId && t.Code == code).Select(t => t.ID).SingleAsync();

		private static List<HospitalLayoutItem> Bed(string code, decimal x, decimal y, decimal w = 26, decimal h = 50)
			=> new() { new HospitalLayoutItem { Kind = "bed", Bed = code, X = x, Y = y, W = w, H = h } };

		// ---- policy ------------------------------------------------------------------------------

		[Fact]
		public void The_hospital_scope_is_registered_so_a_role_assignment_can_match_it()
		{
			Assert.Contains(EntityRegistry.ScopeHospital, EntityRegistry.PermissionScopes);
			Assert.True(EntityRegistry.IsKnownScope(EntityRegistry.ScopeHospital));
		}

		[Fact]
		public async Task Before_any_hospital_role_exists_the_module_is_open_so_a_new_hospital_can_be_configured()
		{
			using var host = new PlatformTestHost();
			var access = Access(host);
			Assert.True(await access.CanAsync(Ctx(7), HospitalActions.Read));
			Assert.True(await access.CanAsync(Ctx(7), HospitalActions.Manage));
		}

		[Fact]
		public async Task Once_roles_exist_manage_belongs_to_the_administrator_and_read_to_hospital_staff_only()
		{
			using var host = new PlatformTestHost();
			await GrantAsync(host, 10, HospitalRoles.HospitalAdministrator);
			await GrantAsync(host, 11, HospitalRoles.HospitalStaff);
			var access = Access(host);

			Assert.True(await access.CanAsync(Ctx(10), HospitalActions.Manage));
			Assert.True(await access.CanAsync(Ctx(10), HospitalActions.Read));
			Assert.False(await access.CanAsync(Ctx(11), HospitalActions.Manage));
			Assert.True(await access.CanAsync(Ctx(11), HospitalActions.Read));
			// An employee with no hospital role: neither, once the company has configured the module.
			Assert.False(await access.CanAsync(Ctx(12), HospitalActions.Read));
			Assert.False(await access.CanAsync(Ctx(12), HospitalActions.Manage));
			// An unknown action is refused, never treated as allowed. ("admit" was this example until H3 made
			// it a real action; "operate" belongs to a unit not built yet.)
			Assert.False(await access.CanAsync(Ctx(10), "operate"));
			// H2/H3 desk work: staff register and accommodate patients; a caller with no role does neither.
			Assert.True(await access.CanAsync(Ctx(11), HospitalActions.Register));
			Assert.True(await access.CanAsync(Ctx(11), HospitalActions.Admit));
			Assert.False(await access.CanAsync(Ctx(12), HospitalActions.Admit));
		}

		[Fact]
		public async Task A_target_in_another_company_is_refused_before_any_role_is_read()
		{
			using var host = new PlatformTestHost();
			var target = PermissionTarget.ForEntity(EntityRegistry.HospitalRoom, 1);
			target = new PermissionTarget { EntityType = target.EntityType, EntityId = target.EntityId, CompanyId = CompanyTwo };
			Assert.False(await Access(host).CanAsync(Ctx(7), HospitalActions.Read, target));
		}

		// ---- room types and rooms -------------------------------------------------------------------

		[Fact]
		public async Task The_standard_types_load_once_and_never_overwrite_a_catalogue()
		{
			using var host = new PlatformTestHost();
			var svc = Facility(host);
			Assert.True((await svc.SeedDefaultRoomTypesAsync(Ctx(7), 7)).Ok);
			Assert.Equal(6, await host.Db.HospitalRoomTypes.CountAsync(t => t.CompanyId == CompanyOne));
			var again = await svc.SeedDefaultRoomTypesAsync(Ctx(7), 7);
			Assert.False(again.Ok);
			Assert.Equal(6, await host.Db.HospitalRoomTypes.CountAsync(t => t.CompanyId == CompanyOne));
		}

		[Fact]
		public async Task A_new_room_gets_its_types_beds_and_the_first_free_place_on_its_floor()
		{
			using var host = new PlatformTestHost();
			var svc = Facility(host); var ctx = Ctx(7);
			int floor = await BuildingAsync(svc, ctx);

			var semi = await svc.CreateRoomAsync(ctx, new HospitalRoomInput { FloorId = floor, Number = "302", Kind = HospitalRoomKinds.Ward, RoomTypeId = await TypeIdAsync(host, "semi") }, 7);
			var bay = await svc.CreateRoomAsync(ctx, new HospitalRoomInput { FloorId = floor, Number = "304", Kind = HospitalRoomKinds.Ward, RoomTypeId = await TypeIdAsync(host, "ward4") }, 7);
			Assert.True(semi.Ok); Assert.True(bay.Ok);

			var beds302 = await host.Db.HospitalBeds.Where(b => b.RoomId == semi.Id).OrderBy(b => b.Code).Select(b => b.Code).ToListAsync();
			Assert.Equal(new[] { "302-A", "302-B" }, beds302);
			Assert.Equal(4, await host.Db.HospitalBeds.CountAsync(b => b.RoomId == bay.Id));

			// The second room does not sit on the first: it starts where the first one ends.
			var r1 = await host.Db.HospitalRooms.SingleAsync(r => r.ID == semi.Id);
			var r2 = await host.Db.HospitalRooms.SingleAsync(r => r.ID == bay.Id);
			Assert.Equal(1, r1.GridColumn); Assert.Equal(3, r1.ColumnSpan);
			Assert.Equal(4, r2.GridColumn); Assert.Equal(1, r2.GridRow);

			var duplicate = await svc.CreateRoomAsync(ctx, new HospitalRoomInput { FloorId = floor, Number = "302", Kind = HospitalRoomKinds.Ward, RoomTypeId = await TypeIdAsync(host, "private") }, 7);
			Assert.False(duplicate.Ok);
		}

		[Fact]
		public async Task Each_company_sees_only_its_own_building()
		{
			using var host = new PlatformTestHost();
			var svc = Facility(host);
			var one = Ctx(7, CompanyOne); var two = Ctx(8, CompanyTwo);
			int f1 = await BuildingAsync(svc, one);
			int f2 = await BuildingAsync(svc, two, "B1");
			await svc.CreateRoomAsync(one, new HospitalRoomInput { FloorId = f1, Number = "101", Kind = HospitalRoomKinds.Ward, RoomTypeId = await TypeIdAsync(host, "private") }, 7);
			await svc.CreateRoomAsync(one, new HospitalRoomInput { FloorId = f1, Number = "102", Kind = HospitalRoomKinds.Ward, RoomTypeId = await TypeIdAsync(host, "semi") }, 7);
			await svc.CreateRoomAsync(two, new HospitalRoomInput { FloorId = f2, Number = "901", Kind = HospitalRoomKinds.Ward, RoomTypeId = await TypeIdAsync(host, "ward4", CompanyTwo) }, 8);

			var b1 = await svc.BuildingAsync(one); var b2 = await svc.BuildingAsync(two);
			Assert.Equal(new[] { "101", "102" }, b1.SelectMany(f => f.Rooms).Select(r => r.Number).OrderBy(n => n));
			Assert.Equal(3, b1.SelectMany(f => f.Rooms).Sum(r => r.Beds.Count));
			Assert.Equal(new[] { "901" }, b2.SelectMany(f => f.Rooms).Select(r => r.Number));
			Assert.Equal(4, b2.SelectMany(f => f.Rooms).Sum(r => r.Beds.Count));

			// A room id from company one is "not found" to company two, whatever it asks.
			var foreign = b1.SelectMany(f => f.Rooms).First();
			Assert.Null(await svc.RoomAsync(two, foreign.Id));
			Assert.Equal("not_found", (await svc.SaveRoomLayoutAsync(two, foreign.Id, Bed("X", 10, 10), 8)).Error);
		}

		[Fact]
		public async Task Without_manage_nothing_is_written()
		{
			using var host = new PlatformTestHost();
			await GrantAsync(host, 10, HospitalRoles.HospitalAdministrator);
			await GrantAsync(host, 11, HospitalRoles.HospitalStaff);
			var svc = Facility(host);
			var staff = Ctx(11);

			Assert.Equal("forbidden", (await svc.SeedDefaultRoomTypesAsync(staff, 11)).Error);
			Assert.Equal("forbidden", (await svc.SaveFloorAsync(staff, new HospitalFloorInput { Code = "F1", Name = "x" }, 11)).Error);
			Assert.Equal(0, await host.Db.HospitalRoomTypes.CountAsync());
			Assert.Equal(0, await host.Db.HospitalFloors.CountAsync());
		}

		// ---- the drawing -------------------------------------------------------------------------------

		[Fact]
		public async Task A_drawing_with_overlapping_beds_is_refused_and_a_valid_one_adds_and_retires_beds()
		{
			using var host = new PlatformTestHost();
			var svc = Facility(host); var ctx = Ctx(7);
			int floor = await BuildingAsync(svc, ctx);
			var room = await svc.CreateRoomAsync(ctx, new HospitalRoomInput { FloorId = floor, Number = "302", Kind = HospitalRoomKinds.Ward, RoomTypeId = await TypeIdAsync(host, "semi") }, 7);
			int id = room.Id!.Value;

			// Two beds on top of each other.
			var clash = Bed("302-A", 10, 10).Concat(Bed("302-B", 20, 20)).ToList();
			var refused = await svc.SaveRoomLayoutAsync(ctx, id, clash, 7);
			Assert.False(refused.Ok);
			Assert.Null((await host.Db.HospitalRooms.AsNoTracking().SingleAsync(r => r.ID == id)).LayoutJson);

			// A bed over the bathroom is the same mistake.
			var overBath = Bed("302-A", 0, 60).Concat(new[] { new HospitalLayoutItem { Kind = "bath", X = 0, Y = 66, W = 22, H = 34 } }).ToList();
			Assert.False((await svc.SaveRoomLayoutAsync(ctx, id, overBath, 7)).Ok);

			// Keep 302-A, drop 302-B, add 302-C: B retires, C is created, the drawing is stored.
			var drawing = Bed("302-A", 10, 10).Concat(Bed("302-C", 60, 10)).ToList();
			Assert.True((await svc.SaveRoomLayoutAsync(ctx, id, drawing, 7)).Ok);
			var beds = await host.Db.HospitalBeds.AsNoTracking().Where(b => b.RoomId == id).ToListAsync();
			Assert.True(beds.Single(b => b.Code == "302-A").IsActive);
			Assert.False(beds.Single(b => b.Code == "302-B").IsActive);
			Assert.True(beds.Single(b => b.Code == "302-C").IsActive);
			Assert.Contains("302-C", (await host.Db.HospitalRooms.AsNoTracking().SingleAsync(r => r.ID == id)).LayoutJson);
		}

		[Fact]
		public async Task A_bed_someone_lies_in_is_never_drawn_out_of_its_room()
		{
			using var host = new PlatformTestHost();
			var svc = Facility(host); var ctx = Ctx(7);
			int floor = await BuildingAsync(svc, ctx);
			var room = await svc.CreateRoomAsync(ctx, new HospitalRoomInput { FloorId = floor, Number = "302", Kind = HospitalRoomKinds.Ward, RoomTypeId = await TypeIdAsync(host, "semi") }, 7);
			int id = room.Id!.Value;

			var b = await host.Db.HospitalBeds.SingleAsync(x => x.Code == "302-B");
			b.Status = HospitalBedStatus.Occupied; await host.Db.SaveChangesAsync();

			var result = await svc.SaveRoomLayoutAsync(ctx, id, Bed("302-A", 10, 10), 7);
			Assert.False(result.Ok);
			Assert.Equal("Bed {0} holds a patient and cannot be removed", result.Error);
			Assert.Equal("302-B", result.Args![0]);
			Assert.True((await host.Db.HospitalBeds.AsNoTracking().SingleAsync(x => x.Code == "302-B")).IsActive);

			// Housekeeping cannot flip an occupied bed either.
			Assert.False((await svc.SetBedStatusAsync(ctx, b.ID, HospitalBedStatus.Cleaning, 7)).Ok);
		}

		[Fact]
		public async Task The_floor_plan_refuses_overlapping_rooms_and_anything_off_the_twelve_columns()
		{
			using var host = new PlatformTestHost();
			var svc = Facility(host); var ctx = Ctx(7);
			int floor = await BuildingAsync(svc, ctx);
			var a = await svc.CreateRoomAsync(ctx, new HospitalRoomInput { FloorId = floor, Number = "301", Kind = HospitalRoomKinds.Ward, RoomTypeId = await TypeIdAsync(host, "private") }, 7);
			var b = await svc.CreateRoomAsync(ctx, new HospitalRoomInput { FloorId = floor, Number = "302", Kind = HospitalRoomKinds.Ward, RoomTypeId = await TypeIdAsync(host, "private") }, 7);

			// Moving 302 onto 301.
			var onTop = await svc.SaveFloorPlanAsync(ctx, floor, new[] { new HospitalRoomPlacement { RoomId = b.Id!.Value, Column = 1, Row = 1, ColumnSpan = 2, RowSpan = 1 } }, 7);
			Assert.False(onTop.Ok);
			// Past the twelfth column.
			var off = await svc.SaveFloorPlanAsync(ctx, floor, new[] { new HospitalRoomPlacement { RoomId = b.Id!.Value, Column = 12, Row = 1, ColumnSpan = 2, RowSpan = 1 } }, 7);
			Assert.False(off.Ok);
			// A real move is kept.
			Assert.True((await svc.SaveFloorPlanAsync(ctx, floor, new[] { new HospitalRoomPlacement { RoomId = b.Id!.Value, Column = 5, Row = 2, ColumnSpan = 3, RowSpan = 2 } }, 7)).Ok);
			var moved = await host.Db.HospitalRooms.AsNoTracking().SingleAsync(r => r.ID == b.Id);
			Assert.Equal((5, 2, 3, 2), (moved.GridColumn, moved.GridRow, moved.ColumnSpan, moved.RowSpan));
			Assert.NotNull(a.Id);
		}

		[Fact]
		public void A_space_without_beds_cannot_be_drawn_with_one()
		{
			var refusal = HospitalFacilityService.ValidateLayout(Bed("C-1", 10, 10), carriesBeds: false);
			Assert.NotNull(refusal);
			Assert.Equal("This space does not carry beds", refusal!.Error);
			Assert.Null(HospitalFacilityService.ValidateLayout(new[] { new HospitalLayoutItem { Kind = "sink", X = 5, Y = 5, W = 10, H = 10 } }, carriesBeds: false));
		}
	}
}
