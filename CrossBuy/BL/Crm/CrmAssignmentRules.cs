namespace CrossBuy.BL.CrmIdentity
{
	/// <summary>
	/// CRM C2 — who gets a new lead. Pure: no EF, no clock, so every rule is pinned by tests and the settings
	/// screen can show "who would get this lead, and why" from the very same code.
	/// </summary>
	/// <remarks>
	/// THE ORDER:
	///   1. The lead's company is already an account with an owner → that owner. A customer should never meet a
	///      second salesperson because they filled in a website form.
	///   2. The first active territory (lowest priority number) whose every stated criterion matches → the member
	///      who has waited longest since their last assignment (round-robin; never-assigned first).
	///      A territory with nobody available is skipped, and the next matching one is tried.
	///   3. Nothing matched → nobody. The lead waits in "unassigned" and the sales managers are told; it is never
	///      handed to an arbitrary person, because a wrong owner hides a lead better than no owner does.
	/// </remarks>
	public static class CrmAssignmentRules
	{
		public sealed record LeadFacts(string? Area, string? Segment, string? Source, int? ExistingAccountOwner = null, int? ExistingAccountId = null);
		public sealed record Member(int EmployeeId, DateTime? LastAssignedAt, bool Available = true);
		public sealed record Territory(int Id, string Name, string? Areas, string? Segments, string? Sources, int Priority, bool IsActive, IReadOnlyList<Member> Members);

		public enum Why { AccountOwner, Territory, NoTerritoryMatched, TerritoryHasNobodyAvailable }

		public sealed record Decision(int? OwnerEmployeeId, int? TerritoryId, string? TerritoryName, Why Reason, IReadOnlyList<int> SkippedTerritories);

		/// A comma list (Arabic or English commas), each item folded so "الأحمدي" and "الاحمدي" are one area.
		public static IReadOnlyList<string> Items(string? list) =>
			string.IsNullOrWhiteSpace(list) ? Array.Empty<string>()
			: list.Split(new[] { ',', '،', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(CrmDuplicateRules.Fold).Where(s => s.Length > 0).Distinct().ToList();

		/// Empty criterion = any value. A stated criterion needs a value that is in the list.
		public static bool Meets(string? criterion, string? value)
		{
			var items = Items(criterion);
			if (items.Count == 0) return true;
			var v = CrmDuplicateRules.Fold(value);
			return v.Length > 0 && items.Contains(v);
		}

		public static bool Matches(Territory t, LeadFacts f) =>
			t.IsActive && Meets(t.Areas, f.Area) && Meets(t.Segments, f.Segment) && Meets(t.Sources, f.Source);

		/// The member who waited longest: never assigned first, then the oldest assignment, then the lower id.
		public static Member? NextInTurn(IEnumerable<Member> members) =>
			members.Where(m => m.Available)
				.OrderBy(m => m.LastAssignedAt.HasValue ? 1 : 0).ThenBy(m => m.LastAssignedAt).ThenBy(m => m.EmployeeId)
				.FirstOrDefault();

		public static Decision Decide(LeadFacts f, IEnumerable<Territory> territories)
		{
			if (f.ExistingAccountOwner is int owner)
				return new Decision(owner, null, null, Why.AccountOwner, Array.Empty<int>());
			var skipped = new List<int>();
			foreach (var t in territories.Where(t => Matches(t, f)).OrderBy(t => t.Priority).ThenBy(t => t.Id))
			{
				if (NextInTurn(t.Members) is { } m) return new Decision(m.EmployeeId, t.Id, t.Name, Why.Territory, skipped);
				skipped.Add(t.Id);
			}
			return new Decision(null, null, null, skipped.Count > 0 ? Why.TerritoryHasNobodyAvailable : Why.NoTerritoryMatched, skipped);
		}
	}
}
