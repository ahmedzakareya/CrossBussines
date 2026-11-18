using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H176 — group calls. Design: the approved prototype (#/calls: tick people → group call, the invitation,
	// the grid with the speaker framed, the participants panel, add / remove / end for everyone, a group recording only
	// with EVERY participant's consent).
	//
	// TOPOLOGY. Without a media server in the hospital, each device connects directly to each other one (a mesh): the
	// same encrypted WebRTC as a two-person call, once per pair. A device's load grows with every person, so a group is
	// at most FOUR people, the host included (MeshCap). When IT installs the internal media server (LiveKit), three or
	// more go through it and the cap rises; the screens and these records do not change.
	//
	// THE RECORD. A group call is one HospitalCalls row (IsGroup; the caller is the host, the callee the first person
	// invited) and one HospitalCallParticipants row per person, with how it went for them: invited, joined, declined,
	// missed, busy, left, removed (and by whom). The log reads it for everyone who took part.
	// ==========================================================================================
	public static class HospitalCallGroupRules
	{
		/// The most people in a group call while devices connect to each other directly (no media server yet).
		public const int MeshCap = 4;

		/// The cap actually used: the settings' value, never above what a mesh carries, never below three.
		public static int Cap(int settingsMax) => Math.Clamp(settingsMax, 3, MeshCap);

		/// How many more may be invited now: the cap less everyone still in the call or still ringing.
		public static int Room(int cap, int joined, int ringing) => Math.Max(0, cap - joined - ringing);

		/// A group call ends when one person or fewer is left in it and nobody is still being rung.
		public static bool Ends(int joined, int ringing) => joined <= 1 && ringing == 0;

		/// The state a participant moves to, or null when that move is not allowed now.
		public static string? Next(string state, string action) => (state, action) switch
		{
			(HospitalCallParticipantState.Invited, "join") => HospitalCallParticipantState.Joined,
			(HospitalCallParticipantState.Invited, "decline") => HospitalCallParticipantState.Declined,
			(HospitalCallParticipantState.Invited, "timeout") => HospitalCallParticipantState.Missed,
			(HospitalCallParticipantState.Invited, "remove") => HospitalCallParticipantState.Removed,
			(HospitalCallParticipantState.Joined, "leave") => HospitalCallParticipantState.Left,
			(HospitalCallParticipantState.Joined, "remove") => HospitalCallParticipantState.Removed,
			(HospitalCallParticipantState.Host, "leave") => HospitalCallParticipantState.Left,
			// invited again after declining, missing it, leaving or being busy
			(HospitalCallParticipantState.Declined or HospitalCallParticipantState.Missed or HospitalCallParticipantState.Left or HospitalCallParticipantState.Busy, "invite") => HospitalCallParticipantState.Invited,
			_ => null,
		};

		/// A group recording starts only when every person in the call (the requester aside) has agreed.
		public static bool AllAgreed(IEnumerable<int> inCall, int requester, IEnumerable<int> agreed)
		{
			var yes = agreed.ToHashSet();
			return inCall.Where(e => e != requester).All(yes.Contains);
		}
	}

	public sealed record HospitalCallParticipantRow(int EmployeeId, string State, DateTime? JoinedAt);

	public sealed class HospitalCallGroupService
	{
		public const string NoTables = "Group calls are not installed yet (hospital_h176_group_calls.sql).";
		private readonly CrossDbContext _db;
		public HospitalCallGroupService(CrossDbContext db) { _db = db; }
		private IQueryable<HospitalCallParticipant> Parts(int companyId) => _db.Set<HospitalCallParticipant>().Where(p => p.CompanyId == companyId);

		public async Task<bool> InstalledAsync(CancellationToken ct = default)
		{
			try { await Parts(0).AnyAsync(ct); return true; }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return false; }
		}

		/// Opens a group call: the host, and each person invited with how they could be reached (Invited, or Busy when
		/// offline / in another call — recorded so they see it).
		public async Task<HospitalCall> OpenAsync(int companyId, int hostId, IReadOnlyList<(int Id, string State)> invited, bool video, int? patientId, string? reason, DateTime nowUtc, CancellationToken ct = default)
		{
			var call = new HospitalCall
			{
				CompanyId = companyId, CallerEmployeeId = hostId, CalleeEmployeeId = invited[0].Id, IsVideo = video, IsGroup = true, PatientId = patientId,
				Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(200, reason.Trim().Length)],
				Status = HospitalCallStatus.Answered, StartedAt = nowUtc, AnsweredAt = nowUtc,
			};
			_db.Add(call);
			await _db.SaveChangesAsync(ct);
			_db.Add(new HospitalCallParticipant { CompanyId = companyId, CallId = call.ID, EmployeeId = hostId, State = HospitalCallParticipantState.Host, InvitedAt = nowUtc, JoinedAt = nowUtc });
			foreach (var (id, state) in invited)
				_db.Add(new HospitalCallParticipant { CompanyId = companyId, CallId = call.ID, EmployeeId = id, State = state, InvitedAt = nowUtc });
			await _db.SaveChangesAsync(ct);
			return call;
		}

		/// Someone more is invited (or invited again). False when they are already in it or ringing.
		public async Task<bool> InviteAsync(int companyId, int callId, int employeeId, string state, DateTime nowUtc, CancellationToken ct = default)
		{
			var row = await Parts(companyId).FirstOrDefaultAsync(p => p.CallId == callId && p.EmployeeId == employeeId, ct);
			if (row == null)
			{
				_db.Add(new HospitalCallParticipant { CompanyId = companyId, CallId = callId, EmployeeId = employeeId, State = state, InvitedAt = nowUtc });
				await _db.SaveChangesAsync(ct);
				return true;
			}
			if (HospitalCallGroupRules.Next(row.State, "invite") == null) return false;
			row.State = state; row.InvitedAt = nowUtc; row.LeftAt = null; row.RemovedByEmployeeId = null;
			await _db.SaveChangesAsync(ct);
			return true;
		}

		/// Moves one participant on (join, decline, timeout, leave, remove). Null when not allowed now.
		public async Task<HospitalCallParticipant?> MoveAsync(int companyId, int callId, int employeeId, string action, int? byEmployeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var row = await Parts(companyId).FirstOrDefaultAsync(p => p.CallId == callId && p.EmployeeId == employeeId, ct);
			if (row == null) return null;
			var next = HospitalCallGroupRules.Next(row.State, action);
			if (next == null) return null;
			row.State = next;
			if (next == HospitalCallParticipantState.Joined) row.JoinedAt = nowUtc;
			if (next is HospitalCallParticipantState.Left or HospitalCallParticipantState.Removed) row.LeftAt = nowUtc;
			if (next == HospitalCallParticipantState.Removed) row.RemovedByEmployeeId = byEmployeeId;
			await _db.SaveChangesAsync(ct);
			return row;
		}

		/// Ends a group call for everyone: the call's length, anyone still ringing counts as missed, everyone in it as left.
		public async Task EndAsync(int companyId, int callId, DateTime nowUtc, CancellationToken ct = default)
		{
			var call = await _db.Set<HospitalCall>().FirstOrDefaultAsync(c => c.ID == callId && c.CompanyId == companyId && c.IsGroup, ct);
			if (call == null || call.Status != HospitalCallStatus.Answered) return;
			call.Status = HospitalCallStatus.Ended; call.EndedAt = nowUtc;
			call.DurationSeconds = Math.Max(0, (int)(nowUtc - (call.AnsweredAt ?? call.StartedAt)).TotalSeconds);
			foreach (var p in await Parts(companyId).Where(p => p.CallId == callId).ToListAsync(ct))
			{
				if (p.State == HospitalCallParticipantState.Invited) p.State = HospitalCallParticipantState.Missed;
				else if (p.State is HospitalCallParticipantState.Joined or HospitalCallParticipantState.Host) { p.State = HospitalCallParticipantState.Left; p.LeftAt = nowUtc; }
			}
			await _db.SaveChangesAsync(ct);
		}

		public async Task<IReadOnlyList<HospitalCallParticipantRow>> ParticipantsAsync(int companyId, int callId, CancellationToken ct = default)
			=> await Parts(companyId).AsNoTracking().Where(p => p.CallId == callId).OrderBy(p => p.ID)
				.Select(p => new HospitalCallParticipantRow(p.EmployeeId, p.State, p.JoinedAt)).ToListAsync(ct);

		/// The people of each group call (for the log), keyed by call.
		public async Task<Dictionary<int, List<HospitalCallParticipantRow>>> ForCallsAsync(int companyId, IEnumerable<int> callIds, CancellationToken ct = default)
		{
			var ids = callIds.Distinct().ToList();
			try
			{
				var rows = await Parts(companyId).AsNoTracking().Where(p => ids.Contains(p.CallId)).OrderBy(p => p.ID)
					.Select(p => new { p.CallId, Row = new HospitalCallParticipantRow(p.EmployeeId, p.State, p.JoinedAt) }).ToListAsync(ct);
				return rows.GroupBy(r => r.CallId).ToDictionary(g => g.Key, g => g.Select(x => x.Row).ToList());
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return new(); }
		}

		/// Whether this person took part in this call (host, or joined at some point) — what makes its recording theirs to open.
		public async Task<bool> TookPartAsync(int companyId, int callId, int employeeId, CancellationToken ct = default)
		{
			try { return await Parts(companyId).AnyAsync(p => p.CallId == callId && p.EmployeeId == employeeId && (p.State == HospitalCallParticipantState.Host || p.JoinedAt != null), ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return false; }
		}

		public async Task ConsentAsync(int companyId, int recordingId, int employeeId, string via, DateTime nowUtc, CancellationToken ct = default)
		{
			if (await _db.Set<HospitalCallRecordingConsent>().AnyAsync(c => c.RecordingId == recordingId && c.EmployeeId == employeeId, ct)) return;
			_db.Add(new HospitalCallRecordingConsent { CompanyId = companyId, RecordingId = recordingId, EmployeeId = employeeId, At = nowUtc, Via = via });
			await _db.SaveChangesAsync(ct);
		}
	}
}
