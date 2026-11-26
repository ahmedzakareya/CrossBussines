namespace CrossBuy.BL.CrmIdentity
{
	/// CRM C7 — segment rules and journey paths, with no database. Shadow-tested in CrmJourneyRulesTests.
	public static class CrmJourneyRules
	{
		// ------------------------------------------------------------------ segments
		/// What a rule can ask of a contact. The value's kind decides the editor and the check.
		public static readonly Dictionary<string, string> Fields = new()
		{
			["industry"] = "text",          // the account's industry is…
			["owner"] = "employee",         // the account's owner is…
			["customer"] = "bool",          // the account is (not) a financial customer
			["boughtWithin"] = "days",      // a posted invoice in the last N days
			["notBoughtFor"] = "days",      // a customer with no posted invoice for N days (lapsed)
			["sales12m"] = "amount",        // posted sales in the last 12 months ≥ X
			["openDeal"] = "bool",          // has (no) open deal
			["wonWithin"] = "days",         // a deal won in the last N days
			["waOptIn"] = "bool",           // recorded WhatsApp consent
			["emailOptIn"] = "bool",        // recorded e-mail consent
			["hasEmail"] = "bool",
			["hasMobile"] = "bool",
			["primary"] = "bool",           // the account's primary contact
		};

		public sealed record Rule(string Field, string? Value);

		/// Everything a rule can look at for one contact, gathered by the service in one pass.
		public sealed record Facts(int ContactId, int AccountId, string? Industry, int? Owner, bool IsCustomer, DateTime? LastInvoice, decimal Sales12m,
			bool OpenDeal, DateTime? LastWon, string? WhatsApp, string? Email, bool HasEmail, bool HasMobile, bool IsPrimary);

		public static string? RulesRefusal(IReadOnlyList<Rule> rules)
		{
			if (rules.Count == 0) return "A segment needs at least one condition";
			if (rules.Count > 10) return "A segment has at most 10 conditions";
			foreach (var r in rules)
			{
				if (!Fields.TryGetValue(r.Field, out var kind)) return "Unknown condition";
				var v = (r.Value ?? "").Trim();
				switch (kind)
				{
					case "days": if (!int.TryParse(v, out var d) || d is < 1 or > 3650) return "Days must be between 1 and 3650"; break;
					case "amount": if (!decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var a) || a < 0) return "Enter an amount"; break;
					case "employee": if (!int.TryParse(v, out _)) return "Choose a person"; break;
					case "bool": if (v is not ("yes" or "no")) return "Choose yes or no"; break;
					default: if (v.Length == 0) return "A condition needs a value"; break;
				}
			}
			return null;
		}

		public static bool Matches(IReadOnlyList<Rule> rules, bool matchAll, Facts f, DateTime today)
		{
			if (rules.Count == 0) return false;
			bool One(Rule r)
			{
				var v = (r.Value ?? "").Trim();
				bool yes = v == "yes";
				int Days() => int.TryParse(v, out var d) ? d : 0;
				return r.Field switch
				{
					"industry" => string.Equals((f.Industry ?? "").Trim(), v, StringComparison.OrdinalIgnoreCase),
					"owner" => int.TryParse(v, out var o) && f.Owner == o,
					"customer" => f.IsCustomer == yes,
					"boughtWithin" => f.LastInvoice is DateTime li && li.Date >= today.Date.AddDays(-Days()),
					"notBoughtFor" => f.IsCustomer && (f.LastInvoice is not DateTime l || l.Date < today.Date.AddDays(-Days())),
					"sales12m" => decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var a) && f.Sales12m >= a,
					"openDeal" => f.OpenDeal == yes,
					"wonWithin" => f.LastWon is DateTime w && w.Date >= today.Date.AddDays(-Days()),
					"waOptIn" => (f.WhatsApp == "OptIn") == yes,
					"emailOptIn" => (f.Email == "OptIn") == yes,
					"hasEmail" => f.HasEmail == yes,
					"hasMobile" => f.HasMobile == yes,
					"primary" => f.IsPrimary == yes,
					_ => false,
				};
			}
			return matchAll ? rules.All(One) : rules.Any(One);
		}

		// ------------------------------------------------------------------ journeys
		public static readonly string[] NodeTypes = { "email", "whatsapp", "wait", "branch", "task" };
		public static readonly string[] Conditions = { "bought", "replied", "inSegment", "waOptIn", "emailOptIn" };

		/// One step of a journey. A branch carries the two paths it splits into; any other node carries none.
		public sealed class Node
		{
			public string Id { get; set; } = "";
			public string Type { get; set; } = "email";
			public string? Subject { get; set; }
			public string? Body { get; set; }
			public string? Template { get; set; }
			public int Days { get; set; }
			public string? Cond { get; set; }
			public List<Node> Yes { get; set; } = new();
			public List<Node> No { get; set; } = new();
		}

		static IEnumerable<Node> All(IEnumerable<Node> list) => list.SelectMany(n => new[] { n }.Concat(All(n.Yes)).Concat(All(n.No)));

		/// Gives every node an id, keeping the ids it already has (so people already waiting on a node stay there after an
		/// edit) and numbering the new ones after the highest.
		public static void AssignIds(List<Node> nodes)
		{
			var all = All(nodes).ToList();
			var used = new HashSet<string>();
			var max = all.Select(n => n.Id.StartsWith("n") && int.TryParse(n.Id[1..], out var k) ? k : 0).DefaultIfEmpty(0).Max();
			foreach (var n in all)
				if (string.IsNullOrWhiteSpace(n.Id) || !used.Add(n.Id)) { n.Id = "n" + (++max); used.Add(n.Id); }
		}

		public static string? TreeRefusal(List<Node> nodes)
		{
			if (nodes.Count == 0) return "A journey needs at least one step";
			var all = All(nodes).ToList();
			if (all.Count > 30) return "A journey has at most 30 steps";
			int Depth(List<Node> l) => l.Count == 0 ? 0 : l.Max(n => n.Type == "branch" ? 1 + Math.Max(Depth(n.Yes), Depth(n.No)) : 0);
			if (Depth(nodes) > 3) return "Conditions can be nested at most three deep";
			foreach (var n in all)
			{
				if (!NodeTypes.Contains(n.Type)) return "Unknown step type";
				if (n.Type == "email" && (string.IsNullOrWhiteSpace(n.Subject) || string.IsNullOrWhiteSpace(n.Body))) return "An e-mail step needs a subject and a message";
				if (n.Type == "whatsapp" && string.IsNullOrWhiteSpace(n.Template)) return "A WhatsApp step needs an approved template";
				if (n.Type == "task" && string.IsNullOrWhiteSpace(n.Subject)) return "A call or task step needs a title";
				if (n.Type == "wait" && n.Days is < 1 or > 90) return "A wait is between 1 and 90 days";
				if (n.Type == "branch" && !Conditions.Contains(n.Cond)) return "Choose what the condition checks";
				if (n.Type != "branch" && (n.Yes.Count > 0 || n.No.Count > 0)) return "Only a condition splits the path";
			}
			return null;
		}

		/// Where the node with this id lives: the list holding it, its position, and the branch that list hangs from.
		sealed record Place(List<Node> List, int Index, Node? Parent);
		static Place? Locate(List<Node> list, string id, Node? parent = null)
		{
			for (var i = 0; i < list.Count; i++)
			{
				if (list[i].Id == id) return new Place(list, i, parent);
				var inner = Locate(list[i].Yes, id, list[i]) ?? Locate(list[i].No, id, list[i]);
				if (inner != null) return inner;
			}
			return null;
		}

		public static Node? Find(List<Node> nodes, string? id) => id == null ? null : All(nodes).FirstOrDefault(n => n.Id == id);
		public static string? First(List<Node> nodes) => nodes.FirstOrDefault()?.Id;

		/// The node that runs after this one is done: the next one beside it, or — at the end of a branch's path — the one
		/// after that branch. Null means the journey is finished. An unknown id (removed by an edit) also finishes.
		public static string? After(List<Node> nodes, string id)
		{
			var p = Locate(nodes, id);
			if (p == null) return null;
			if (p.Index + 1 < p.List.Count) return p.List[p.Index + 1].Id;
			return p.Parent == null ? null : After(nodes, p.Parent.Id);
		}

		/// Into a branch: the first node of the path the answer chose, or straight past the branch when that path is empty.
		public static string? Enter(List<Node> nodes, string branchId, bool yes)
		{
			var b = Find(nodes, branchId);
			if (b == null) return null;
			var path = yes ? b.Yes : b.No;
			return path.Count > 0 ? path[0].Id : After(nodes, branchId);
		}
	}
}
