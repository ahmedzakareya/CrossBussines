using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// WHAT THE COMPANY SPENDS — the posted expense side of the general ledger.
	//
	// WHY THIS SURFACE EXISTS, AND WHY IT WAS BUILT RATHER THAN REFUSED.
	//
	// "ايه المصروفات" was refused, and "المصروفات حسب النوع" was worse: it reached the STOCK
	// MOVEMENTS surface and answered with movement types, because "حسب النوع" is vocabulary there
	// and "المصروفات" is vocabulary nowhere. A confident table of inventory adjustments under a
	// question about spending.
	//
	// Before building it I counted, because a surface over data that is not there is how an honest
	// refusal turns into a confident fiction: 28 expense accounts in company 1, 1,086 posted lines
	// across 17 distinct accounts. That is a real ledger, so this answers from it.
	//
	// The same count is why there is NO low-stock surface beside it. "ايه الاصناف اللي قربت تخلص"
	// needs a reorder point per item and the Items table records none — MinMarginPct is the only
	// threshold column there is. Demand cannot supply one either: the stock history is burst-seeded,
	// 28 active days out of 312. Any threshold would be mine rather than the company's, so that
	// question stays refused, and the refusal is the honest answer rather than a missing feature.
	// =================================================================================================
	public sealed class AskExpensesSurface : AskSurface
	{
		public const string SurfaceCode = "accounting.expenses";

		private readonly CrossDbContext _db;
		public AskExpensesSurface(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public override string Code => SurfaceCode;

		public override string Module => EntityRegistry.ScopeAccounting;
		// REPORT AUTHORITY, NOT SCREEN AUTHORITY.
		//
		// This read `read` until 2026-11-11, and accounting's own comment says what was wrong with
		// that: `read` is UNCONDITIONAL — any authenticated employee of a configured company may
		// open an accounting screen — while report-read requires a role, because "a report crosses
		// documents, periods and customers in a single artifact that can be exported and mailed, so
		// it answers to a role rather than to mere membership."
		//
		// An Ask answer is exactly that artifact: one question, every customer, the whole period,
		// ranked, on screen in a second. The product had already drawn this line for reporting and
		// the assistant was serving the same content under the weaker permission.
		//
		// Verified before changing it: company 1 has exactly one accounting role — employee 5,
		// ChiefAccountant — and that is the owner's own login, so the surface stays open to the
		// person it was built for and closes to everyone who was never meant to have it.
		public override string Action => CrossBuy.BL.AccountingActions.ReportRead;

		public override AskText Description => AskText.Of(
			"المصروفات المرحَّلة من دفتر الأستاذ: المبلغ حسب الحساب أو مركز التكلفة أو الشهر",
			"Posted expenses from the general ledger: amount by account, cost centre or month");

		// THE SAME LENS THE LEDGER ITSELF USES. GeneralLedgerService reads Posted AND Reversed, and a
		// surface that quietly used a narrower filter would disagree with the Trial Balance beside
		// it — the one number in this product that must never have two versions.
		public override AskText Rules => AskText.Of(
			"قيود مرحَّلة ومعكوسة فقط، وهي نفس العدسة التي يقرأ بها دفتر الأستاذ · المصروف هو الحسابات "
			+ "التي يبدأ رمزها بـ5 · المبلغ = مدين ناقص دائن، فالقيد العكسي يَطرح نفسه بدل أن يُحسب "
			+ "مرتين · مركز التكلفة غير مسجَّل على أغلب السطور، وتلك تظهر باسم خاص ولا تُستبعَد.",
			"Posted and reversed entries only — the same lens the general ledger reads through · an "
			+ "expense is an account whose code begins with 5 · amount = debit minus credit, so a "
			+ "reversal subtracts itself instead of counting twice · most lines carry no cost centre, "
			+ "and those are shown under their own label rather than dropped.");

		public static readonly string NoCostCentreAr = "(بلا مركز تكلفة)";
		public static readonly string NoCostCentreEn = "(no cost centre)";

		public override IReadOnlyList<AskColumn> Columns { get; } = new[]
		{
			new AskColumn { Name = "account", Kind = AskColumnKind.Dimension, Label = AskText.Of("الحساب", "Account"),
				Synonyms = new[] { "حساب", "حسابات", "الحسابات", "بند", "بنود", "البنود", "نوع المصروف",
					"account", "accounts", "expense account", "line item" } },
			new AskColumn { Name = "accountcode", Kind = AskColumnKind.Dimension, Label = AskText.Of("رمز الحساب", "Account code"),
				Synonyms = new[] { "رمز", "الرمز", "كود الحساب", "account code", "code" } },
			new AskColumn { Name = "costcenter", Kind = AskColumnKind.Dimension, Label = AskText.Of("مركز التكلفة", "Cost centre"),
				Synonyms = new[] { "مركز", "مراكز", "مركز تكلفة", "مراكز التكلفة", "ادارة", "الادارة",
					"cost centre", "cost center", "department", "departments" } },
			new AskColumn { Name = "month", Kind = AskColumnKind.Dimension, Label = AskText.Of("الشهر", "Month"),
				Synonyms = new[] { "شهر", "شهري", "شهور", "الشهور", "شهريا", "بالشهر",
					"month", "monthly", "months", "per month", "by month" } },
			new AskColumn { Name = "date", Kind = AskColumnKind.Dimension, Label = AskText.Of("التاريخ", "Date"),
				Synonyms = new[] { "تاريخ", "يوم", "يومي", "بالتاريخ", "date", "day", "daily" } },
			new AskColumn { Name = "entry", Kind = AskColumnKind.Dimension, Label = AskText.Of("القيد", "Entry"),
				Synonyms = new[] { "قيد", "قيود", "القيود", "entry", "entries", "journal", "voucher" } },

			new AskColumn { Name = "amount", Kind = AskColumnKind.Measure, IsMoney = true, Label = AskText.Of("المبلغ", "Amount"),
				Synonyms = new[] { "مبلغ", "المبلغ", "مصروف", "مصروفات", "المصروفات", "مصاريف", "المصاريف",
					"صرفنا", "انفاق", "الانفاق", "تكلفة", "التكاليف", "expense", "expenses", "spend",
					"spending", "cost", "costs", "outgoings", "amount" } },
		};

		public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			// The join is written out rather than navigated, because the expense test is on the
			// ACCOUNT CODE and the company test is on the ENTRY — they live in different tables and
			// a missing one of the two is how a surface quietly reads another company's ledger.
			var lines = await (
				from l in _db.JournalEntryLines.AsNoTracking()
				join e in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
				join a in _db.Accounts.AsNoTracking() on l.AccountId equals a.ID
				where e.CompanyID == scope.CompanyId
					&& a.CompanyID == scope.CompanyId
					&& (e.Status == "Posted" || e.Status == "Reversed")
					&& a.Code.StartsWith("5")
				select new
				{
					a.Code, AccountName = a.Name, AccountNameEn = a.NameEn,
					l.Debit, l.Credit, l.CostCenterId,
					e.EntryDate, e.EntryNo,
				}).ToListAsync(ct);

			if (lines.Count == 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();

			var centreIds = lines.Where(l => l.CostCenterId != null).Select(l => l.CostCenterId!.Value).Distinct().ToList();
			// CostCenter carries Name and NameEn — checked in the table, not assumed from the three
			// other conventions this codebase already uses for the same idea.
			var centres = (await _db.CostCenters.AsNoTracking()
					.Where(c => centreIds.Contains(c.ID))
					.Select(c => new { c.ID, c.Name, c.NameEn })
					.ToListAsync(ct))
				.ToDictionary(c => c.ID);

			var rows = new List<IReadOnlyDictionary<string, object?>>(lines.Count);
			foreach (var l in lines)
			{
				var accountLabel = AskItems.Readable(arabic ? l.AccountName : l.AccountNameEn,
													 arabic ? l.AccountNameEn : l.AccountName)
					?? l.Code;

				var centreLabel = arabic ? NoCostCentreAr : NoCostCentreEn;
				if (l.CostCenterId != null && centres.TryGetValue(l.CostCenterId.Value, out var cc))
					centreLabel = AskItems.Readable(arabic ? cc.Name : cc.NameEn, arabic ? cc.NameEn : cc.Name)
						?? ("#" + cc.ID);

				rows.Add(new Dictionary<string, object?>
				{
					["account"] = accountLabel.Trim(),
					// Both spellings, for matching only, so a named account is findable in either.
					[SearchKeyFor("account")] = ((l.AccountName ?? "") + " " + (l.AccountNameEn ?? "")).Trim(),
					["accountcode"] = l.Code,
					["costcenter"] = centreLabel.Trim(),
					["month"] = l.EntryDate.ToString("yyyy-MM", CultureInfo.InvariantCulture),
					["date"] = l.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
					["entry"] = l.EntryNo,
					// DEBIT MINUS CREDIT, not debit alone. An expense is debit-natured, so a credit
					// on one of these accounts is a correction or a reversal — counting only debits
					// would report money the company did not end up spending.
					["amount"] = l.Debit - l.Credit,
					[LinkFor("account")] = null,
					[LinkFor("costcenter")] = null,
					[LinkFor("month")] = null,
					[LinkFor("date")] = null,
					[LinkFor("entry")] = null,
				});
			}
			return rows;
		}
	}
}
