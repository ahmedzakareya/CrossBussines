using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// COMMUNICATION — the prototype's smaller pieces (owner, 2026-11-18: strict adherence to design-output/comm-prototype):
	// pinning and muting a conversation, the counts on the top bar and the floating button, and the last message between
	// two people that the incoming-call card shows.
	// ==========================================================================================
	public partial class ChatController
	{
		private async Task<bool> MemberMayAsync(int conversationId)
		{
			var ctx = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			if (ctx is not { CompanyId: > 0, EmployeeId: > 0 }) return false;
			var comm = HttpContext.RequestServices.GetRequiredService<ICommunicationAccessService>();
			return await comm.CanAsync(ctx, CommunicationActions.Read, new PermissionTarget { ConversationId = conversationId }, HttpContext.RequestAborted);
		}

		/// POST /Chat/Pin — pin a conversation to the top of my own list, or unpin it.
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> Pin(int c, bool on)
		{
			var me = await MeAsync(); if (me == null) return NotFound(new { ok = false });
			var ctx = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			var comm = HttpContext.RequestServices.GetRequiredService<ICommunicationAccessService>();
			if (ctx == null || !await comm.CanAsync(ctx, CommunicationActions.Read, new PermissionTarget { ConversationId = c }, HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var row = await _db.ConversationMembers.FirstOrDefaultAsync(m => m.ConversationId == c && m.EmployeeId == me.Value.empId);
			if (row == null) return NotFound(new { ok = false });
			row.PinnedAt = on ? DateTime.UtcNow : null;
			await _db.SaveChangesAsync();
			return Json(new { ok = true });
		}

		/// POST /Chat/Mute — no notice for an ordinary message in this conversation (a mention still notifies), or back on.
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> Mute(int c, bool on)
		{
			var me = await MeAsync(); if (me == null) return NotFound(new { ok = false });
			var ctx = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			var comm = HttpContext.RequestServices.GetRequiredService<ICommunicationAccessService>();
			if (ctx == null || !await comm.CanAsync(ctx, CommunicationActions.Read, new PermissionTarget { ConversationId = c }, HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var row = await _db.ConversationMembers.FirstOrDefaultAsync(m => m.ConversationId == c && m.EmployeeId == me.Value.empId);
			if (row == null) return NotFound(new { ok = false });
			row.MutedUntil = on ? DateTime.UtcNow.AddYears(50) : null;
			await _db.SaveChangesAsync();
			return Json(new { ok = true });
		}

		/// GET /Chat/Pulse — what the top bar and the floating button show: unread messages and missed calls not returned.
		[HttpGet]
		public async Task<IActionResult> Pulse()
		{
			var me = await MeAsync(); if (me == null) return Json(new { ok = false });
			var convs = await _chat.MyConversationsAsync(me.Value.companyId, me.Value.empId);
			int missed = 0;
			try { missed = await CallSvc.MissedWaitingAsync(me.Value.companyId, me.Value.empId); } catch (Exception) { }
			return Json(new { ok = true, unread = convs.Where(x => !x.Muted).Sum(x => x.Unread), missed });
		}

		/// GET /Chat/LiveCalls — the group calls going on now in my conversations (the list's "live" mark, the conversation's
		/// "a group call is on now · Join" banner).
		[HttpGet]
		public async Task<IActionResult> LiveCalls()
		{
			var me = await MeAsync(); if (me == null) return Json(Array.Empty<object>());
			var live = CrossBuy.Hubs.HospitalCallHub.LiveInConversations(me.Value.companyId);
			if (live.Count == 0) return Json(Array.Empty<object>());
			var convIds = live.Select(l => l.ConversationId).Distinct().ToList();
			var mine = await _db.ConversationMembers.AsNoTracking().Where(m => m.EmployeeId == me.Value.empId && convIds.Contains(m.ConversationId)).Select(m => m.ConversationId).ToListAsync();
			var shown = live.Where(l => mine.Contains(l.ConversationId)).ToList();
			var people = (await CallSvc.PeopleAsync(me.Value.companyId, DateTime.UtcNow, shown.SelectMany(l => l.Joined).Distinct())).ToDictionary(p => p.EmployeeId);
			return Json(shown.Select(l => new
			{
				conversationId = l.ConversationId, callId = l.CallId, video = l.Video, since = DateTime.SpecifyKind(l.StartedAt, DateTimeKind.Utc),
				inIt = l.Joined.Contains(me.Value.empId),
				joined = l.Joined.Where(people.ContainsKey).Select(id => new { id, name = DisplayName.Of(people[id].Name, people[id].NameEn), photo = people[id].Photo, initials = HospitalUi.Initials(people[id].NameEn) }),
			}));
		}

		/// GET /Chat/GroupCallsIn?c=… — the group calls of one group conversation (its details pane), newest first.
		[HttpGet]
		public async Task<IActionResult> GroupCallsIn(int c)
		{
			var me = await MeAsync(); if (me == null || !await MemberMayAsync(c)) return Json(Array.Empty<object>());
			try
			{
				var calls = await _db.Set<CrossBuy.Models.Context.Hospital.HospitalCall>().AsNoTracking()
					.Where(x => x.CompanyId == me.Value.companyId && x.ConversationId == c && x.IsGroup)
					.OrderByDescending(x => x.StartedAt).Take(5)
					.Select(x => new { x.ID, x.IsVideo, x.StartedAt, x.DurationSeconds, x.CallerEmployeeId }).ToListAsync();
				var ids = calls.Select(x => x.ID).ToList();
				var counts = await _db.Set<CrossBuy.Models.Context.Hospital.HospitalCallParticipant>().AsNoTracking()
					.Where(p => ids.Contains(p.CallId) && p.JoinedAt != null).GroupBy(p => p.CallId).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
				return Json(calls.Select(x => new { id = x.ID, video = x.IsVideo, at = DateTime.SpecifyKind(x.StartedAt, DateTimeKind.Utc), seconds = x.DurationSeconds, people = counts.TryGetValue(x.ID, out var n) ? n : 1 }));   // the host has a row too
			}
			catch (Microsoft.Data.SqlClient.SqlException) { return Json(Array.Empty<object>()); }
		}

		/// GET /Chat/LastWith?other=… — the last message between me and one person (the incoming-call card shows it).
		[HttpGet]
		public async Task<IActionResult> LastWith(int other)
		{
			var me = await MeAsync(); if (me == null) return Json(new { ok = false });
			int a = me.Value.empId;
			var conv = await _db.ConversationMembers.AsNoTracking()
				.Where(m => m.EmployeeId == a && _db.Conversations.Any(c => c.ID == m.ConversationId && c.Kind == "Direct" && c.CompanyID == me.Value.companyId)
					&& _db.ConversationMembers.Any(o => o.ConversationId == m.ConversationId && o.EmployeeId == other))
				.Select(m => (int?)m.ConversationId).FirstOrDefaultAsync();
			if (conv == null || !await MemberMayAsync(conv.Value)) return Json(new { ok = true, body = (string?)null });
			var last = await _db.ChatMessages.AsNoTracking().Where(x => x.ConversationId == conv && x.DeletedAt == null)
				.OrderByDescending(x => x.ID).Select(x => new { x.Body, x.AttachmentName, x.AttachmentType, x.SenderEmployeeId }).FirstOrDefaultAsync();
			if (last == null) return Json(new { ok = true, body = (string?)null });
			return Json(new { ok = true, conversationId = conv, body = last.Body ?? last.AttachmentName, kind = last.AttachmentType, mine = last.SenderEmployeeId == a });
		}
	}
}
