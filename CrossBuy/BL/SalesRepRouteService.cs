using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	/// <summary>
	/// Sales-rep routes and visits — "زيارات اليوم" in the owner's mobile prototype.
	///
	/// A route is a rep's standing plan (customers in order, planned times, weekdays, a daily sales target). The rep's
	/// day is MATERIALISED from the active routes the first time it is read: one visit per stop that applies to that
	/// weekday, idempotent on (route, customer, date) — a unique index backs it, so two devices opening the day at once
	/// cannot double it. A manager can also add a one-off visit (no route). A visit then moves Planned → Arrived → Done,
	/// or is Skipped with a reason; it is never deleted.
	///
	/// The day's ACHIEVEMENT against the target is the sum of the orders taken at that day's visits, which the order
	/// endpoint records on the visit itself (server-side), so the figure cannot be typed in by the phone.
	///
	/// Every query names the company explicitly as well as relying on the global company filter.
	/// Constructed with `new` (as HrRulePackService is), so no DI registration is needed.
	/// </summary>
	public sealed class SalesRepRouteService
	{
		private readonly CrossDbContext _db;
		public SalesRepRouteService(CrossDbContext db) { _db = db; }

		public sealed record StopInput(int CustomerId, int Seq, TimeSpan? PlannedTime, int DaysMask);

		// ------------------------------------------------------------------ the rep's day
		/// The rep's visits for <paramref name="day"/>, generating them from the active routes first (idempotent).
		public async Task<List<SalesRepVisit>> DayAsync(int companyId, int repEmployeeId, DateTime day, CancellationToken ct = default)
		{
			day = day.Date;
			var routes = await _db.SalesRepRoutes.AsNoTracking().Include(r => r.Stops)
				.Where(r => r.CompanyID == companyId && r.RepEmployeeID == repEmployeeId && r.IsActive).ToListAsync(ct);
			if (routes.Count > 0)
			{
				var have = await _db.SalesRepVisits.AsNoTracking()
					.Where(v => v.CompanyID == companyId && v.RepEmployeeID == repEmployeeId && v.VisitDate == day && v.RouteID != null)
					.Select(v => new { v.RouteID, v.CustomerID }).ToListAsync(ct);
				var adds = new List<SalesRepVisit>();
				foreach (var r in routes)
					foreach (var s in r.Stops.Where(s => s.OnDay(day.DayOfWeek)).OrderBy(s => s.Seq))
						if (!have.Any(h => h.RouteID == r.ID && h.CustomerID == s.CustomerID))
							adds.Add(new SalesRepVisit
							{
								CompanyID = companyId, RouteID = r.ID, RepEmployeeID = repEmployeeId, CustomerID = s.CustomerID,
								VisitDate = day, PlannedTime = s.PlannedTime, Seq = s.Seq, CreatedBy = "route",
							});
				if (adds.Count > 0)
				{
					_db.SalesRepVisits.AddRange(adds);
					try { await _db.SaveChangesAsync(ct); }
					catch (DbUpdateException)
					{
						// another device generated the same day a moment earlier: the unique index refused the duplicates
						foreach (var a in adds) _db.Entry(a).State = EntityState.Detached;
					}
				}
			}
			return await _db.SalesRepVisits.AsNoTracking()
				.Where(v => v.CompanyID == companyId && v.RepEmployeeID == repEmployeeId && v.VisitDate == day)
				.OrderBy(v => v.PlannedTime == null).ThenBy(v => v.PlannedTime).ThenBy(v => v.Seq).ThenBy(v => v.ID)
				.ToListAsync(ct);
		}

		/// The rep's active route for the day's heading and target (the first one when there are several).
		public Task<SalesRepRoute?> RouteOfAsync(int companyId, int repEmployeeId, CancellationToken ct = default) =>
			_db.SalesRepRoutes.AsNoTracking().Where(r => r.CompanyID == companyId && r.RepEmployeeID == repEmployeeId && r.IsActive)
				.OrderBy(r => r.ID).FirstOrDefaultAsync(ct);

		private Task<SalesRepVisit?> MineAsync(int companyId, int repEmployeeId, int visitId, CancellationToken ct) =>
			_db.SalesRepVisits.FirstOrDefaultAsync(v => v.ID == visitId && v.CompanyID == companyId && v.RepEmployeeID == repEmployeeId, ct);

		/// The rep reached the customer: the first arrival is kept (opening the card again does not move it).
		public async Task<(bool ok, string? error, SalesRepVisit? v)> ArriveAsync(int companyId, int repEmployeeId, int visitId, CancellationToken ct = default)
		{
			var v = await MineAsync(companyId, repEmployeeId, visitId, ct);
			if (v == null) return (false, "Visit not found", null);
			if (v.Status == SalesRepVisit.Planned) { v.Status = SalesRepVisit.Arrived; v.ArrivedAt = DateTime.UtcNow; await _db.SaveChangesAsync(ct); }
			return (true, null, v);
		}

		/// An order taken at the visit: its id and value are recorded on the visit (the achievement), and the visit is done.
		public async Task RecordOrderAsync(int companyId, int repEmployeeId, int visitId, int salesOrderId, decimal total, CancellationToken ct = default)
		{
			var v = await MineAsync(companyId, repEmployeeId, visitId, ct);
			if (v == null) return;
			v.SalesOrderID = salesOrderId;
			v.OrderTotal += total;
			v.ArrivedAt ??= DateTime.UtcNow;
			v.Status = SalesRepVisit.Done;
			v.CompletedAt ??= DateTime.UtcNow;
			await _db.SaveChangesAsync(ct);
		}

		/// A collection made at the visit (the receipt itself is posted by the accounting API); the visit is done.
		public async Task<(bool ok, string? error)> RecordCollectionAsync(int companyId, int repEmployeeId, int visitId, decimal amount, CancellationToken ct = default)
		{
			if (amount <= 0) return (false, "The amount must be positive");
			var v = await MineAsync(companyId, repEmployeeId, visitId, ct);
			if (v == null) return (false, "Visit not found");
			v.Collected += amount;
			v.ArrivedAt ??= DateTime.UtcNow;
			v.Status = SalesRepVisit.Done;
			v.CompletedAt ??= DateTime.UtcNow;
			await _db.SaveChangesAsync(ct);
			return (true, null);
		}

		/// Close the visit with what happened (no order needed — "the shop was closed", "follow up next week").
		public async Task<(bool ok, string? error)> CloseAsync(int companyId, int repEmployeeId, int visitId, bool skipped, string? outcome, CancellationToken ct = default)
		{
			var v = await MineAsync(companyId, repEmployeeId, visitId, ct);
			if (v == null) return (false, "Visit not found");
			if (skipped && string.IsNullOrWhiteSpace(outcome)) return (false, "Say why the visit was skipped");
			v.Status = skipped ? SalesRepVisit.Skipped : SalesRepVisit.Done;
			v.Outcome = string.IsNullOrWhiteSpace(outcome) ? v.Outcome : outcome.Trim();
			v.CompletedAt ??= DateTime.UtcNow;
			if (!skipped) v.ArrivedAt ??= DateTime.UtcNow;
			await _db.SaveChangesAsync(ct);
			return (true, null);
		}

		// ------------------------------------------------------------------ managing routes (web)
		public async Task<(List<RouteRow> rows, int total)> SearchRoutesAsync(int companyId, string? q, bool? active, int page, int pageSize, bool ar, CancellationToken ct = default)
		{
			var rows = _db.SalesRepRoutes.AsNoTracking().Where(r => r.CompanyID == companyId);
			if (active.HasValue) rows = rows.Where(r => r.IsActive == active.Value);
			foreach (var t in SearchTerms.Parse(q))
			{
				var term = t;
				var reps = _db.Employee.AsNoTracking().Where(e => e.FullName.Contains(term) || (e.FullNameEn != null && e.FullNameEn.Contains(term))).Select(e => e.ID);
				rows = rows.Where(r => r.Name.Contains(term) || (r.NameEn != null && r.NameEn.Contains(term)) || reps.Contains(r.RepEmployeeID));
			}
			var total = await rows.CountAsync(ct);
			if (page < 1) page = 1; if (pageSize < 1) pageSize = 25; else if (pageSize > 200) pageSize = 200;
			var list = await rows.OrderBy(r => r.Name).Skip((page - 1) * pageSize).Take(pageSize)
				.Select(r => new { r.ID, r.Name, r.NameEn, r.RepEmployeeID, r.DailyTarget, r.IsActive, Stops = r.Stops.Count }).ToListAsync(ct);
			var repIds = list.Select(r => r.RepEmployeeID).Distinct().ToList();
			var reps2 = await _db.Employee.AsNoTracking().Where(e => repIds.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync(ct);
			return (list.Select(r =>
			{
				var e = reps2.FirstOrDefault(x => x.ID == r.RepEmployeeID);
				return new RouteRow(r.ID, ar || string.IsNullOrWhiteSpace(r.NameEn) ? r.Name : r.NameEn!, r.RepEmployeeID,
					e == null ? "" : (ar || string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullName : e.FullNameEn!), e?.ProfileImage, r.DailyTarget, r.IsActive, r.Stops);
			}).ToList(), total);
		}

		public sealed record RouteRow(int Id, string Name, int RepId, string Rep, string? RepPhoto, decimal DailyTarget, bool IsActive, int Stops);

		public Task<SalesRepRoute?> GetRouteAsync(int companyId, int id, CancellationToken ct = default) =>
			_db.SalesRepRoutes.AsNoTracking().Include(r => r.Stops).FirstOrDefaultAsync(r => r.ID == id && r.CompanyID == companyId, ct);

		/// Create or update a route with its whole stop list (the editor posts them all at once).
		public async Task<(bool ok, string? error, int id)> SaveRouteAsync(int companyId, int id, string name, string? nameEn, int repEmployeeId,
			decimal dailyTarget, bool isActive, IReadOnlyList<StopInput> stops, string? user, CancellationToken ct = default)
		{
			if (string.IsNullOrWhiteSpace(name)) return (false, "The route needs a name", 0);
			if (dailyTarget < 0) return (false, "The daily target cannot be negative", 0);
			if (!await _db.Employee.AsNoTracking().AnyAsync(e => e.ID == repEmployeeId && e.EmpCompanyID == companyId && e.IsActive, ct))
				return (false, "Choose an active employee of this company as the rep", 0);
			var custIds = stops.Select(s => s.CustomerId).Distinct().ToList();
			if (custIds.Count != stops.Count) return (false, "A customer appears twice on the route", 0);
			if (custIds.Count > 0 && await _db.Customers.AsNoTracking().CountAsync(c => custIds.Contains(c.ID) && c.CompanyID == companyId, ct) != custIds.Count)
				return (false, "A customer on the route is not one of this company's", 0);
			if (stops.Any(s => s.DaysMask is < 1 or > 127)) return (false, "Every stop needs at least one weekday", 0);

			SalesRepRoute route;
			if (id > 0)
			{
				var found = await _db.SalesRepRoutes.Include(r => r.Stops).FirstOrDefaultAsync(r => r.ID == id && r.CompanyID == companyId, ct);
				if (found == null) return (false, "Route not found", 0);
				route = found;
				route.UpdatedAt = DateTime.UtcNow;
			}
			else
			{
				route = new SalesRepRoute { CompanyID = companyId, CreatedBy = user };
				_db.SalesRepRoutes.Add(route);
			}
			route.Name = name.Trim(); route.NameEn = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim();
			route.RepEmployeeID = repEmployeeId; route.DailyTarget = dailyTarget; route.IsActive = isActive;

			// replace the stop list: keep rows whose customer stays (their id is stable), drop the rest, add the new ones
			foreach (var old in route.Stops.Where(s => !custIds.Contains(s.CustomerID)).ToList()) { route.Stops.Remove(old); _db.SalesRepRouteStops.Remove(old); }
			var seq = 0;
			foreach (var s in stops.OrderBy(s => s.Seq))
			{
				seq++;
				var row = route.Stops.FirstOrDefault(x => x.CustomerID == s.CustomerId);
				if (row == null) { row = new SalesRepRouteStop { CompanyID = companyId, CustomerID = s.CustomerId }; route.Stops.Add(row); }
				row.Seq = seq; row.PlannedTime = s.PlannedTime; row.DaysMask = s.DaysMask;
			}
			await _db.SaveChangesAsync(ct);
			return (true, null, route.ID);
		}

		// ------------------------------------------------------------------ the board (web)
		public sealed record BoardRow(int Id, int RepEmployeeId, string Rep, string? RepPhoto, string Customer, TimeSpan? PlannedTime, string Status,
			DateTime? ArrivedAt, decimal OrderTotal, decimal Collected, string? Outcome, string? Route);

		/// Every visit of the day for the company (or one rep), generating each active route's day first.
		public async Task<List<BoardRow>> BoardAsync(int companyId, DateTime day, int? repEmployeeId, bool ar, CancellationToken ct = default)
		{
			var reps = await _db.SalesRepRoutes.AsNoTracking().Where(r => r.CompanyID == companyId && r.IsActive && (repEmployeeId == null || r.RepEmployeeID == repEmployeeId))
				.Select(r => r.RepEmployeeID).Distinct().ToListAsync(ct);
			foreach (var rep in reps) await DayAsync(companyId, rep, day, ct);
			var visits = await _db.SalesRepVisits.AsNoTracking().Where(v => v.CompanyID == companyId && v.VisitDate == day.Date && (repEmployeeId == null || v.RepEmployeeID == repEmployeeId))
				.OrderBy(v => v.RepEmployeeID).ThenBy(v => v.PlannedTime == null).ThenBy(v => v.PlannedTime).ThenBy(v => v.Seq).ToListAsync(ct);
			var empIds = visits.Select(v => v.RepEmployeeID).Distinct().ToList();
			var custIds = visits.Select(v => v.CustomerID).Distinct().ToList();
			var routeIds = visits.Where(v => v.RouteID != null).Select(v => v.RouteID!.Value).Distinct().ToList();
			var emps = await _db.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync(ct);
			var custs = await _db.Customers.AsNoTracking().Where(c => custIds.Contains(c.ID)).Select(c => new { c.ID, c.Name, c.NameEn }).ToListAsync(ct);
			var routes = await _db.SalesRepRoutes.AsNoTracking().Where(r => routeIds.Contains(r.ID)).Select(r => new { r.ID, r.Name, r.NameEn }).ToListAsync(ct);
			string pick(string a, string? e) => ar || string.IsNullOrWhiteSpace(e) ? a : e!;
			return visits.Select(v =>
			{
				var e = emps.FirstOrDefault(x => x.ID == v.RepEmployeeID); var c = custs.FirstOrDefault(x => x.ID == v.CustomerID); var r = routes.FirstOrDefault(x => x.ID == v.RouteID);
				return new BoardRow(v.ID, v.RepEmployeeID, e == null ? "" : pick(e.FullName, e.FullNameEn), e?.ProfileImage, c == null ? "" : pick(c.Name, c.NameEn), v.PlannedTime, v.Status,
					v.ArrivedAt, v.OrderTotal, v.Collected, v.Outcome, r == null ? null : pick(r.Name, r.NameEn));
			}).ToList();
		}

		/// A one-off visit added by a manager for one day (no route).
		public async Task<(bool ok, string? error)> AddVisitAsync(int companyId, int repEmployeeId, int customerId, DateTime day, TimeSpan? time, string? user, CancellationToken ct = default)
		{
			if (!await _db.Employee.AsNoTracking().AnyAsync(e => e.ID == repEmployeeId && e.EmpCompanyID == companyId && e.IsActive, ct)) return (false, "Choose an active employee of this company as the rep");
			if (!await _db.Customers.AsNoTracking().AnyAsync(c => c.ID == customerId && c.CompanyID == companyId, ct)) return (false, "Customer not found");
			if (day.Date < DateTime.Today) return (false, "A visit cannot be planned in the past");
			_db.SalesRepVisits.Add(new SalesRepVisit { CompanyID = companyId, RepEmployeeID = repEmployeeId, CustomerID = customerId, VisitDate = day.Date, PlannedTime = time, Seq = 999, CreatedBy = user });
			await _db.SaveChangesAsync(ct);
			return (true, null);
		}
	}
}
