using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	/// What a hospital record page hands to Views/Hospital/_RecordConversation: the record, and whether the panel
	/// draws its own full-width row (a record page) or sits in a column the page already gave it (patient, room).
	public sealed record HospitalConversationRef(string Code, int Id, bool Card = true);

	// ==========================================================================================
	// A CONVERSATION ON EVERY HOSPITAL RECORD (owner 2026-11-24: «التعليقات ال موجوده في المخازن تضاف للمستشفي … لكل سجل»).
	//
	// Inventory's documents carry the Communication Platform's panel with comments, replies, reactions, files, @-mentions
	// and tasks. The hospital had the first three on two records (the patient's file and the room), each answered by
	// its own trio of endpoints. These four endpoints answer every hospital record instead, so a record page joins
	// with one line (the partial) and a code in HospitalConversationFamilies.
	//
	// ONE GATE, the same question the page itself asks:
	//   · the code is one of this module's conversation families (anything else is "not found");
	//   · hospital `read` FOR THAT RECORD - and for a clinical record (HospitalActions.ClinicalRecords: a visit, a
	//     result, a labour...) that already means the clinical tier, decided in HospitalAccessService, so the desk
	//     and the cashier talk on the file, the room, the bill and the estimate and never on clinical content;
	//   · the record exists in the caller's own company.
	// Absent, other-company and refused answer identically (404): the panel is no existence oracle.
	//
	// The endpoints are [HospitalReceptionAllowed] because the desk's records live here too; the outer clinical gate
	// is therefore replaced by the record-level rule above, not skipped.
	// ==========================================================================================
	public partial class HospitalController
	{
		private static readonly IReadOnlySet<string> HospitalConversationFamilies = new HashSet<string>(StringComparer.Ordinal)
		{
			EntityRegistry.HospitalPatient, EntityRegistry.HospitalRoom, EntityRegistry.HospitalEncounter, EntityRegistry.HospitalLabOrder,
			EntityRegistry.HospitalRadOrder, EntityRegistry.HospitalBill, EntityRegistry.HospitalEstimate, EntityRegistry.HospitalLabour,
			EntityRegistry.HospitalPregnancy, EntityRegistry.HospitalRehabEpisode, EntityRegistry.HospitalMorgueCase,
		};

		private async Task<bool> RecordExistsAsync(string code, int id, int companyId, CancellationToken ct) => code switch
		{
			EntityRegistry.HospitalPatient => await _db.HospitalPatients.AsNoTracking().AnyAsync(x => x.ID == id && x.CompanyId == companyId && x.IsActive, ct),
			EntityRegistry.HospitalRoom => await _db.HospitalRooms.AsNoTracking().AnyAsync(x => x.ID == id && x.CompanyId == companyId && x.IsActive, ct),
			EntityRegistry.HospitalEncounter => await _db.Set<Models.Context.Hospital.HospitalEncounter>().AsNoTracking().AnyAsync(x => x.ID == id && x.CompanyId == companyId, ct),
			EntityRegistry.HospitalLabOrder => await _db.Set<Models.Context.Hospital.HospitalLabOrder>().AsNoTracking().AnyAsync(x => x.ID == id && x.CompanyId == companyId, ct),
			EntityRegistry.HospitalRadOrder => await _db.Set<Models.Context.Hospital.HospitalRadOrder>().AsNoTracking().AnyAsync(x => x.ID == id && x.CompanyId == companyId, ct),
			EntityRegistry.HospitalBill => await _db.Set<Models.Context.Hospital.HospitalBill>().AsNoTracking().AnyAsync(x => x.ID == id && x.CompanyId == companyId, ct),
			EntityRegistry.HospitalEstimate => await _db.Set<Models.Context.Hospital.HospitalEstimate>().AsNoTracking().AnyAsync(x => x.ID == id && x.CompanyId == companyId, ct),
			EntityRegistry.HospitalLabour => await _db.Set<Models.Context.Hospital.HospitalLabour>().AsNoTracking().AnyAsync(x => x.ID == id && x.CompanyId == companyId, ct),
			EntityRegistry.HospitalPregnancy => await _db.Set<Models.Context.Hospital.HospitalPregnancy>().AsNoTracking().AnyAsync(x => x.ID == id && x.CompanyId == companyId, ct),
			EntityRegistry.HospitalRehabEpisode => await _db.Set<Models.Context.Hospital.HospitalRehabEpisode>().AsNoTracking().AnyAsync(x => x.ID == id && x.CompanyId == companyId, ct),
			EntityRegistry.HospitalMorgueCase => await _db.Set<Models.Context.Hospital.HospitalMorgueCase>().AsNoTracking().AnyAsync(x => x.ID == id && x.CompanyId == companyId, ct),
			_ => false,
		};

		private async Task<BusinessContext?> RecordGateAsync(string? entity, int id, CancellationToken ct)
		{
			if (id <= 0 || entity is null || !HospitalConversationFamilies.Contains(entity)) return null;
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.CompanyId <= 0) return null;
			if (!await _access.CanAsync(ctx, HospitalActions.Read,
					CrossBuy.Models.Platform.PermissionTarget.ForEntity(entity, id), ct)) return null;
			return await RecordExistsAsync(entity, id, ctx.CompanyId, ct) ? ctx : null;
		}

		[HospitalReceptionAllowed]   // the record-level gate decides clinical content (see the header)
		[HttpGet("Records/Conversation")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RecordConversation(string? entity, int id, CancellationToken ct = default)
		{
			var ctx = await RecordGateAsync(entity, id, ct);
			return ctx == null ? NotFound(new { ok = false, code = "not_found" }) : await ConversationListAsync(ctx, entity!, id, ct);
		}

		[HospitalReceptionAllowed]
		[HttpPost("Records/ConversationAdd"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		[RequestSizeLimit(21_000_000)]
		public async Task<IActionResult> RecordConversationAdd(string? entity, int id, string? body, long? parentCommentId, IFormFile? file, int[]? mentionIds, CancellationToken ct = default)
		{
			var ctx = await RecordGateAsync(entity, id, ct);
			return ctx == null ? NotFound(new { ok = false, code = "not_found" }) : await ConversationAddAsync(ctx, entity!, id, body, parentCommentId, file, ct, mentionIds);
		}

		[HospitalReceptionAllowed]
		[HttpPost("Records/ConversationReact"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RecordConversationReact(string? entity, int id, long commentId, string? key, bool on, CancellationToken ct = default)
		{
			var ctx = await RecordGateAsync(entity, id, ct);
			return ctx == null ? NotFound(new { ok = false, code = "not_found" }) : await ConversationReactAsync(ctx, commentId, key, on, ct);
		}
	}
}
