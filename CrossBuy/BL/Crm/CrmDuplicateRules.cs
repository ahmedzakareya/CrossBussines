using System.Globalization;
using System.Text;

namespace CrossBuy.BL.CrmIdentity
{
	/// <summary>
	/// CRM C1 — when are two records the same customer? Pure: no EF, no clock, so every rule is pinned by tests.
	/// </summary>
	/// <remarks>
	/// A NAME ALONE IS WEAK EVIDENCE, IDENTIFIERS ARE STRONG. The measured database has 6 groups of customers
	/// sharing a name, so a unique index on a name is impossible and an exact-name rule would also flag two
	/// genuinely different "Al-Noor" shops. A shared phone, e-mail or tax number is close to proof. The score
	/// adds the evidence, and the screen shows each reason beside the pair so a person decides, never the rule.
	/// </remarks>
	public static class CrmDuplicateRules
	{
		public const int Threshold = 50;

		// An identical name alone reaches the threshold on purpose: it is PROPOSED for a person to judge (the measured
		// six same-name customer groups must surface), never merged by itself. A merely similar name needs a second fact.
		public const int TaxPoints = 50, EmailPoints = 45, NameExactPoints = 50, PhonePoints = 40, NameSimilarPoints = 30, DomainPoints = 20, SameAccountPoints = 15;

		/// One record reduced to the identifiers that matter. AccountId is set for contacts only.
		public sealed record Candidate(int Id, string Name, string? NameEn, string? Phone, string? Email, string? TaxNo = null, int? AccountId = null);

		public enum Reason { SameTaxNumber, SameEmail, SameName, SamePhone, SimilarName, SameEmailDomain, SameAccount }

		public sealed record Match(int LowId, int HighId, int Score, IReadOnlyList<Reason> Reasons);

		// Words that carry no identity: the legal form, "company", "group", "trading". "Nova Retail" and
		// "Nova Retail W.L.L" are one business; so are "شركة الخليج للمقاولات" and "الخليج للمقاولات".
		static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
		{
			"co", "company", "group", "wll", "llc", "ltd", "inc", "the", "est", "establishment", "and", "of", "for", "corp", "corporation", "plc", "sa", "spc",
			"شركه", "شركة", "مؤسسه", "مجموعه", "و", "ذمم", "ذ", "م", "مم", "محدوده", "المحدوده", "مساهمه", "قابضه", "القابضه",
		};

		// Mailboxes anyone can open: a shared domain there says nothing about being the same business.
		static readonly HashSet<string> FreeMail = new(StringComparer.OrdinalIgnoreCase)
		{
			"gmail.com", "hotmail.com", "outlook.com", "yahoo.com", "live.com", "icloud.com", "msn.com", "aol.com", "proton.me", "protonmail.com", "mail.com", "ymail.com",
		};

		/// Arabic and Latin folded to one comparable form: no diacritics or tatweel, one alef, ta marbuta as ha,
		/// alef maqsura as ya, lower case, punctuation as space.
		public static string Fold(string? s)
		{
			if (string.IsNullOrWhiteSpace(s)) return "";
			var sb = new StringBuilder(s.Length);
			foreach (var ch0 in s.Normalize(NormalizationForm.FormD))
			{
				var ch = ch0;
				if (ch >= 'ً' && ch <= 'ْ') continue;            // harakat
				if (ch == 'ـ' || ch == 'ٰ') continue;            // tatweel, superscript alef
				if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
				if (ch == '.') continue;                                    // "W.L.L" and "Co." are one word, not letters
				ch = ch switch
				{
					'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
					'ة' => 'ه',
					'ى' => 'ي',
					'ؤ' => 'و',
					'ئ' => 'ي',
					_ => char.ToLowerInvariant(ch),
				};
				sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
			}
			return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
		}

