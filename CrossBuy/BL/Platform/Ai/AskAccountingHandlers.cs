using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// Two accounting questions, both answered from the LEDGER rather than from a convenient summary.
	//
	// Both reuse the arithmetic that the cash-flow screen was repaired with earlier today, and for the
	// same reasons:
	//   - POSTED ONLY. A reversal here is a separate Posted entry and the original is flagged
	//     Reversed, so counting both counts the money twice. On this installation that was 15,502.74
	//     of cash that did not exist.
	//   - BASE CURRENCY, AND THE ALLOCATION RECORD. Open receivables are GrandTotalBase minus what
	//     ReceiptAllocations says was actually applied — not a customer's total receipts spread
	//     oldest-first, which disagreed with the record on 72 invoices and mixed three currencies.
	//
	// An assistant that answers differently from the screens is worse than one that cannot answer.
	// =================================================================================================

	/// "How much cash do we have?"
	public sealed class AskCashBalanceHandler : IAskHandler
	{
		public const string IntentId = "accounting.cash-balance";
		private readonly CrossDbContext _db;
		public AskCashBalanceHandler(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public string Intent => IntentId;
		public string Module => EntityRegistry.ScopeAccounting;
		public string Action => "read";
		public IReadOnlyCollection<string> Parameters { get; } = Array.Empty<string>();

		public AskText Description => AskText.Of(
			"رصيد النقدية والبنوك من دفتر الأستاذ",
			"The cash and bank balance, from the ledger",
			"Le solde de trésorerie et des banques, d'après le grand livre");

		public AskText Example => AskText.Of("كام رصيد النقدية؟", "What is our cash balance?", "Quel est notre solde de trésorerie ?");

		public IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"النقدية", "نقدية", "الخزنة", "الخزينة", "خزنه", "كاش", "البنك", "البنوك", "السيوله", "السيولة",
			"cash", "cash balance", "bank", "banks", "treasury", "liquidity",
		};

		public async Task<AskAnswer> AnswerAsync(AskRequest request, AskScope scope, CancellationToken ct = default)
		{
			var rows = await (from l in _db.JournalEntryLines.AsNoTracking()
							  join e in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
							  join a in _db.Accounts.AsNoTracking() on l.AccountId equals a.ID
							  where e.CompanyID == scope.CompanyId
									&& e.Status == "Posted"            // NOT "Reversed": see the note above
									&& a.CompanyID == scope.CompanyId
									&& a.Code!.StartsWith("1101")
							  group (l.Debit - l.Credit) by new { a.Code, a.Name } into g
							  select new { g.Key.Code, g.Key.Name, Balance = g.Sum() })
							 .ToListAsync(ct);

			if (rows.Count == 0)
				return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
					"لا توجد حركة مرحَّلة على حسابات النقدية.",
					"There is no posted movement on the cash accounts.",
					"Aucun mouvement validé n'existe sur les comptes de trésorerie."));

			var ordered = rows.OrderByDescending(r => r.Balance).ToList();
			var result = ordered
				.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["code"] = r.Code,
					["name"] = r.Name,
					// MONEY IS ALWAYS TWO PLACES. "4" beside "8.25" in one column reads as a different kind
					// of number, and a money column whose width changes per row cannot be scanned.
					["onHand"] = decimal.Round(r.Balance, 2),
					["onHandText"] = r.Balance.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
				})
				.Append(new Dictionary<string, object?>
				{
					["code"] = "",
					["name"] = "— " + (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
					{
						"ar" => "الإجمالي",
						"fr" => "Total",
						_ => "Total",
					}),
					["onHand"] = decimal.Round(ordered.Sum(r => r.Balance), 2),
					["onHandText"] = ordered.Sum(r => r.Balance).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
				})
				.ToList();

			return new AskAnswer
			{
				Answered = true,
				Rows = result,
				Provenance = new AskProvenance
				{
					CompanyId = scope.CompanyId,
					Sources = new[] { "JournalEntryLines", "Accounts" },
					Authority = EntityRegistry.ScopeAccounting + ":read",
					RowsWithheld = 0,
				},
			};
		}
	}

	/// "How much do customers owe us?"
	public sealed class AskOpenReceivablesHandler : IAskHandler
	{
		public const string IntentId = "accounting.open-receivables";
		private readonly CrossDbContext _db;
		public AskOpenReceivablesHandler(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public string Intent => IntentId;
		public string Module => EntityRegistry.ScopeAccounting;
		public string Action => "read";
		public IReadOnlyCollection<string> Parameters { get; } = Array.Empty<string>();

		public AskText Description => AskText.Of(
			"إجمالي المستحق على العملاء، من واقع الفواتير وما خُصّص لها فعلًا",
			"Total owed by customers, from invoices and what was actually allocated against them",
			"Total dû par les clients, d'après les factures et les règlements qui leur ont été effectivement affectés");

		public AskText Example => AskText.Of("كام المستحق على العملاء؟", "How much do customers owe us?", "Combien les clients nous doivent-ils ?");

		public IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"المستحق", "مستحق", "الذمم", "ذمم", "المدينون", "مديونية", "العملاء", "تحصيل", "التحصيل",
			"receivable", "receivables", "owed", "owe", "debtors", "outstanding", "collection", "ar",
		};

		public async Task<AskAnswer> AnswerAsync(AskRequest request, AskScope scope, CancellationToken ct = default)
		{
			var settled = (await _db.ReceiptAllocations.AsNoTracking()
					.Where(a => a.CompanyID == scope.CompanyId)
					.GroupBy(a => a.SalesInvoiceId)
					.Select(g => new { invoiceId = g.Key, amount = g.Sum(x => x.ArBase) })
					.ToListAsync(ct))
				.ToDictionary(x => x.invoiceId, x => x.amount);

			var invoices = await _db.SalesInvoices.AsNoTracking()
				.Where(i => i.CompanyID == scope.CompanyId && i.Status == "Posted")
				.Select(i => new { i.ID, i.GrandTotalBase })
				.ToListAsync(ct);

			decimal open = 0m;
			var invoicesOpen = 0;
			var skippedNoBase = 0;
			foreach (var inv in invoices)
			{
				// An invoice with no base amount is EXCLUDED and counted. Substituting the foreign
				// total would put another currency into this sum without saying so.
				if (inv.GrandTotalBase == null) { skippedNoBase++; continue; }
				var rest = inv.GrandTotalBase.Value - (settled.TryGetValue(inv.ID, out var s) ? s : 0m);
				if (rest <= 0.005m) continue;
				open += rest;
				invoicesOpen++;
			}

			if (invoicesOpen == 0 && skippedNoBase == 0)
				return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
					"لا توجد فواتير مبيعات مفتوحة.", "There are no open sales invoices.",
					"Aucune facture de vente impayée n'a été trouvée."));

			var language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
			var rows = new List<IReadOnlyDictionary<string, object?>>
			{
				new Dictionary<string, object?>
				{
					["name"] = language switch
					{
						"ar" => "المستحق على العملاء",
						"fr" => "Montant dû par les clients",
						_ => "Owed by customers",
					},
					["code"] = language switch
					{
						"ar" => $"{invoicesOpen} فاتورة",
						"fr" => $"{invoicesOpen} facture(s)",
						_ => $"{invoicesOpen} invoice(s)",
					},
					["onHand"] = decimal.Round(open, 2),
					["onHandText"] = open.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
				},
			};

			return new AskAnswer
			{
				Answered = true,
				Rows = rows,
				Provenance = new AskProvenance
				{
					CompanyId = scope.CompanyId,
					Sources = new[] { "SalesInvoices", "ReceiptAllocations" },
					Authority = EntityRegistry.ScopeAccounting + ":read",
					// Invoices that could not be measured are reported as withheld rather than
					// quietly dropped, so the total is never read as covering everything.
					RowsWithheld = skippedNoBase,
					WithheldNote = skippedNoBase == 0 ? null : AskText.Of(
						$"استُبعدت {skippedNoBase} فاتورة لعدم وجود مبلغ بالعملة الأساسية، فالإجمالي أعلاه جزئي.",
						$"{skippedNoBase} invoice(s) were excluded for having no base-currency amount,"
						+ " so the total above is partial.",
						$"{skippedNoBase} facture(s) exclue(s) : le montant en devise de base est absent. Le total est partiel."),
				},
			};
		}
	}
}
