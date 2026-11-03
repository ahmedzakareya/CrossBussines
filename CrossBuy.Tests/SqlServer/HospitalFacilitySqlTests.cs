using CrossBuy.Models.Context.Hospital;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
	// Hospital H1 slice against a REAL SQL Server: the shipped deploy/sql script — not the EF model —
	// builds the tables, twice (idempotency), and the EF mapping then reads and writes them. A column
	// the entity has and the script forgot fails here as `Invalid column name`, which the SQLite tests
	// cannot see. Runs on a disposable probe database; skipped, never silently passed, without
	// CROSSBUY_TEST_SQL.
	[Collection(SqlServerCollection.Name)]
	public class HospitalFacilitySqlTests : IAsyncLifetime
	{
		private readonly SqlServerFixture _sql;
		private SqlServerFixture.ProbeDatabase? _probe;
		public HospitalFacilitySqlTests(SqlServerFixture sql) { _sql = sql; }

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			// No model schema: the SLICE is what creates the tables under test.
			_probe = await _sql.CreateProbeDatabaseAsync("Hospital", applyModelSchema: false);
		}

		public async Task DisposeAsync()
		{
			if (_probe != null) await _sql.DropProbeDatabaseAsync(_probe);
		}

		private async Task ApplySliceAsync()
		{
			await using var c = new SqlConnection(_probe!.ConnectionString);
			await c.OpenAsync();
			await _sql.RunScriptAsync(c, "hospital_h1_facility.sql");
		}

		[SkippableFact]
		public async Task The_slice_runs_twice_and_the_entities_round_trip_through_it()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			await ApplySliceAsync();
			await ApplySliceAsync();   // idempotent: the second pass changes nothing and does not throw

			await using (var db = _sql.ContextFor(_probe!))
			{
				var type = new HospitalRoomType { CompanyId = 1, Code = "semi", Name = "غرفة مزدوجة", NameEn = "Semi-private room", BedCount = 2, DailyRate = 750.5m, Features = "bath,oxygen", SameSexOnly = true, CreatedAt = DateTime.UtcNow };
				var floor = new HospitalFloor { CompanyId = 1, Code = "F3", Name = "الدور الثالث", CreatedAt = DateTime.UtcNow };
				db.AddRange(type, floor);
				await db.SaveChangesAsync();
				var ward = new HospitalWard { CompanyId = 1, FloorId = floor.ID, Code = "W3", Name = "جناح ٣", SexPolicy = HospitalSexPolicy.Male, CreatedAt = DateTime.UtcNow };
				db.Add(ward);
				await db.SaveChangesAsync();
				var room = new HospitalRoom { CompanyId = 1, FloorId = floor.ID, WardId = ward.ID, RoomTypeId = type.ID, Number = "302", Kind = HospitalRoomKinds.Ward, GridColumn = 4, GridRow = 2, ColumnSpan = 3, RowSpan = 1, LayoutJson = "[{\"k\":\"bed\",\"x\":14,\"y\":10,\"w\":26,\"h\":50,\"bed\":\"302-A\"}]", CreatedAt = DateTime.UtcNow };
				db.Add(room);
				await db.SaveChangesAsync();
				db.Add(new HospitalBed { CompanyId = 1, RoomId = room.ID, Code = "302-A", CreatedAt = DateTime.UtcNow });
				await db.SaveChangesAsync();
			}

			// Re-read from a NEW context: the database, not the tracker, is the witness.
			await using (var db = _sql.ContextFor(_probe!))
			{
				var room = await db.HospitalRooms.AsNoTracking().SingleAsync(r => r.Number == "302");
				Assert.Equal((4, 2, 3, 1), (room.GridColumn, room.GridRow, room.ColumnSpan, room.RowSpan));
				Assert.Contains("302-A", room.LayoutJson);
				Assert.Equal(750.5m, (await db.HospitalRoomTypes.AsNoTracking().SingleAsync()).DailyRate);
				Assert.Equal(HospitalBedStatus.Vacant, (await db.HospitalBeds.AsNoTracking().SingleAsync()).Status);
				Assert.Equal("غرفة مزدوجة", (await db.HospitalRoomTypes.AsNoTracking().SingleAsync()).Name);
			}
		}

		[SkippableFact]
		public async Task The_database_itself_refuses_a_drawing_that_is_not_json_and_a_room_off_the_grid()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			await ApplySliceAsync();
			await using var c = new SqlConnection(_probe!.ConnectionString);
			await c.OpenAsync();

			async Task<bool> Refused(string sql)
			{
				try { await using var cmd = new SqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); return false; }
				catch (SqlException ex) when (ex.Number == 547) { return true; }   // CHECK constraint
			}

			Assert.True(await Refused("INSERT dbo.HospitalRooms (CompanyId, FloorId, Number, LayoutJson) VALUES (1, 1, 'X1', 'not json');"));
			Assert.True(await Refused("INSERT dbo.HospitalRooms (CompanyId, FloorId, Number, GridColumn, ColumnSpan) VALUES (1, 1, 'X2', 12, 2);"));
			Assert.True(await Refused("INSERT dbo.HospitalBeds (CompanyId, RoomId, Code, Status) VALUES (1, 1, 'B1', 'Sleeping');"));
			Assert.False(await Refused("INSERT dbo.HospitalRooms (CompanyId, FloorId, Number, LayoutJson) VALUES (1, 1, 'X3', '[]');"));
		}
	}
}