		/// The identity-bearing words of a name: folded, legal-form noise removed, the Arabic article and the
		/// "لل" prefix taken off long words ("للمقاولات" and "المقاولات" are one word).
		public static IReadOnlyList<string> NameTokens(string? name)
		{
			var list = new List<string>();
			foreach (var t0 in Fold(name).Split(' ', StringSplitOptions.RemoveEmptyEntries))
			{
				var t = t0;
				if (t.Length > 4 && t.StartsWith("لل", StringComparison.Ordinal)) t = t[2..];
				else if (t.Length > 3 && t.StartsWith("ال", StringComparison.Ordinal)) t = t[2..];
				if (Noise.Contains(t) || Noise.Contains(t0)) continue;
				list.Add(t);
			}
			return list;
		}

		/// Digits only, the last eight kept: "+965 9900 1122", "009659900 1122" and "99001122" are one number.
		/// Fewer than seven digits is not a phone number we can match on.
		public static string? PhoneKey(string? phone)
		{
			if (string.IsNullOrWhiteSpace(phone)) return null;
			var digits = new string(phone.Where(c => c >= '0' && c <= '9' || c >= '٠' && c <= '٩').Select(c => c >= '٠' ? (char)('0' + (c - '٠')) : c).ToArray());
			if (digits.Length < 7) return null;
			return digits.Length > 8 ? digits[^8..] : digits;
		}

		public static string? EmailKey(string? email)
		{
			if (string.IsNullOrWhiteSpace(email)) return null;
			var e = email.Trim().ToLowerInvariant();
			var at = e.IndexOf('@');
			return at > 0 && at < e.Length - 3 ? e : null;
		}

		/// The company domain of an e-mail, or null for a free mailbox (which identifies nobody's company).
		public static string? DomainKey(string? email)
		{
			var e = EmailKey(email);
			if (e == null) return null;
			var d = e[(e.IndexOf('@') + 1)..];
			return FreeMail.Contains(d) ? null : d;
		}

