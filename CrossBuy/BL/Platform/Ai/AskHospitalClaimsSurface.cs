using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// WHAT THE INSURER DECIDED — the claim lines.
	//
	// Its twin, AskHospitalBillingSurface, answers what the hospital CHARGED. This one answers what
	// happened to that charge afterwards, and the two are deliberately separate surfaces rather than
	// one: they are different grains over different rows, and the single most expensive mistake this
	// layer can make is to answer one under the other's name. Billed is 4,253.91 over 104 lines;
	// claimed is a different figure over 19.
	//
	// AMOUNT, ACCEPTED AND RECOUPED ARE THREE DIFFERENT PROMISES.
	//   claimed   — what was asked of the insurer. Every line has one.
	//   accepted  — what the insurer agreed to. A PENDING line has none, and that is not zero: it is
	//               undecided. The column is Nullable, so such a line is excluded from the measure
	//               and COUNTED, never silently averaged in as nought.
	//   recouped  — money the insurer took back after accepting it. Also absent on most lines.
	// Reading "accepted" as zero on an undecided line would understate acceptance by exactly the
	// amount nobody has ruled on yet — a confident wrong number, which is the family of defect this
	// whole layer exists to stop.
	//
	// THE DATE AND THE PAYER COME FROM THE BATCH, NOT THE CLAIM. HospitalClaims carries neither: it
	// has no date column at all. The batch holds CreatedAt and PayerId, so a line reaches both by
	// claim -> batch. A line whose batch cannot be read is LABELLED and kept, never dropped.
	//
	// MONEY IS BEHIND `bill`, VOLUME IS BEHIND `read` — same split as the billing surface, because
	// the hospital's own permission model separates HospitalActions.Bill from HospitalActions.Read.
	// =================================================================================================
	public sealed class AskHospitalClaimsSurface : AskSurface
	{
		public const string SurfaceCode = "hospital.claims";

		private readonly CrossDbContext _db;
		public AskHospitalClaimsSurface(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public override string Code => SurfaceCode;
		public override string Module => EntityRegistry.ScopeHospital;
		public override string Action => HospitalActions.Read;

		public override AskText Description => AskText.Of(
			"مطالبات التأمين: المبلغ المطالَب به والمعتمد وعدد البنود حسب الحالة أو جهة الدفع أو التصنيف",
			"Insurance claims: the claimed and accepted amounts and the line count by status, payer or category");

		public override AskText Rules => AskText.Of(
			"ده قرار التأمين، مش فوترة المستشفى — رقمين مختلفين على صفوف مختلفة · "
			+ "«المطالَب به» على كل بند، و«المعتمد» على البنود اللي اتبتّ فيها بس: البند المعلَّق "
			+ "مالوش مبلغ معتمد وده مش صفر، ده قرار لسه ما اتخدش، فبيتستثنى من المقياس ويتعدّ · "
			+ "التاريخ وجهة الدفع بييجوا من دفعة المطالبات · لا شيء هنا يخص مريضًا بعينه.",
			"This is the INSURER'S decision, not the hospital's billing — two different figures over "
			+ "different rows · \"claimed\" exists on every line; \"accepted\" only on lines that were "
			+ "decided: a pending line has no accepted amount and that is not zero, it is undecided, so "
			+ "it is excluded from the measure and counted · the date and the payer come from the claim "
			+ "batch · nothing here is about an individual patient.");

		public override IReadOnlyList<AskColumn> Columns { get; } = new[]
		{
			// THE COLUMN IS THE DECISION; "مرفوضة" IS ONE OF ITS VALUES.
			//
			// "مرفوض" must never be a synonym here. A word that names a VALUE and is also read as
			// the column's vocabulary turns «المطالبات المرفوضة» into a question ABOUT the status
			// column — it would rank every status instead of filtering to the rejected ones, and
			// hand back a confident total under a question about refusals. The value words reach
			// the filter through the row's own search text.
			new AskColumn { Name = "status", Kind = AskColumnKind.Dimension, Label = AskText.Of("حالة البند", "Line status"),
				Synonyms = new[] { "حالة", "الحالة", "حالة البند", "قرار", "القرار",
					"status", "decision", "line status" } },
			new AskColumn { Name = "payer", Kind = AskColumnKind.Dimension, Label = AskText.Of("جهة الدفع", "Payer"),
				Synonyms = new[] { "جهة الدفع", "جهات الدفع", "شركة التامين", "شركات التامين", "التامين",
					"payer", "payers", "insurer", "insurers", "insurance company" } },
			new AskColumn { Name = "category", Kind = AskColumnKind.Dimension, Label = AskText.Of("التصنيف", "Category"),
				Synonyms = new[] { "تصنيف", "التصنيف", "نوع الخدمة", "نوع البند",
					"category", "categories", "service type", "line type" } },
			// "Reject reason" WAS THIS COLUMN'S LABEL, AND IT SWALLOWED THE STATUS VALUE.
			//
			// A word counts as surface vocabulary when any vocabulary token is a SUBSTRING of it:
			// "rejected".Contains("reject") is true, so the label's own token made "rejected" look
			// like a column word. «rejected claims» was therefore never looked up as a VALUE, no
			// filter was built, and the answer was all 19 lines with no notice that nothing matched.
			// The Arabic escaped only by accident of spelling - "المرفوضه" does not contain "الرفض".
			// The column is named for the REASON it holds, with no token the value shares.
			new AskColumn { Name = "refusalreason", Kind = AskColumnKind.Dimension, Label = AskText.Of("سبب الرفض", "Refusal reason"),
				Synonyms = new[] { "سبب", "السبب", "سبب الرفض", "اسباب الرفض", "كود الرفض",
					"reason", "refusal reason", "reason for refusal", "denial reason", "reason code" } },
			new AskColumn { Name = "month", Kind = AskColumnKind.Dimension, Label = AskText.Of("الشهر", "Month"),
				Synonyms = new[] { "شهر", "شهري", "شهور", "الشهور", "بالشهر", "month", "monthly", "by month" } },
			new AskColumn { Name = "date", Kind = AskColumnKind.Dimension, Label = AskText.Of("التاريخ", "Date"),
				Synonyms = new[] { "تاريخ", "يوم", "يومي", "بالتاريخ", "date", "day", "daily" } },

			new AskColumn { Name = "claimed", Kind = AskColumnKind.Measure, IsMoney = true,
				Action = HospitalActions.Bill,
				Label = AskText.Of("المبلغ المطالَب به", "Claimed"),
				Synonyms = new[] { "مطالب", "المطالب به", "قيمة المطالبة", "مبلغ المطالبة", "مبلغ", "المبلغ",
					"claimed", "claim amount", "amount claimed", "gross" } },
			// THE COLUMN'S NAME IS VOCABULARY TOO. Renaming the label and the synonyms was not enough:
			// AskColumn.Name goes into the known-word list as well, so "accepted" stayed a surface word
			// and «accepted claims» was read as naming this MEASURE - it summed the approved amount over
			// all 19 lines instead of filtering to the 7 that were accepted. Label, synonyms AND name.
			new AskColumn { Name = "approved", Kind = AskColumnKind.Measure, IsMoney = true, Nullable = true,
				Action = HospitalActions.Bill,
				// NAMED FOR THE AGREEMENT, NOT FOR THE STATUS. Calling this measure "Accepted" put
				// "المعتمد" and "accepted" into the surface's vocabulary, and the status value is
				// spelt the same way - so «المطالبات المعتمدة» was read as naming this COLUMN and
				// answered with all 19 lines instead of the 7 that were actually accepted. The
				// measure keeps its meaning under a name the value does not share.
				Label = AskText.Of("المبلغ الموافَق عليه", "Approved amount"),
				Synonyms = new[] { "الموافق عليه", "اللي اتوافق عليه", "اللي وافقوا عليه",
					"approved", "approved amount", "agreed amount", "amount agreed" } },
			new AskColumn { Name = "recouped", Kind = AskColumnKind.Measure, IsMoney = true, Nullable = true,
				Action = HospitalActions.Bill,
				Label = AskText.Of("المبلغ المسترد", "Recouped"),
				Synonyms = new[] { "مسترد", "المسترد", "استرداد", "اللي اترجع",
					"recouped", "recoupment", "clawback", "taken back" } },
			new AskColumn { Name = "lines", Kind = AskColumnKind.Measure, Label = AskText.Of("عدد البنود", "Lines"),
				// "كام" is absent for the same reason as on the billing surface: on a money
				// surface it means "how MUCH" more often than "how MANY".
				Synonyms = new[] { "عدد", "العدد", "بنود", "عدد البنود", "مطالبات", "عدد المطالبات",
					"lines", "count", "how many", "claims" } },
		};

		public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			var lines = await _db.Set<HospitalClaimLine>().AsNoTracking()
				.Where(l => l.CompanyId == scope.CompanyId)
				.Select(l => new
				{
					l.ClaimId, l.Category, l.Status, l.Amount, l.AcceptedAmount, l.RecoupedAmount,
					l.RejectCode, l.RejectReason, l.DecidedAt,
				})
				.ToListAsync(ct);

			if (lines.Count == 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();

			var claimIds = lines.Select(l => l.ClaimId).Distinct().ToList();
			var claims = (await _db.Set<HospitalClaim>().AsNoTracking()
					.Where(c => c.CompanyId == scope.CompanyId && claimIds.Contains(c.ID))
					.Select(c => new { c.ID, c.BatchId })
					.ToListAsync(ct))
				.ToDictionary(c => c.ID, c => c.BatchId);

			var batchIds = claims.Values.Distinct().ToList();
			var batches = (await _db.Set<HospitalClaimBatch>().AsNoTracking()
					.Where(b => b.CompanyId == scope.CompanyId && batchIds.Contains(b.ID))
					.Select(b => new { b.ID, b.PayerId, b.CreatedAt, b.SubmittedAt })
					.ToListAsync(ct))
				.ToDictionary(b => b.ID);

			var payerIds = batches.Values.Select(b => b.PayerId).Distinct().ToList();
			var payers = (await _db.Set<HospitalPayer>().AsNoTracking()
					.Where(p => p.CompanyId == scope.CompanyId && payerIds.Contains(p.ID))
					.Select(p => new { p.ID, p.Name, p.NameEn })
					.ToListAsync(ct))
				.ToDictionary(p => p.ID);

			var rows = new List<IReadOnlyDictionary<string, object?>>(lines.Count);
			foreach (var l in lines)
			{
				var payer = arabic ? "(جهة غير معروفة)" : "(unknown payer)";
				var payerSearch = "";
				DateTime? when = null;

				if (claims.TryGetValue(l.ClaimId, out var batchId) && batches.TryGetValue(batchId, out var b))
				{
					// Submitted is the date the claim actually went to the insurer; a batch still in
					// draft has only CreatedAt, so that is the honest fallback rather than nothing.
					when = b.SubmittedAt ?? b.CreatedAt;
					if (payers.TryGetValue(b.PayerId, out var p))
					{
						payer = (arabic ? (p.Name ?? p.NameEn) : (p.NameEn ?? p.Name)) ?? payer;
						payerSearch = ((p.Name ?? "") + " " + (p.NameEn ?? "")).Trim();
					}
				}

				// A line that was decided carries its own date, and that beats the batch's — the
				// decision is what a question about rejections is asking the date of.
				when = l.DecidedAt ?? when;

				var reject = Reject(l.RejectCode, l.RejectReason, arabic);

				rows.Add(new Dictionary<string, object?>
				{
					["status"] = Status(l.Status, arabic),
					[SearchKeyFor("status")] = (l.Status ?? "") + " " + Status(l.Status, true),
					["payer"] = payer.Trim(),
					[SearchKeyFor("payer")] = payerSearch,
					["category"] = Category(l.Category, arabic),
					[SearchKeyFor("category")] = (l.Category ?? "") + " " + Category(l.Category, true),
					["refusalreason"] = reject,
					[SearchKeyFor("refusalreason")] = ((l.RejectCode ?? "") + " " + (l.RejectReason ?? "")).Trim(),
					["month"] = when?.ToString("yyyy-MM", CultureInfo.InvariantCulture)
						?? (arabic ? "(بلا تاريخ)" : "(no date)"),
					["date"] = when?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
						?? (arabic ? "(بلا تاريخ)" : "(no date)"),
					["claimed"] = l.Amount,
					// NULL, not 0, on an undecided line. The measure is declared Nullable so the
					// layer excludes and counts it rather than reading the absence as a decision.
					["approved"] = l.AcceptedAmount,
					["recouped"] = l.RecoupedAmount,
					["lines"] = 1m,
					[LinkFor("status")] = null,
					[LinkFor("payer")] = null,
					[LinkFor("category")] = null,
					[LinkFor("refusalreason")] = null,
					[LinkFor("month")] = null,
					[LinkFor("date")] = null,
				});
			}
			return rows;
		}

		private static string Status(string? stored, bool arabic)
		{
			var v = (stored ?? "").Trim();
			if (v.Length == 0) return arabic ? "(غير محددة)" : "(not recorded)";
			if (!arabic) return v;
			return v.ToLowerInvariant() switch
			{
				"pending" => "معلَّقة",
				"accepted" => "معتمدة",
				"rejected" => "مرفوضة",
				"resubmitted" => "معادة التقديم",
				_ => v,
			};
		}

		private static string Category(string? stored, bool arabic)
		{
			var v = (stored ?? "").Trim();
			if (v.Length == 0) return arabic ? "(غير مصنَّف)" : "(uncategorised)";
			if (!arabic) return v;
			return v.ToLowerInvariant() switch
			{
				"accommodation" => "إقامة",
				"consultation" => "كشف",
				"lab" => "معمل",
				"radiology" => "أشعة",
				"pharmacy" => "صيدلية",
				"procedure" => "إجراء",
				"other" => "أخرى",
				_ => v,
			};
		}

		/// A LINE THAT WAS NOT REJECTED HAS NO REASON, AND SAYS SO.
		///
		/// Labelling it "(none)" beside real reasons would read as a reason called "none" and put
		/// every accepted line into the rejection analysis. It is named for what it is instead.
		private static string Reject(string? code, string? reason, bool arabic)
		{
			var r = (reason ?? "").Trim();
			var c = (code ?? "").Trim();
			if (r.Length == 0 && c.Length == 0) return arabic ? "(لم تُرفض)" : "(not rejected)";
			if (r.Length == 0) return c;
			return c.Length == 0 ? r : c + " — " + r;
		}
	}
}
