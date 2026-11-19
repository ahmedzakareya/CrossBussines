using CrossBuy.BL;
using CrossBuy.BL.Communication;
using CrossBuy.Models.Context;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	// ==========================================================================================
	// COMMUNICATION v2 for the app (owner 2026-11-19: "finish everything"): what the browser's chat does beyond plain
	// messages — threads, pin, save, forward, polls, scheduled messages, status and do-not-disturb, channels, catch-up —
	// behind the app's token. The same ChatPlusService and the same authority questions as ChatController.Plus: read
	// a conversation to look at it, send in it to change it; nothing here decides anything itself.
	// ==========================================================================================
	public partial class ChatApiController
	{
		private static List<int> Ids(string? csv) => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).Distinct().ToList();
		private static DateTime? Utc(string? iso) => DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
		private async Task<int> ConvOfAsync(int messageId, CancellationToken ct) => await _db.ChatMessages.AsNoTracking().Where(m => m.ID == messageId).Select(m => m.ConversationId).FirstOrDefaultAsync(ct);
		private async Task<bool> MayAsync(BusinessContext me, string action, int c, CancellationToken ct) => c > 0 && await _comm.CanAsync(me, action, In(c), ct);

		// ---------------- threads ----------------
		[HttpGet("thread")]
		public async Task<IActionResult> Thread(int c, int root, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await MayAsync(me, CommunicationActions.Read, c, ct)) return NotFound(Missing);
			return Ok(await Plus.ThreadAsync(me.CompanyId, c, root, me.EmployeeId!.Value));
		}

		[HttpPost("thread")]
		public async Task<IActionResult> ThreadReply([FromForm] int c, [FromForm] int root, [FromForm] string body, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await MayAsync(me, CommunicationActions.Send, c, ct)) return NotFound(Missing);
			var m = await Plus.PostAsync(me.CompanyId, c, me.EmployeeId!.Value, new ChatPlusService.PostInput { Body = body, ThreadRootId = root });
			return m == null ? BadRequest(new { ok = false }) : Ok(await Plus.DtoAsync(m, me.EmployeeId!.Value));
		}

		// ---------------- pin, save, forward ----------------
		[HttpPost("pin")]
		public async Task<IActionResult> Pin([FromQuery] int id, [FromQuery] bool on, CancellationToken ct)
		{
			var me = await MeAsync(ct); int c = await ConvOfAsync(id, ct);
			if (me == null || !await MayAsync(me, CommunicationActions.Send, c, ct)) return NotFound(Missing);
			return Ok(new { ok = await Plus.PinAsync(me.CompanyId, c, me.EmployeeId!.Value, id, on) });
		}

		[HttpPost("save")]
		public async Task<IActionResult> Save([FromQuery] int id, [FromQuery] bool on, CancellationToken ct)
		{
			var me = await MeAsync(ct); int c = await ConvOfAsync(id, ct);
			if (me == null || !await MayAsync(me, CommunicationActions.Read, c, ct)) return NotFound(Missing);
			return Ok(new { ok = await Plus.SaveAsync(me.CompanyId, me.EmployeeId!.Value, id, on) });
		}

		[HttpGet("saved")]
		public async Task<IActionResult> Saved(CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await _comm.CanAsync(me, CommunicationActions.Read, null, ct)) return NotFound(Missing);
			return Ok(await Plus.SavedAsync(me.CompanyId, me.EmployeeId!.Value));
		}

		[HttpPost("forward")]
		public async Task<IActionResult> Forward([FromForm] int id, [FromForm] string to, [FromForm] string? note, CancellationToken ct)
		{
			var me = await MeAsync(ct); int c = await ConvOfAsync(id, ct);
			if (me == null || !await MayAsync(me, CommunicationActions.Read, c, ct)) return NotFound(Missing);
			var targets = new List<int>();
			foreach (var t in Ids(to)) if (await MayAsync(me, CommunicationActions.Send, t, ct)) targets.Add(t);
			return Ok(new { ok = true, sent = await Plus.ForwardAsync(me.CompanyId, me.EmployeeId!.Value, id, targets, note) });
		}

		// ---------------- polls ----------------
		[HttpPost("poll")]
		public async Task<IActionResult> CreatePoll([FromForm] int c, [FromForm] string question, [FromForm] string options, [FromForm] bool multi, [FromForm] bool anonymous, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await MayAsync(me, CommunicationActions.Send, c, ct)) return NotFound(Missing);
			var m = await Plus.CreatePollAsync(me.CompanyId, c, me.EmployeeId!.Value, question, (options ?? "").Split('\n').ToList(), multi, anonymous, null);
			return m == null ? BadRequest(new { ok = false }) : Ok(await Plus.DtoAsync(m, me.EmployeeId!.Value));
		}

		// ---------------- scheduled messages ----------------
		[HttpPost("schedule")]
		public async Task<IActionResult> Schedule([FromForm] int c, [FromForm] string body, [FromForm] string sendAt, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await MayAsync(me, CommunicationActions.Send, c, ct)) return NotFound(Missing);
			var at = Utc(sendAt); if (at == null) return BadRequest(new { ok = false });
			int id = await Plus.ScheduleAsync(me.CompanyId, c, me.EmployeeId!.Value, body, at.Value);
			return Ok(new { ok = id > 0, id });
		}

		[HttpGet("scheduled")]
		public async Task<IActionResult> Scheduled(int c, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await MayAsync(me, CommunicationActions.Read, c, ct)) return NotFound(Missing);
			return Ok(await Plus.ScheduledAsync(me.CompanyId, c, me.EmployeeId!.Value));
		}

		[HttpPost("scheduled/cancel")]
		public async Task<IActionResult> CancelScheduled([FromQuery] int id, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await _comm.CanAsync(me, CommunicationActions.Send, null, ct)) return NotFound(Missing);
			return Ok(new { ok = await Plus.CancelScheduledAsync(me.CompanyId, me.EmployeeId!.Value, id) });
		}

		// ---------------- status and do-not-disturb ----------------
		[HttpGet("statuses")]
		public async Task<IActionResult> Statuses(string? ids, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
			return Ok(new { me = me.EmployeeId!.Value, statuses = await Plus.StatusesAsync(me.CompanyId, Ids(ids).Append(me.EmployeeId!.Value)) });
		}

		[HttpPost("status")]
		public async Task<IActionResult> SetStatus([FromForm] string? emoji, [FromForm] string? text, [FromForm] string? until, [FromForm] bool dnd, [FromForm] string? dndUntil, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await _comm.CanAsync(me, CommunicationActions.Send, null, ct)) return NotFound(Missing);
			await Plus.SetStatusAsync(me.CompanyId, me.EmployeeId!.Value, emoji, text, Utc(until), dnd, Utc(dndUntil));
			return Ok(new { ok = true });
		}

		// ---------------- channels ----------------
		[HttpGet("channels")]
		public async Task<IActionResult> Channels(string? q, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await _comm.CanAsync(me, CommunicationActions.Read, null, ct)) return NotFound(Missing);
			return Ok(await Plus.BrowseChannelsAsync(me.CompanyId, me.EmployeeId!.Value, q));
		}

		[HttpPost("channels")]
		public async Task<IActionResult> CreateChannel([FromForm] string title, [FromForm] string? description, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await _comm.CanAsync(me, CommunicationActions.Send, null, ct)) return NotFound(Missing);
			int id = await Plus.CreateChannelAsync(me.CompanyId, me.EmployeeId!.Value, title, description);
			return id > 0 ? Ok(new { ok = true, id }) : BadRequest(new { ok = false });
		}

		[HttpPost("channels/join")]
		public async Task<IActionResult> JoinChannel([FromQuery] int c, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await _comm.CanAsync(me, CommunicationActions.Send, null, ct)) return NotFound(Missing);
			return Ok(new { ok = await Plus.JoinChannelAsync(me.CompanyId, me.EmployeeId!.Value, c) });
		}

		// ---------------- what I missed ----------------
		[HttpGet("catchup")]
		public async Task<IActionResult> CatchUp(int c, int since, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await MayAsync(me, CommunicationActions.Read, c, ct)) return NotFound(Missing);
			var self = await _db.Employee.AsNoTracking().Where(e => e.ID == me.EmployeeId).Select(e => new { e.FullName, e.FullNameEn }).FirstOrDefaultAsync(ct);
			var r = await Plus.CatchUpAsync(me.CompanyId, c, me.EmployeeId!.Value, since, self?.FullName, self?.FullNameEn);
			return r == null ? NotFound(Missing) : Ok(r);
		}
	}
}
