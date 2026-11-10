using CrossBuy.BL;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL FACILITY SERVICE — the one place the building is read and changed.
	//
	// Every read and write names the caller's company in its WHERE clause (these tables have no
	// global filter — see HospitalFacility.cs), and every write asks HospitalAccessService for
	// `manage` itself, so a second caller cannot reach a write through a forgotten attribute.
	//
	// The rules the drawing must keep are enforced HERE, not only in the designer's JavaScript:
	//   · a bed someone lies in (Occupied / Reserved) is never deleted or drawn out of its room;
	//   · two beds never overlap, and a bed never overlaps a bathroom, anteroom, lounge or
	//     companion bed;
	//   · two rooms never share a cell of their floor's grid, and nothing leaves the 12 columns.
	// ==========================================================================================

	/// Error is a resource KEY (English text, "{0}" placeholders) and Args fill it, so the controller can
	/// translate a refusal that names a bed or a room instead of passing a composed English sentence.
	public sealed record HospitalResult(bool Ok, string? Error = null, int? Id = null, object[]? Args = null)
	{
		public static HospitalResult Success(int? id = null) => new(true, null, id);
		public static HospitalResult Fail(string error, params object[] args) => new(false, error, null, args);
	}

	public sealed class HospitalRoomTypeInput
	{
		public int? Id { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int BedCount { get; set; } = 1;
		public decimal DailyRate { get; set; }
		public List<string> Features { get; set; } = new();
		public bool SameSexOnly { get; set; }
		public int SortOrder { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed class HospitalFloorInput
	{
		public int? Id { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int SortOrder { get; set; }
	}

	public sealed class HospitalWardInput
	{
		public int? Id { get; set; }
		public int FloorId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string SexPolicy { get; set; } = HospitalSexPolicy.Any;
	}

	public sealed class HospitalRoomInput
	{
		public int FloorId { get; set; }
		public int? WardId { get; set; }
		public int? RoomTypeId { get; set; }
		public string Number { get; set; } = "";
		public string? Name { get; set; }
		public string? NameEn { get; set; }
		public string Kind { get; set; } = HospitalRoomKinds.Ward;
	}

	public sealed class HospitalRoomPlacement
	{
		public int RoomId { get; set; }
		public int Column { get; set; }
		public int Row { get; set; }
		public int ColumnSpan { get; set; }
		public int RowSpan { get; set; }
	}

	/// One drawn thing inside a room, in percent of the room. `Bed` names the bed's code.
	public sealed class HospitalLayoutItem
	{
		[JsonPropertyName("k")] public string Kind { get; set; } = "";
		[JsonPropertyName("x")] public decimal X { get; set; }
		[JsonPropertyName("y")] public decimal Y { get; set; }
		[JsonPropertyName("w")] public decimal W { get; set; }
		[JsonPropertyName("h")] public decimal H { get; set; }
		[JsonPropertyName("bed")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Bed { get; set; }
	}

	public static class HospitalLayoutKinds
	{
		public const string Bed = "bed";
		public static readonly IReadOnlyList<string> All = new[]
			{ "bed", "bath", "door", "window", "companion", "lounge", "curtain", "anteroom", "sink" };
		/// Things a bed may not stand on.
		public static readonly IReadOnlyList<string> Solid = new[] { "bed", "bath", "anteroom", "lounge", "companion" };
	}

	// ---- read models ------------------------------------------------------------------------
	public sealed record HospitalBedView(int Id, string Code, string Status);
	public sealed record HospitalRoomView(
		int Id, int FloorId, int? WardId, string Number, string? Name, string? NameEn, string Kind,
		int Column, int Row, int ColumnSpan, int RowSpan, string? LayoutJson,
		HospitalRoomType? Type, IReadOnlyList<HospitalBedView> Beds);
	public sealed record HospitalWardView(int Id, string Code, string Name, string? NameEn, string SexPolicy);
	public sealed record HospitalFloorView(
		int Id, string Code, string Name, string? NameEn, int SortOrder,
		IReadOnlyList<HospitalWardView> Wards, IReadOnlyList<HospitalRoomView> Rooms);

	/// The money every hospital price is shown in: the company's default currency (Companies.DefaultCurrencyId),
	/// with its symbol for an Arabic page and its ISO code for an English one.
	public sealed record HospitalCurrency(string Code, string? Symbol, int Decimals)
	{
		public string Label(bool arabic) => arabic && !string.IsNullOrWhiteSpace(Symbol) ? Symbol! : Code;
		public string Format(decimal amount) => amount.ToString("N" + Decimals, System.Globalization.CultureInfo.InvariantCulture);
	}

	public sealed class HospitalFacilityService
	{
		private const int GridColumns = 12;
		private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		public HospitalFacilityService(CrossDbContext db, IHospitalAccessService access)
		{ _db = db; _access = access; }

		private Task<bool> CanManageAsync(BusinessContext ctx, CancellationToken ct)
			=> _access.CanAsync(ctx, HospitalActions.Manage, null, ct);

		// ====================================================================================
		// READS
		// ====================================================================================

		public async Task<HospitalCurrency?> CurrencyAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var id = await _db.Companies.AsNoTracking().Where(c => c.CompanyID == ctx.CompanyId).Select(c => c.DefaultCurrencyId).FirstOrDefaultAsync(ct);
			if (id is not int cid) return null;
			return await _db.Currencies.AsNoTracking().Where(c => c.ID == cid)
				.Select(c => new HospitalCurrency(c.Code, c.Symbol, c.DecimalPlaces)).FirstOrDefaultAsync(ct);
		}

		public async Task<IReadOnlyList<HospitalRoomType>> RoomTypesAsync(BusinessContext ctx, CancellationToken ct = default)
			=> await _db.HospitalRoomTypes.AsNoTracking()
				.Where(t => t.CompanyId == ctx.CompanyId)
				.OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
				.ToListAsync(ct);

		/// The whole building in one read: floors → wards and rooms → beds. Four queries, grouped in
		/// memory, whatever the size of the hospital.
		public async Task<IReadOnlyList<HospitalFloorView>> BuildingAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			int company = ctx.CompanyId;
			var floors = await _db.HospitalFloors.AsNoTracking().Where(f => f.CompanyId == company && f.IsActive)
				.OrderBy(f => f.SortOrder).ThenBy(f => f.Code).ToListAsync(ct);
			var wards = await _db.HospitalWards.AsNoTracking().Where(w => w.CompanyId == company && w.IsActive).ToListAsync(ct);
			var rooms = await _db.HospitalRooms.AsNoTracking().Where(r => r.CompanyId == company && r.IsActive)
				.OrderBy(r => r.Number).ToListAsync(ct);
			var beds = await _db.HospitalBeds.AsNoTracking().Where(b => b.CompanyId == company && b.IsActive)
				.OrderBy(b => b.Code).ToListAsync(ct);
			var types = (await RoomTypesAsync(ctx, ct)).ToDictionary(t => t.ID);

			var bedsByRoom = beds.GroupBy(b => b.RoomId).ToDictionary(g => g.Key, g => g.Select(b => new HospitalBedView(b.ID, b.Code, b.Status)).ToList());
			return floors.Select(f => new HospitalFloorView(
				f.ID, f.Code, f.Name, f.NameEn, f.SortOrder,
				wards.Where(w => w.FloorId == f.ID).Select(w => new HospitalWardView(w.ID, w.Code, w.Name, w.NameEn, w.SexPolicy)).ToList(),
				rooms.Where(r => r.FloorId == f.ID).Select(r => ToView(r, types, bedsByRoom)).ToList())).ToList();
		}

		public async Task<HospitalRoomView?> RoomAsync(BusinessContext ctx, int roomId, CancellationToken ct = default)
		{
			var r = await _db.HospitalRooms.AsNoTracking()
				.FirstOrDefaultAsync(x => x.ID == roomId && x.CompanyId == ctx.CompanyId && x.IsActive, ct);
			if (r == null) return null;
			var types = (await RoomTypesAsync(ctx, ct)).ToDictionary(t => t.ID);
			var beds = await _db.HospitalBeds.AsNoTracking()
				.Where(b => b.CompanyId == ctx.CompanyId && b.RoomId == roomId && b.IsActive)
				.OrderBy(b => b.Code).Select(b => new HospitalBedView(b.ID, b.Code, b.Status)).ToListAsync(ct);
			return ToView(r, types, new Dictionary<int, List<HospitalBedView>> { [roomId] = beds });
		}

		private static HospitalRoomView ToView(HospitalRoom r, IReadOnlyDictionary<int, HospitalRoomType> types,
			IReadOnlyDictionary<int, List<HospitalBedView>> beds)
			=> new(r.ID, r.FloorId, r.WardId, r.Number, r.Name, r.NameEn, r.Kind,
				r.GridColumn, r.GridRow, r.ColumnSpan, r.RowSpan, r.LayoutJson,
				r.RoomTypeId is int t && types.TryGetValue(t, out var type) ? type : null,
				beds.TryGetValue(r.ID, out var list) ? list : new List<HospitalBedView>());

		// ====================================================================================
		// ROOM TYPES
		// ====================================================================================

		/// The six types the prototype was designed around, so a new hospital starts from a working
		/// catalogue instead of an empty page. Inserts only into an EMPTY catalogue; never overwrites.
		public async Task<HospitalResult> SeedDefaultRoomTypesAsync(BusinessContext ctx, int? employeeId, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (await _db.HospitalRoomTypes.AnyAsync(t => t.CompanyId == ctx.CompanyId, ct))
				return HospitalResult.Fail("The room type catalogue is not empty");

			var now = DateTime.UtcNow;
			var defaults = new (string code, string ar, string en, int beds, string feat, bool sameSex)[]
			{
				("vip", "جناح VIP", "VIP suite", 1, "bath,companion,lounge,tv,fridge,oxygen", false),
				("private", "غرفة خاصة", "Private room", 1, "bath,companion,tv,oxygen", false),
				("semi", "غرفة مزدوجة", "Semi-private room", 2, "bath,tv,oxygen", true),
				("ward4", "عنبر ٤ أسرّة", "Four-bed bay", 4, "oxygen", true),
				("isolation", "غرفة عزل (ضغط سالب)", "Isolation room (negative pressure)", 1, "bath,negative-pressure,anteroom,oxygen", false),
				("icu", "عناية مركزة — صالة مفتوحة", "ICU — open bay", 4, "monitor,oxygen", false),
			};
			int order = 0;
			foreach (var d in defaults)
				_db.HospitalRoomTypes.Add(new HospitalRoomType
				{
					CompanyId = ctx.CompanyId, Code = d.code, Name = d.ar, NameEn = d.en, BedCount = d.beds,
					Features = d.feat, SameSexOnly = d.sameSex, SortOrder = ++order, CreatedAt = now, CreatedByEmployeeId = employeeId,
				});
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success();
		}

		public async Task<HospitalResult> SaveRoomTypeAsync(BusinessContext ctx, HospitalRoomTypeInput input, int? employeeId, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var code = (input.Code ?? "").Trim();
			var name = (input.Name ?? "").Trim();
			if (code.Length == 0 || code.Length > 40) return HospitalResult.Fail("Enter a code of up to 40 characters");
			if (name.Length == 0) return HospitalResult.Fail("Enter the Arabic name");
			if (input.BedCount is < 1 or > 20) return HospitalResult.Fail("A room type holds between 1 and 20 beds");
			if (input.DailyRate < 0) return HospitalResult.Fail("The daily rate cannot be negative");
			var unknown = input.Features.FirstOrDefault(f => !HospitalRoomFeatures.IsKnown(f));
			if (unknown != null) return HospitalResult.Fail("Unknown room feature: {0}", unknown);
			int self = input.Id ?? 0;
			if (await _db.HospitalRoomTypes.AnyAsync(t => t.CompanyId == ctx.CompanyId && t.Code == code && t.ID != self, ct))
				return HospitalResult.Fail("Another room type already uses this code");

			HospitalRoomType? row;
			if (input.Id is int id)
			{
				row = await _db.HospitalRoomTypes.FirstOrDefaultAsync(t => t.ID == id && t.CompanyId == ctx.CompanyId, ct);
				if (row == null) return HospitalResult.Fail("not_found");
				row.UpdatedAt = DateTime.UtcNow; row.UpdatedByEmployeeId = employeeId;
			}
			else
			{
				row = new HospitalRoomType { CompanyId = ctx.CompanyId, CreatedAt = DateTime.UtcNow, CreatedByEmployeeId = employeeId };
				_db.HospitalRoomTypes.Add(row);
			}
			row.Code = code; row.Name = name; row.NameEn = string.IsNullOrWhiteSpace(input.NameEn) ? null : input.NameEn.Trim();
			row.BedCount = input.BedCount; row.DailyRate = input.DailyRate; row.SameSexOnly = input.SameSexOnly;
			row.Features = input.Features.Count == 0 ? null : string.Join(",", input.Features.Distinct(StringComparer.Ordinal));
			row.SortOrder = input.SortOrder; row.IsActive = input.IsActive;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		// ====================================================================================
		// FLOORS AND WARDS
		// ====================================================================================

		public async Task<HospitalResult> SaveFloorAsync(BusinessContext ctx, HospitalFloorInput input, int? employeeId, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var code = (input.Code ?? "").Trim();
			if (code.Length == 0 || code.Length > 40) return HospitalResult.Fail("Enter a code of up to 40 characters");
			if (string.IsNullOrWhiteSpace(input.Name)) return HospitalResult.Fail("Enter the Arabic name");
			int self = input.Id ?? 0;
			if (await _db.HospitalFloors.AnyAsync(f => f.CompanyId == ctx.CompanyId && f.Code == code && f.ID != self, ct))
				return HospitalResult.Fail("Another floor already uses this code");

			HospitalFloor? row;
			if (input.Id is int id)
			{
				row = await _db.HospitalFloors.FirstOrDefaultAsync(f => f.ID == id && f.CompanyId == ctx.CompanyId, ct);
				if (row == null) return HospitalResult.Fail("not_found");
			}
			else
			{
				row = new HospitalFloor { CompanyId = ctx.CompanyId, CreatedAt = DateTime.UtcNow, CreatedByEmployeeId = employeeId };
				_db.HospitalFloors.Add(row);
			}
			row.Code = code; row.Name = input.Name.Trim(); row.NameEn = string.IsNullOrWhiteSpace(input.NameEn) ? null : input.NameEn.Trim();
			row.SortOrder = input.SortOrder;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		public async Task<HospitalResult> SaveWardAsync(BusinessContext ctx, HospitalWardInput input, int? employeeId, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var code = (input.Code ?? "").Trim();
			if (code.Length == 0 || code.Length > 40) return HospitalResult.Fail("Enter a code of up to 40 characters");
			if (string.IsNullOrWhiteSpace(input.Name)) return HospitalResult.Fail("Enter the Arabic name");
			if (!HospitalSexPolicy.IsKnown(input.SexPolicy)) return HospitalResult.Fail("Unknown sex policy");
			if (!await _db.HospitalFloors.AnyAsync(f => f.ID == input.FloorId && f.CompanyId == ctx.CompanyId, ct))
				return HospitalResult.Fail("not_found");
			int self = input.Id ?? 0;
			if (await _db.HospitalWards.AnyAsync(w => w.CompanyId == ctx.CompanyId && w.Code == code && w.ID != self, ct))
				return HospitalResult.Fail("Another ward already uses this code");

			HospitalWard? row;
			if (input.Id is int id)
			{
				row = await _db.HospitalWards.FirstOrDefaultAsync(w => w.ID == id && w.CompanyId == ctx.CompanyId, ct);
				if (row == null) return HospitalResult.Fail("not_found");
			}
			else
			{
				row = new HospitalWard { CompanyId = ctx.CompanyId, CreatedAt = DateTime.UtcNow, CreatedByEmployeeId = employeeId };
				_db.HospitalWards.Add(row);
			}
			row.FloorId = input.FloorId; row.Code = code; row.Name = input.Name.Trim();
			row.NameEn = string.IsNullOrWhiteSpace(input.NameEn) ? null : input.NameEn.Trim();
			row.SexPolicy = input.SexPolicy;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		// ====================================================================================
		// ROOMS
		// ====================================================================================

		/// Creates a room on its floor at the first free place, with as many beds as its type says.
		public async Task<HospitalResult> CreateRoomAsync(BusinessContext ctx, HospitalRoomInput input, int? employeeId, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var number = (input.Number ?? "").Trim();
			if (number.Length == 0 || number.Length > 40) return HospitalResult.Fail("Enter a room number of up to 40 characters");
			if (!HospitalRoomKinds.IsKnown(input.Kind)) return HospitalResult.Fail("Unknown room kind");
			if (!await _db.HospitalFloors.AnyAsync(f => f.ID == input.FloorId && f.CompanyId == ctx.CompanyId && f.IsActive, ct))
				return HospitalResult.Fail("not_found");
			if (input.WardId is int wardId
				&& !await _db.HospitalWards.AnyAsync(w => w.ID == wardId && w.CompanyId == ctx.CompanyId && w.FloorId == input.FloorId, ct))
				return HospitalResult.Fail("The ward is not on this floor");
			if (await _db.HospitalRooms.AnyAsync(r => r.CompanyId == ctx.CompanyId && r.Number == number, ct))
				return HospitalResult.Fail("Another room already uses this number");

			HospitalRoomType? type = null;
			if (HospitalRoomKinds.CarriesBeds(input.Kind))
			{
				if (input.RoomTypeId is not int typeId) return HospitalResult.Fail("Choose the room type");
				type = await _db.HospitalRoomTypes.AsNoTracking()
					.FirstOrDefaultAsync(t => t.ID == typeId && t.CompanyId == ctx.CompanyId && t.IsActive, ct);
				if (type == null) return HospitalResult.Fail("not_found");
			}

			int span = type == null ? 2 : type.BedCount >= 4 ? 4 : type.BedCount >= 2 ? 3 : 2;
			var taken = await _db.HospitalRooms.AsNoTracking()
				.Where(r => r.CompanyId == ctx.CompanyId && r.FloorId == input.FloorId && r.IsActive)
				.Select(r => new HospitalRoomPlacement { RoomId = r.ID, Column = r.GridColumn, Row = r.GridRow, ColumnSpan = r.ColumnSpan, RowSpan = r.RowSpan })
				.ToListAsync(ct);
			var (col, row) = FirstFreeCell(taken, span);

			var now = DateTime.UtcNow;
			var room = new HospitalRoom
			{
				CompanyId = ctx.CompanyId, FloorId = input.FloorId, WardId = input.WardId, RoomTypeId = type?.ID,
				Number = number, Name = string.IsNullOrWhiteSpace(input.Name) ? null : input.Name.Trim(),
				NameEn = string.IsNullOrWhiteSpace(input.NameEn) ? null : input.NameEn.Trim(), Kind = input.Kind,
				GridColumn = col, GridRow = row, ColumnSpan = span, RowSpan = 1,
				CreatedAt = now, CreatedByEmployeeId = employeeId,
			};
			_db.HospitalRooms.Add(room);
			await using var tx = await _db.Database.BeginTransactionAsync(ct);
			await _db.SaveChangesAsync(ct);
			if (type != null)
			{
				for (int i = 0; i < type.BedCount; i++)
				{
					var code = BedCode(number, i, type.BedCount);
					if (await _db.HospitalBeds.AnyAsync(b => b.CompanyId == ctx.CompanyId && b.Code == code, ct))
						return HospitalResult.Fail("A bed code for this room is already used: {0}", code);
					_db.HospitalBeds.Add(new HospitalBed { CompanyId = ctx.CompanyId, RoomId = room.ID, Code = code, CreatedAt = now, CreatedByEmployeeId = employeeId });
				}
				await _db.SaveChangesAsync(ct);
			}
			await tx.CommitAsync(ct);
			return HospitalResult.Success(room.ID);
		}

		/// "302" for a single room, "302-A", "302-B" … for a shared one: the letter the desk says.
		public static string BedCode(string roomNumber, int index, int count)
			=> count <= 1 ? roomNumber : roomNumber + "-" + (char)('A' + index);

		private static (int col, int row) FirstFreeCell(IReadOnlyList<HospitalRoomPlacement> taken, int span)
		{
			for (int row = 1; row < 200; row++)
				for (int col = 1; col + span <= GridColumns + 1; col++)
				{
					var probe = new HospitalRoomPlacement { Column = col, Row = row, ColumnSpan = span, RowSpan = 1 };
					if (!taken.Any(t => Overlaps(t, probe))) return (col, row);
				}
			return (1, 200);
		}

		private static bool Overlaps(HospitalRoomPlacement a, HospitalRoomPlacement b)
			=> a.Column < b.Column + b.ColumnSpan && b.Column < a.Column + a.ColumnSpan
			&& a.Row < b.Row + b.RowSpan && b.Row < a.Row + a.RowSpan;

		/// The floor designer's save: every moved or resized room on ONE floor, all or nothing.
		public async Task<HospitalResult> SaveFloorPlanAsync(BusinessContext ctx, int floorId,
			IReadOnlyList<HospitalRoomPlacement> placements, int? employeeId, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (placements.Count == 0) return HospitalResult.Success();

			foreach (var p in placements)
				if (p.Column < 1 || p.ColumnSpan < 1 || p.Column + p.ColumnSpan > GridColumns + 1 || p.Row < 1 || p.RowSpan < 1 || p.RowSpan > 20)
					return HospitalResult.Fail("A room is outside the floor's 12 columns");

			var rooms = await _db.HospitalRooms
				.Where(r => r.CompanyId == ctx.CompanyId && r.FloorId == floorId && r.IsActive).ToListAsync(ct);
			var byId = rooms.ToDictionary(r => r.ID);
			if (placements.Any(p => !byId.ContainsKey(p.RoomId))) return HospitalResult.Fail("not_found");

			// The result as a whole must not overlap — the unchanged rooms included.
			var final = rooms.Select(r => placements.FirstOrDefault(p => p.RoomId == r.ID)
				?? new HospitalRoomPlacement { RoomId = r.ID, Column = r.GridColumn, Row = r.GridRow, ColumnSpan = r.ColumnSpan, RowSpan = r.RowSpan }).ToList();
			for (int i = 0; i < final.Count; i++)
				for (int j = i + 1; j < final.Count; j++)
					if (Overlaps(final[i], final[j]))
						return HospitalResult.Fail("Rooms {0} and {1} overlap", byId[final[i].RoomId].Number, byId[final[j].RoomId].Number);

			var now = DateTime.UtcNow;
			foreach (var p in placements)
			{
				var r = byId[p.RoomId];
				r.GridColumn = p.Column; r.GridRow = p.Row; r.ColumnSpan = p.ColumnSpan; r.RowSpan = p.RowSpan;
				r.UpdatedAt = now; r.UpdatedByEmployeeId = employeeId;
			}
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success();
		}

		/// The room designer's save: the drawing AND the room's beds, in one transaction. A bed in the
		/// drawing that does not exist is created; an existing bed missing from the drawing is retired —
		/// unless someone lies in it, which refuses the whole save.
		public async Task<HospitalResult> SaveRoomLayoutAsync(BusinessContext ctx, int roomId,
			IReadOnlyList<HospitalLayoutItem> items, int? employeeId, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var room = await _db.HospitalRooms.FirstOrDefaultAsync(r => r.ID == roomId && r.CompanyId == ctx.CompanyId && r.IsActive, ct);
			if (room == null) return HospitalResult.Fail("not_found");

			var check = ValidateLayout(items, HospitalRoomKinds.CarriesBeds(room.Kind));
			if (check != null) return check;

			var drawnCodes = items.Where(i => i.Kind == HospitalLayoutKinds.Bed).Select(i => i.Bed!.Trim()).ToList();
			var beds = await _db.HospitalBeds.Where(b => b.CompanyId == ctx.CompanyId && b.RoomId == roomId).ToListAsync(ct);

			var retiring = beds.Where(b => b.IsActive && !drawnCodes.Contains(b.Code, StringComparer.Ordinal)).ToList();
			var held = retiring.FirstOrDefault(b => HospitalBedStatus.HoldsPatient(b.Status));
			if (held != null) return HospitalResult.Fail("Bed {0} holds a patient and cannot be removed", held.Code);

			// A new code must not belong to a bed in ANOTHER room of the company.
			var newCodes = drawnCodes.Where(c => !beds.Any(b => b.Code == c)).ToList();
			if (newCodes.Count > 0 && await _db.HospitalBeds.AnyAsync(b => b.CompanyId == ctx.CompanyId && b.RoomId != roomId && newCodes.Contains(b.Code), ct))
				return HospitalResult.Fail("A bed code in the drawing belongs to another room");

			var now = DateTime.UtcNow;
			foreach (var b in retiring) { b.IsActive = false; b.UpdatedAt = now; b.UpdatedByEmployeeId = employeeId; }
			foreach (var code in drawnCodes)
			{
				var existing = beds.FirstOrDefault(b => b.Code == code);
				if (existing == null)
					_db.HospitalBeds.Add(new HospitalBed { CompanyId = ctx.CompanyId, RoomId = roomId, Code = code, CreatedAt = now, CreatedByEmployeeId = employeeId });
				else if (!existing.IsActive)
				{ existing.IsActive = true; existing.Status = HospitalBedStatus.Vacant; existing.UpdatedAt = now; existing.UpdatedByEmployeeId = employeeId; }
			}

			room.LayoutJson = JsonSerializer.Serialize(items, Json);
			room.UpdatedAt = now; room.UpdatedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(room.ID);
		}

		/// Null when the drawing is acceptable; otherwise the refusal.
		public static HospitalResult? ValidateLayout(IReadOnlyList<HospitalLayoutItem> items, bool carriesBeds)
		{
			if (items.Count > 80) return HospitalResult.Fail("A room drawing holds at most 80 items");
			foreach (var i in items)
			{
				if (!HospitalLayoutKinds.All.Contains(i.Kind, StringComparer.Ordinal)) return HospitalResult.Fail("Unknown item in the drawing: {0}", i.Kind);
				if (i.W <= 0 || i.H <= 0 || i.X < -5 || i.Y < -5 || i.X + i.W > 105 || i.Y + i.H > 105) return HospitalResult.Fail("An item is outside the room");
				if (i.Kind == HospitalLayoutKinds.Bed && string.IsNullOrWhiteSpace(i.Bed)) return HospitalResult.Fail("A bed in the drawing has no code");
				if (i.Kind == HospitalLayoutKinds.Bed && i.Bed!.Trim().Length > 40) return HospitalResult.Fail("A bed code is longer than 40 characters");
			}
			var beds = items.Where(i => i.Kind == HospitalLayoutKinds.Bed).ToList();
			if (!carriesBeds && beds.Count > 0) return HospitalResult.Fail("This space does not carry beds");
			var dup = beds.GroupBy(b => b.Bed!.Trim()).FirstOrDefault(g => g.Count() > 1);
			if (dup != null) return HospitalResult.Fail("Bed {0} is drawn twice", dup.Key);

			var solid = items.Where(i => HospitalLayoutKinds.Solid.Contains(i.Kind, StringComparer.Ordinal)).ToList();
			for (int a = 0; a < solid.Count; a++)
				for (int b = a + 1; b < solid.Count; b++)
				{
					var p = solid[a]; var q = solid[b];
					if (p.Kind != HospitalLayoutKinds.Bed && q.Kind != HospitalLayoutKinds.Bed) continue;
					if (p.X < q.X + q.W && q.X < p.X + p.W && p.Y < q.Y + q.H && q.Y < p.Y + p.H)
						return HospitalResult.Fail("Two items overlap: {0} and {1}", p.Bed ?? p.Kind, q.Bed ?? q.Kind);
				}
			return null;
		}

		// ====================================================================================
		// BED STATUS — the housekeeping states only. Occupied / Reserved belong to admission.
		// ====================================================================================

		public async Task<HospitalResult> SetBedStatusAsync(BusinessContext ctx, int bedId, string status, int? employeeId, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (status is not (HospitalBedStatus.Vacant or HospitalBedStatus.Cleaning or HospitalBedStatus.Blocked))
				return HospitalResult.Fail("Only Vacant, Cleaning or Blocked can be set here");
			var bed = await _db.HospitalBeds.FirstOrDefaultAsync(b => b.ID == bedId && b.CompanyId == ctx.CompanyId && b.IsActive, ct);
			if (bed == null) return HospitalResult.Fail("not_found");
			if (HospitalBedStatus.HoldsPatient(bed.Status)) return HospitalResult.Fail("Bed {0} holds a patient", bed.Code);
			bed.Status = status; bed.UpdatedAt = DateTime.UtcNow; bed.UpdatedByEmployeeId = employeeId;
			// H67: a bed marked for cleaning by hand opens its cleaning job on the support desk too — one flow, saved together
			if (status == HospitalBedStatus.Cleaning) await HospitalSupportDeskService.OpenTurnoverAsync(_db, ctx, bed, employeeId, DateTime.UtcNow, ct);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(bed.ID);
		}
	}
}
