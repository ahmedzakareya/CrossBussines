using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CrossBuy.BL.CrmIdentity
{
	/// <summary>
	/// CRM C3 — stage requirements and discount approval. Pure: no EF, no clock, so the deal screen, the board's
	/// refusal and the server's own check all come from the same rules, and the tests pin them.
	/// </summary>
	public static class CrmDealRules
	{
		public sealed record Line(int? ItemId, string? Description, decimal Qty, decimal UnitPrice, decimal DiscountPercent);

		/// The approval covers exactly these lines. Order-free, culture-free: two saves of the same lines give the
		/// same fingerprint; any change to an item, a quantity, a price or a discount gives a different one.
		public static string Fingerprint(IEnumerable<Line> lines)
		{
			var parts = lines.Select(l => string.Join("|",
					l.ItemId?.ToString(CultureInfo.InvariantCulture) ?? "-",
					(l.Description ?? "").Trim(),
					l.Qty.ToString("0.####", CultureInfo.InvariantCulture),
					l.UnitPrice.ToString("0.####", CultureInfo.InvariantCulture),
					l.DiscountPercent.ToString("0.##", CultureInfo.InvariantCulture)))
				.OrderBy(s => s, StringComparer.Ordinal);
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))));
		}

		public static decimal MaxDiscount(IEnumerable<Line> lines) => lines.Select(l => l.DiscountPercent).DefaultIfEmpty(0m).Max();

		/// The largest discount this person may give alone. No limits configured at all = the feature is off (null).
		/// Limits configured but none for any of the person's roles = 0: they may give no discount without asking.
		public static decimal? LimitFor(IEnumerable<string> roles, IReadOnlyDictionary<string, decimal> limits)
		{
			if (limits.Count == 0) return null;
			var mine = roles.Where(limits.ContainsKey).Select(r => limits[r]).ToList();
			return mine.Count == 0 ? 0m : mine.Max();
		}

		public enum DiscountState { NotLimited, Within, NeedsApproval, Pending, Approved, Rejected }

		public sealed record ApprovalFacts(string Status, decimal RequestedPercent, string Fingerprint);

		/// Where this deal's discount stands for this person. An approval counts only for the exact lines it was given for.
		public static DiscountState StateOf(decimal maxDiscount, decimal? limit, string fingerprint, IEnumerable<ApprovalFacts> approvals)
		{
			if (limit == null) return DiscountState.NotLimited;
			if (maxDiscount <= limit.Value) return DiscountState.Within;
			var forThese = approvals.Where(a => a.Fingerprint == fingerprint && a.RequestedPercent >= maxDiscount).ToList();
			if (forThese.Any(a => a.Status == "Approved")) return DiscountState.Approved;
			if (forThese.Any(a => a.Status == "Pending")) return DiscountState.Pending;
			if (forThese.Any(a => a.Status == "Rejected")) return DiscountState.Rejected;
			return DiscountState.NeedsApproval;
		}

		public static bool DiscountCleared(DiscountState s) => s is DiscountState.NotLimited or DiscountState.Within or DiscountState.Approved;

		/// Who may decide a request: never the person who asked; someone whose own limit covers it, or a CRM manager.
		public static bool MayApprove(int approverId, int requesterId, decimal requested, decimal? approverLimit, bool approverIsManager) =>
			approverId != requesterId && (approverIsManager || (approverLimit is decimal l && l >= requested));

		public sealed record DealFacts(decimal Amount, DateTime? CloseDate, int ProductLines, IReadOnlySet<string> Roles, bool HasNextStep,
			bool DiscountCleared, bool HasQuotation, IReadOnlySet<int> FilledFields);

		public sealed record Requirement(string Kind, int? FieldId = null);

		/// The requirements a deal does NOT yet meet, in the order they were configured.
		public static IReadOnlyList<Requirement> Unmet(DealFacts f, IEnumerable<Requirement> required) =>
			required.Where(r => !(r.Kind switch
			{
				"Amount" => f.Amount > 0,
				"CloseDate" => f.CloseDate != null,
				"Products" => f.ProductLines > 0,
				"DecisionMaker" => f.Roles.Contains("DecisionMaker"),
				"Approver" => f.Roles.Contains("Approver"),
				"NextStep" => f.HasNextStep,
				"Discount" => f.DiscountCleared,
				"Quotation" => f.HasQuotation,
				"CustomField" => r.FieldId is int id && f.FilledFields.Contains(id),
				_ => true,   // an unknown kind never blocks a deal
			})).ToList();
	}
}