		public static string? TaxKey(string? tax)
		{
			if (string.IsNullOrWhiteSpace(tax)) return null;
			var k = new string(tax.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
			return k.Length >= 5 ? k : null;
		}

		/// True when one name's identity words are all in the other's (and there is at least one real word), or
		/// the two word sets overlap by at least 60%.
		public static bool SimilarNames(IReadOnlyList<string> a, IReadOnlyList<string> b)
		{
			if (a.Count == 0 || b.Count == 0) return false;
			var sa = new HashSet<string>(a); var sb = new HashSet<string>(b);
			var inter = sa.Count(sb.Contains);
			var shorter = sa.Count <= sb.Count ? sa : sb;
			if (inter == shorter.Count && shorter.Any(t => t.Length >= 3)) return true;
			var union = sa.Count + sb.Count - inter;
			return union > 0 && inter * 10 >= union * 6;
		}

		static bool NamesEqual(Candidate a, Candidate b)
		{
			static string K(string? n) => string.Join(' ', NameTokens(n));
			var a1 = K(a.Name); var a2 = K(a.NameEn); var b1 = K(b.Name); var b2 = K(b.NameEn);
			return (a1.Length > 0 && (a1 == b1 || a1 == b2)) || (a2.Length > 0 && (a2 == b1 || a2 == b2));
		}

		static bool NamesSimilar(Candidate a, Candidate b) =>
			SimilarNames(NameTokens(a.Name), NameTokens(b.Name)) || SimilarNames(NameTokens(a.NameEn), NameTokens(b.NameEn))
			|| SimilarNames(NameTokens(a.Name), NameTokens(b.NameEn)) || SimilarNames(NameTokens(a.NameEn), NameTokens(b.Name));

		/// The evidence that two records are one, scored. Null when the evidence is below the threshold.
		/// Used to PROPOSE a merge, so it needs enough facts together.
		public static Match? Compare(Candidate a, Candidate b) => Evidence(a, b) is { } m && m.Score >= Threshold ? m : null;

		/// True when the two share an identifier that belongs to one party only — a mobile, an e-mail or a tax number.
		/// That alone is reason enough to WARN while typing ("this number is already on file"), though not to propose a merge.
		public static bool SharesIdentifier(Match m) =>
			m.Reasons.Any(r => r is Reason.SamePhone or Reason.SameEmail or Reason.SameTaxNumber);

		/// Every fact two records share, scored, with no threshold. Null only when they share nothing.
		public static Match? Evidence(Candidate a, Candidate b)
		{
			if (a.Id == b.Id) return null;
			var reasons = new List<Reason>();
			var score = 0;
			if (TaxKey(a.TaxNo) is { } ta && ta == TaxKey(b.TaxNo)) { score += TaxPoints; reasons.Add(Reason.SameTaxNumber); }
			if (EmailKey(a.Email) is { } ea && ea == EmailKey(b.Email)) { score += EmailPoints; reasons.Add(Reason.SameEmail); }
			else if (DomainKey(a.Email) is { } da && da == DomainKey(b.Email)) { score += DomainPoints; reasons.Add(Reason.SameEmailDomain); }
			if (PhoneKey(a.Phone) is { } pa && pa == PhoneKey(b.Phone)) { score += PhonePoints; reasons.Add(Reason.SamePhone); }
			// two PEOPLE in two different companies sharing a name ("Mohammad Ali") are not evidence of anything
			var namesCount = a.AccountId == null || b.AccountId == null || a.AccountId == b.AccountId;
			if (namesCount && NamesEqual(a, b)) { score += NameExactPoints; reasons.Add(Reason.SameName); }
			else if (namesCount && NamesSimilar(a, b)) { score += NameSimilarPoints; reasons.Add(Reason.SimilarName); }
			if (a.AccountId != null && a.AccountId == b.AccountId && reasons.Count > 0) { score += SameAccountPoints; reasons.Add(Reason.SameAccount); }
			if (reasons.Count == 0) return null;
			return new Match(Math.Min(a.Id, b.Id), Math.Max(a.Id, b.Id), Math.Min(score, 99), reasons);
		}

		/// Every pair above the threshold, best first. BLOCKED: only records sharing a phone, an e-mail, a domain, a
		/// tax number or a name word are compared, so ten thousand records cost thousands of comparisons, not fifty million.
		public static IReadOnlyList<Match> FindPairs(IReadOnlyList<Candidate> all, ISet<(int, int)>? dismissed = null)
		{
			var blocks = new Dictionary<string, List<int>>(StringComparer.Ordinal);
			void Add(string? key, int idx) { if (key == null) return; if (!blocks.TryGetValue(key, out var l)) blocks[key] = l = new List<int>(); l.Add(idx); }
			for (var i = 0; i < all.Count; i++)
			{
				var c = all[i];
				Add(PhoneKey(c.Phone) is { } p ? "p:" + p : null, i);
				Add(EmailKey(c.Email) is { } e ? "e:" + e : null, i);
				Add(DomainKey(c.Email) is { } d ? "d:" + d : null, i);
				Add(TaxKey(c.TaxNo) is { } t ? "t:" + t : null, i);
				foreach (var w in NameTokens(c.Name).Concat(NameTokens(c.NameEn)).Where(w => w.Length >= 3).Distinct()) Add("n:" + w, i);
			}
			var seen = new HashSet<(int, int)>();
			var result = new List<Match>();
			foreach (var block in blocks.Values)
			{
				if (block.Count < 2 || block.Count > 400) continue;   // a word shared by 400 records ("trading") is not evidence
				for (var x = 0; x < block.Count; x++)
					for (var y = x + 1; y < block.Count; y++)
					{
						var a = all[block[x]]; var b = all[block[y]];
						var key = (Math.Min(a.Id, b.Id), Math.Max(a.Id, b.Id));
						if (!seen.Add(key) || (dismissed != null && dismissed.Contains(key))) continue;
						if (Compare(a, b) is { } m) result.Add(m);
					}
			}
			return result.OrderByDescending(m => m.Score).ThenBy(m => m.LowId).ToList();
		}
	}
}
