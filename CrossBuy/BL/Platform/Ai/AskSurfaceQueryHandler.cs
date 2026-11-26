using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// THE SALES SURFACE, EXPOSED AS ONE QUESTION.
	//
	// It plugs into the existing gate unchanged: still an intent, still checked for company and
	// module authority before it runs, still returning rows with provenance. What differs is that
	// the question is not fixed — the `query` parameter carries a COMPOSED query, validated against
	// the surface's declaration before a row is read.
	//
	// So the four sales questions written by hand earlier today become one case of what this can
	// answer, and the next sales question needs no code. "Revenue by month", "margin by customer",
	// "quantity of one item in July", "the twenty worst-margin items" are all the same handler.
	//
	// DEGRADATION IS DELIBERATE AND VISIBLE. With no query, or an unusable one, it answers the most
	// likely sales question — revenue by item, highest first — and SAYS that it chose. An assistant
	// that silently substitutes a different question is worse than one that refuses, so the
	// substitution is printed above the figures.
	// =================================================================================================
	public abstract class AskSurfaceQueryHandler : IAskHandler
	{
		protected readonly AskSurface _surface;
		private readonly IAskQueryComposer _composer;

		protected AskSurfaceQueryHandler(AskSurface surface, IAskQueryComposer composer)
		{
			_surface = surface ?? throw new ArgumentNullException(nameof(surface));
			_composer = composer ?? throw new ArgumentNullException(nameof(composer));
		}

		// ---- WHAT A SUBCLASS SUPPLIES. Six members, and nothing else. --------------------------
		public abstract string Intent { get; }

		/// The tables this reads, named for the provenance line. Stated by the subclass because a
		/// surface's own tables are the one thing the shared machinery cannot know.
		protected abstract IReadOnlyList<string> Sources { get; }

		/// The question anyone asks first, used when nothing was composed from the words.
		/// Answers a question that names a particular record: finds the rows whose own name carries
		/// the words the surface did not recognise. Returns null when this surface has no named
		/// records, so the caller falls through to its refusal.
		///
		/// FINDING NOTHING IS AN ANSWER, NOT A FAILURE. "Is there an employee called X" is answered
		/// by "no, nobody by that name" just as truly as by a row, and a refusal there would hide a
		/// fact the asker came for. The two are told apart on screen by the figure beside them, not
		/// by the tone of the sentence.
		private async Task<AskAnswer?> LookupByNameAsync(
			IReadOnlyList<string> words, AskScope scope, bool arabic, CancellationToken ct,
			bool repeating = false)
		{
			var key = _surface.NameKey;
			if (string.IsNullOrEmpty(key) || words.Count == 0) return null;

			var rows = await _surface.RowsAsync(scope, ct);
			if (rows.Count == 0) return null;

			// EVERY word first, then any single one. "احمد زكريا" should find Ahmed Zakaria rather
			// than every Ahmed; but a lone surname must still find its owner, and a question that
			// names two different people must not come back empty because no one row holds both.
			var hits = Matching(rows, key!, words, all: true);
			var partial = false;
			if (hits.Count == 0)
			{
				hits = Matching(rows, key!, words, all: false);
				partial = hits.Count > 0;
			}

			var named = string.Join(arabic ? "، " : ", ", words);

			if (hits.Count == 0)
				return new AskAnswer
				{
					// Answered, with nothing found: the asker learns the roster holds no such name,
					// which is what they asked. Refusing would have been the product declining to
					// look rather than having looked.
					Answered = true,
					Rows = System.Array.Empty<IReadOnlyDictionary<string, object?>>(),
					// The surface description is a full sentence with its own colon and list, so it
					// cannot be dropped into the middle of another sentence. The COUNT carries the
					// same reassurance better anyway: it says the search really happened.
					Spoken = AskText.Of(
						"مفيش حد بالاسم ده — دوَّرت في "
						+ rows.Count.ToString(CultureInfo.InvariantCulture) + " سجل ومحصلش تطابق على «" + named + "».",
						"Nobody by that name — I searched "
						+ rows.Count.ToString(CultureInfo.InvariantCulture) + " records and none matches \"" + named + "\".",
						"Aucun résultat pour ce nom : j'ai recherché parmi "
						+ rows.Count.ToString(CultureInfo.InvariantCulture) + " fiches sans trouver « " + named + " »."),
					Understood = _surface.Rules,
					Provenance = Provenance(scope, rows.Count),
				};

			var shaped = new List<IReadOnlyDictionary<string, object?>>();
			foreach (var r in hits.Take(AskQueryValidator.MaxRecords))
			{
				// The facts this surface records about the record, read off its own dimensions, so
				// a surface that adds a column needs no change here.
				var facts = _surface.Columns
					.Where(c => c.Kind == AskColumnKind.Dimension)
					.Select(c => r.TryGetValue(c.Name, out var v) ? Convert.ToString(v) : null)
					.Where(v => !string.IsNullOrWhiteSpace(v))
					.ToList();

				// ONE RECORD IS A DETAIL VIEW, NOT A LINE.
				//
				// The compact "a · b · c" line is right for a LIST, where the eye runs down the
				// names. For a single record it is the same facts with the labels thrown away:
				// «الأطفال · (بلا عيادة) · متفرغ · نشط` tells you five things and which five you
				// have to work out. When the answer is one thing, it carries its fields with their
				// names and the screen lays them out.
				var detail = hits.Count != 1 ? null : _surface.Columns
					.Where(c => c.Kind == AskColumnKind.Dimension)
					.Select(c => new
					{
						label = c.Label.ForCulture(CultureInfo.CurrentUICulture),
						value = r.TryGetValue(c.Name, out var dv) ? Convert.ToString(dv) : null,
					})
					.Where(x => !string.IsNullOrWhiteSpace(x.value))
					.ToList();

				shaped.Add(new Dictionary<string, object?>
				{
					["name"] = Convert.ToString(r[key!]),
					["fields"] = detail,
					["code"] = detail != null ? null : string.Join(" · ", facts),
					["href"] = r.TryGetValue(AskSurface.RecordHrefKey, out var h) ? Convert.ToString(h) : null,
					// SHOW THE PERSON, NOT A GLYPH. A named colleague answered with initials in a
					// circle is the house rule's own failure case.
					["photo"] = r.TryGetValue(AskSurface.PhotoKey, out var p) ? Convert.ToString(p) : null,
					["onHandText"] = null,
				});
			}

			var more = hits.Count > shaped.Count
				? (arabic ? " ومعروض " + shaped.Count.ToString(CultureInfo.InvariantCulture)
							+ " من " + hits.Count.ToString(CultureInfo.InvariantCulture) + "."
						  : " Showing " + shaped.Count.ToString(CultureInfo.InvariantCulture)
							+ " of " + hits.Count.ToString(CultureInfo.InvariantCulture) + ".")
					: "";

			// SAID WHEN THE MATCH IS LOOSER THAN THE QUESTION. Falling back to any-word matching is
			// what makes a surname work, and it is also how "احمد زكريا" could return a different
			// Ahmed. Unsaid, that reads as a confident identification.
			var loose = !partial ? ""
				: (arabic ? " ده تطابق على جزء من الاسم، مش على الاسم كامل."
						  : " This matches part of the name, not all of it.");

			return new AskAnswer
			{
				Answered = true,
				IsRecordList = true,                 // a named lookup always returns records
				// WHAT A PRONOUN POINTS AT. The owner found one doctor and said «جيب بياناته»;
				// without this, "his" points at nothing and the turn is refused.
				Subject = new AskSubject
				{
					Kind = NarrowKind,
					Value = string.Join(" ", words),
					Display = Convert.ToString(shaped[0]["name"]) ?? "",
				},
				Rows = shaped,
				// THE SECOND TIME IS A DIFFERENT SENTENCE. «جيب بياناته» brings back the same record,
				// and repeating "yes, there is one by that name" reads as the question going
				// unheard. What the asker needs to know is that this IS everything held here.
				Spoken = repeating
					? AskText.Of(
						"ده كل اللي مسجَّل عنه هنا: " + string.Join("، ", shaped.Take(3).Select(x => Convert.ToString(x["name"])))
							+ ". افتح ملفه من الاسم للتفاصيل الكاملة.",
						"This is everything recorded here: " + string.Join(", ", shaped.Take(3).Select(x => Convert.ToString(x["name"])))
							+ ". Open the file from the name for the rest.",
						"Voici tout ce qui est enregistré ici : " + string.Join(", ", shaped.Take(3).Select(x => Convert.ToString(x["name"])))
							+ ". Ouvrez la fiche en cliquant sur son nom pour plus de détails.")
					: AskText.Of(
					(hits.Count == 1 ? "آه، فيه واحد بالاسم ده: " : "لقيت " + hits.Count.ToString(CultureInfo.InvariantCulture) + " بالاسم ده: ")
						+ string.Join("، ", shaped.Take(3).Select(x => Convert.ToString(x["name"]))) + "." + more + loose,
						(hits.Count == 1 ? "Yes, one match: " : "Found " + hits.Count.ToString(CultureInfo.InvariantCulture) + ": ")
							+ string.Join(", ", shaped.Take(3).Select(x => Convert.ToString(x["name"]))) + "." + more + loose,
						(hits.Count == 1 ? "Oui, une correspondance : " : "" + hits.Count.ToString(CultureInfo.InvariantCulture) + " correspondances : ")
							+ string.Join(", ", shaped.Take(3).Select(x => Convert.ToString(x["name"]))) + "." + more + loose),
				Understood = _surface.Rules,
				Provenance = Provenance(scope, rows.Count),
			};
		}

		private static List<IReadOnlyDictionary<string, object?>> Matching(
			IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string key,
			IReadOnlyList<string> words, bool all)
		{
			var found = new List<IReadOnlyDictionary<string, object?>>();
			foreach (var r in rows)
			{
				// Search every spelling the row carries, and fall back to the displayed name for a
				// surface that only has the one.
				if (!r.TryGetValue(AskSurface.SearchNameKey, out var raw) || raw == null)
					if (!r.TryGetValue(key, out raw)) continue;
				var name = AskSearch.Normalise(Convert.ToString(raw));
				if (name.Length < 3) continue;   // an empty or near-empty name matches everything
				// Containment, not token equality: Arabic glues particles on, and a stored name may
				// hold a middle name the question left out.
				// Folded on BOTH sides. Folding one side guarantees a miss - the single most
				// repeated defect in this service, and the list now arrives unfolded by design.
				// A three-letter floor on the ANY pass too: one short word should not pull back a
				// person whose name merely contains those letters.
				var matched = all
					? words.All(w => w.Length >= 3 && name.Contains(AskSearch.Normalise(w), StringComparison.Ordinal))
					: words.Any(w => w.Length >= 4 && name.Contains(AskSearch.Normalise(w), StringComparison.Ordinal));
				if (matched) found.Add(r);
			}
			return found;
		}

		private AskProvenance Provenance(AskScope scope, int rowsRead) => new AskProvenance
		{
			CompanyId = scope.CompanyId,
			Sources = Sources,
			Authority = _surface.Module + ":" + _surface.Action,
			RowsWithheld = 0,
		};

		/// A stretch of time named in words. Returns null when the question names none, or when this
		/// surface has no date to filter — the workforce roster has no "yesterday".
		private sealed class Window
		{
			public AskFilter Filter { get; init; } = new AskFilter();
			public IReadOnlyList<string> Words { get; init; } = System.Array.Empty<string>();
			public AskText Label { get; init; } = AskText.Of("", "");
			public AskText Note(bool arabic) => AskText.Of(
				"عن " + Label.Ar, "For " + Label.En, "Pour " + Label.Fr);
		}

		private Window? RelativeWindow(string? question)
		{
			if (string.IsNullOrWhiteSpace(question)) return null;
			var hasDate = _surface.Column("date") != null;
			var hasMonth = _surface.Column("month") != null;
			if (!hasDate && !hasMonth) return null;

			var q = AskSearch.Normalise(question);
			var inv = CultureInfo.InvariantCulture;
			// DateTime.Today, like the rest of this service. The product has no clock abstraction,
			// and introducing one here would make this change about something else.
			var today = DateTime.Today;

			Window Day(DateTime d, string ar, string en, params string[] words) => new Window
			{
				Filter = new AskFilter { Column = "date", Op = AskOp.Equals, Value = d.ToString("yyyy-MM-dd", inv) },
				Words = words.Select(AskSearch.Normalise).ToList(),
				Label = AskText.Of(ar + " (" + d.ToString("yyyy-MM-dd", inv) + ")", en + " (" + d.ToString("yyyy-MM-dd", inv) + ")"),
			};

			// ORDER MATTERS: "اول امبارح" contains "امبارح", so the longer phrase is tested first or
			// it is read as the shorter one and answers the wrong day.
			if (hasDate)
			{
				if (q.Contains("اول امبارح", StringComparison.Ordinal)
					|| q.Contains("day before yesterday", StringComparison.Ordinal))
					return Day(today.AddDays(-2), "أول امبارح", "the day before yesterday", "اول", "امبارح");

				if (q.Contains("امبارح", StringComparison.Ordinal) || q.Contains("البارح", StringComparison.Ordinal)
					|| q.Contains("مبارح", StringComparison.Ordinal) || q.Contains("yesterday", StringComparison.Ordinal))
					return Day(today.AddDays(-1), "امبارح", "yesterday", "امبارح", "البارح", "مبارح", "yesterday");

				if (q.Contains("النهارده", StringComparison.Ordinal) || q.Contains("اليوم", StringComparison.Ordinal)
					|| q.Contains("today", StringComparison.Ordinal))
					return Day(today, "النهاردة", "today", "النهارده", "اليوم", "today");

				if (q.Contains("الاسبوع ده", StringComparison.Ordinal) || q.Contains("هذا الاسبوع", StringComparison.Ordinal)
					|| q.Contains("this week", StringComparison.Ordinal) || q.Contains("اخر اسبوع", StringComparison.Ordinal))
				{
					var from = today.AddDays(-6);
					return new Window
					{
						Filter = new AskFilter
						{
							Column = "date", Op = AskOp.Between,
							Value = from.ToString("yyyy-MM-dd", inv), Value2 = today.ToString("yyyy-MM-dd", inv),
						},
						Words = new[] { "الاسبوع", "اسبوع", "week" },
						Label = AskText.Of(
							"آخر ٧ أيام (" + from.ToString("yyyy-MM-dd", inv) + " إلى " + today.ToString("yyyy-MM-dd", inv) + ")",
							"the last 7 days (" + from.ToString("yyyy-MM-dd", inv) + " to " + today.ToString("yyyy-MM-dd", inv) + ")"),
					};
				}
			}

			// "الشهر ده" only. "الشهر اللي فات" belongs to the COMPARISON path, which answers a
			// different and better question about it, and claiming it here would take that away.
			if (hasMonth
				&& (q.Contains("الشهر ده", StringComparison.Ordinal) || q.Contains("هذا الشهر", StringComparison.Ordinal)
					|| q.Contains("this month", StringComparison.Ordinal))
				&& !q.Contains("فات", StringComparison.Ordinal) && !q.Contains("الماضي", StringComparison.Ordinal))
				return new Window
				{
					Filter = new AskFilter
					{
						Column = "month", Op = AskOp.Equals, Value = today.ToString("yyyy-MM", inv),
					},
					Words = new[] { "الشهر", "شهر", "month" },
					Label = AskText.Of("الشهر ده (" + today.ToString("yyyy-MM", inv) + ")",
									   "this month (" + today.ToString("yyyy-MM", inv) + ")"),
				};

			return null;
		}

		/// A sentence put IN FRONT of the answer, where it cannot be skimmed past. Null leaves the
		/// answer alone, so a surface with nothing to add costs nothing.
		private static AskText Prefixed(AskText said, AskText? before) =>
			before == null ? said
				: AskText.Of(before.Ar + " " + said.Ar, before.En + " " + said.En,
					before.Fr + " " + said.Fr);

		/// The kind of subject a surface leaves behind: the WORDS that narrowed it, not a
		/// parameter value. Named apart from the item handlers' kinds so a doctor is never
		/// handed to a question about an item.
		public const string NarrowKind = "ask:narrow";

		/// Whether the asker wants the THINGS, not a figure about them.
		///
		/// Deliberately a short, explicit list rather than anything clever: a word that wrongly
		/// turns a total into a list costs the reader the number they asked for, so the words here
		/// are the ones that can only mean "show me them" — and "كام" or "عدد" in the same question
		/// wins, because someone who asked how many asked for a number.
		private static readonly string[] ListWords =
			{ "قائمة", "قايمة", "لستة", "اسماء", "الاسماء", "مين", "اعرض", "اعرضلي", "هاتلي",
			  "list", "names", "who are", "show me the" };
		private static readonly string[] CountWords =
			{ "كام", "عدد", "how many", "count", "number of" };

		private static bool WantsAList(string? question)
		{
			if (string.IsNullOrWhiteSpace(question)) return false;
			var q = AskSearch.Normalise(question);
			if (CountWords.Any(w => q.Contains(AskSearch.Normalise(w), StringComparison.Ordinal))) return false;
			return ListWords.Any(w => q.Contains(AskSearch.Normalise(w), StringComparison.Ordinal));
		}

		/// Only true when the words name a VALUE this surface holds — a record of its own, or a
		/// value of one of its dimensions. It reads the same data the answer would, so a yes here
		/// and a filter there cannot disagree.
		public async Task<bool> CanNarrowAsync(string? question, AskScope scope, CancellationToken ct = default)
		{
			if (string.IsNullOrWhiteSpace(question)) return false;
			var unknown = UnknownWordsIn(question!);
			if (unknown.Count == 0) return false;
			if (await NamedValueAsync(unknown, scope, ct) != null) return true;
			var named = await LookupByNameAsync(unknown, scope, false, ct);
			return named != null && named.Rows.Count > 0;
		}

		/// A word the surface does not know, matched against the VALUES its dimensions actually
		/// hold. "النيل" is not vocabulary and it is a customer; "الطاقة" is not vocabulary and it
		/// is part of a vendor's name. Finding it turns the question from a ranking of everything
		/// into an answer about the thing that was named.
		private sealed class NamedValue
		{
			public AskFilter Filter { get; init; } = new AskFilter();
			public string Column { get; init; } = "";
			public AskText Label { get; init; } = AskText.Of("", "");
			public bool Exact { get; init; }
			public AskText Note(bool arabic) => AskText.Of(
				(Exact ? "مصفّى على " : "مصفّى على ما يحتوي ") + Label.Ar + " = «" + Filter.Value + "»",
				(Exact ? "Filtered to " : "Filtered to values containing ") + Label.En + " = \"" + Filter.Value + "\"",
				(Exact ? "Filtré sur " : "Filtré sur les valeurs contenant ") + Label.Fr + " = «" + Filter.Value + "»");
		}

		private async Task<NamedValue?> NamedValueAsync(
			IReadOnlyList<string> words, AskScope scope, CancellationToken ct)
		{
			var dims = _surface.Columns.Where(c => c.Kind == AskColumnKind.Dimension).ToList();
			if (dims.Count == 0 || words.Count == 0) return null;

			var rows = await _surface.RowsAsync(scope, ct);
			if (rows.Count == 0) return null;

			// A value holding EVERY unknown word is the one the asker meant. Only if no value does
			// is a single word enough — and then the note says "containing", because matching one
			// word of a name is a guess and the reader has to be able to see that it was one.
			foreach (var exact in new[] { true, false })
			{
				foreach (var col in dims)
				{
					var hit = new List<string>();
					foreach (var r in rows)
					{
						if (!r.TryGetValue(col.Name, out var raw) || raw == null) continue;
						var text = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
						if (text.Length == 0) continue;
						// Searched against every spelling the row carries, but FILTERED on the
						// displayed one, so the answer names the value the reader can see.
						var searchable = r.TryGetValue(AskSurface.SearchKeyFor(col.Name), out var alt) && alt != null
							? Convert.ToString(alt, CultureInfo.InvariantCulture) ?? text
							: text;
						var folded = AskSearch.Normalise(searchable);
						// BOTH DIRECTIONS. The stored value can be SHORTER than the word asked: the
						// engagement reads "زائر" and the question says "الزائرين", so testing only
						// "does the value contain the word" missed it and the surface answered with
						// a ranking of all three kinds instead of the one that was named.
						// AN EMPTY VALUE MATCHES EVERY WORD, so it is refused before anything else.
						//
						// "".Contains(x) is false, but x.Contains("") is TRUE for every x — and a
						// practitioner with no clinic carries an empty clinic search key. «ايه سعر
						// الدولار» asked after the list of doctors came back filtered to "(no
						// clinic)", because the empty string matched "الدولار". An unrelated
						// question answered with the previous subject's figures is the exact
						// failure the narrow check was written to prevent, and it was inside it.
						bool Hit(string w)
						{
							if (folded.Length < 3) return false;
							var n = AskSearch.Normalise(w);
							if (n.Length < 3) return false;
							return folded.Contains(n, StringComparison.Ordinal)
								|| (n.Length >= 4 && n.Contains(folded, StringComparison.Ordinal))
								|| folded.Split(' ').Any(part => part.Length >= 3
									&& (n.Contains(part, StringComparison.Ordinal) || part.Contains(n, StringComparison.Ordinal)));
						}
						var ok = exact ? words.All(Hit) : words.Any(w => w.Length >= 4 && Hit(w));
						if (ok && !hit.Contains(text)) hit.Add(text);
					}
					if (hit.Count == 0) continue;

					// ONE value is filtered exactly; several are kept together by the word they
					// share, so a question that names a family of things is not silently narrowed
					// to whichever one happened to be read first.
					var single = hit.Count == 1;
					var word = words.OrderByDescending(w => w.Length).First();
					return new NamedValue
					{
						Column = col.Name,
						Label = col.Label,
						Exact = exact && single,
						Filter = new AskFilter
						{
							Column = col.Name,
							Op = single ? AskOp.Equals : AskOp.Contains,
							Value = single ? hit[0] : word,
						},
					};
				}
			}
			return null;
		}

		/// The words in a question that appear NOWHERE in this surface's vocabulary — not in its
		/// keywords, not in any column's name, label or synonyms. A proper noun lands here; so does
		/// anything the product simply does not record.
		///
		/// Matching is containment in both directions, not equality, because Arabic glues particles
		/// onto words: "الاصناف" must count as knowing "اصناف", and "بالشهر" as knowing "شهر". A
		/// multi-word keyword such as "what did we buy" covers each of its own words, which is what
		/// lets a general English question through.
		private IReadOnlyList<string> UnknownWordsIn(string question)
		{
			// The raw words, aligned to the folded ones by position. Normalise works character by
			// character and never splits or joins, so the two lists have the same length - and the
			// folded word is what gets compared while the raw one is what gets quoted back.
			var raw = question.Split(
				new[] { ' ', '\t', '\n', '\r', '-', '_', '/', '\\', '،', ',', '.',
						'؟', '?', '!', ':', ';', '؛', '(', ')', '[', ']', '\"', '\'' },
				StringSplitOptions.RemoveEmptyEntries);
			var known = new List<string>();
			foreach (var k in Keywords)
			{
				foreach (var w in AskSearch.Tokens(k))
					if (w.Length >= 3) known.Add(w);
			}
			foreach (var col in _surface.Columns)
			{
				foreach (var v in new[] { col.Name, col.Label.Ar, col.Label.En }.Concat(col.Synonyms))
				{
					if (string.IsNullOrWhiteSpace(v)) continue;
					foreach (var w in AskSearch.Tokens(v))
						if (w.Length >= 3) known.Add(w);
				}
			}

			var unknown = new List<string>();
			var folded = AskSearch.Tokens(question);
			for (var idx = 0; idx < folded.Length; idx++)
			{
				var word = folded[idx];
				// Two letters cannot name a subject in either language; a digit on its own is a
				// quantity the composer already reads.
				if (word.Length < 3) continue;
				if (word.All(char.IsDigit)) continue;
				if (Chatter.Contains(word)) continue;   // `word` is the folded form; Chatter is folded too
				if (known.Any(k => k.Contains(word, StringComparison.Ordinal)
					|| word.Contains(k, StringComparison.Ordinal))) continue;
				// Quoted in the asker's spelling; matched on the folded one by the caller.
				var shown = idx < raw.Length && raw.Length == folded.Length ? raw[idx] : word;
				if (!unknown.Contains(shown)) unknown.Add(shown);
			}
			return unknown;
		}

		/// Words that carry no subject: the scaffolding of a question in either language. They are
		/// NOT in any surface's vocabulary and never will be, so without this list every ordinary
		/// phrasing — "عايز اعرف", "show me" — would read as naming something unknown.
		private static readonly string[] ChatterWords =
		{
			"عايز", "عاوز", "عايزة", "محتاج", "ممكن", "هات", "هاتلي", "ورينى", "ورينا", "وريني",
			"قولي", "قوللي", "اعرف", "اعرض", "اظهر", "طلعلي", "طيب", "بسال", "بسأل", "اسال",
			"سؤال", "سؤالي", "ايه", "ايش", "مين", "فين", "امتى", "امتي", "ازاي", "كام", "كم",
			"اسم", "الاسم", "اسمه", "اسمها", "باسم", "بالاسم", "المسمى", "اسمك",
			// A REQUEST FOR A LIST IS NOT A NAME TO LOOK UP. "قائمة الموظفين" was answered with
			// "nobody by that name — I searched 21 records and none matches «قائمة»".
			"قائمة", "قايمة", "القائمة", "لستة", "اسماء", "الاسماء", "كشف", "جدول",
			// A PRONOUN IS NOT A NAME TO LOOK UP. «اي ساعة فيها مواعيد اكتر» opened with "I could not
			// find «فيها» in this data" - a doubt stapled to a correct answer, because the
			// attached pronoun was treated as something that might name a row.
			"فيها", "فيهم", "فيه", "عندها", "عندهم", "بيها", "ليها", "ليهم",
			"list", "names", "roster", "register",
			"name", "named", "called", "his", "her", "their", "whose",
			"عندنا", "عندكم", "بتاعنا", "بتاع", "بتاعة", "الشركة", "شركتنا", "لدينا", "احنا",
			"اخبار", "احوال", "وضع", "الوضع", "حالة", "الحالة", "موقف", "الموقف", "ملخص",
			"نظرة", "تفاصيل", "بيانات", "معلومات", "status", "overview", "summary", "details",
			"انا", "هو", "هي", "هما", "دي", "ده", "دا", "اللي", "الي", "كل", "اي", "باقي",
			"اعلي", "أعلى", "الاعلى", "اكتر", "أكثر", "الاكتر", "اقل", "أقل", "الاقل",
			"احسن", "أحسن", "اسوا", "أسوأ", "اكبر", "أكبر", "اصغر", "أصغر", "ترتيب", "رتب",
			"اول", "أول", "تانى", "تاني", "اجمالي", "إجمالي", "مجموع", "متوسط", "نسبة",
			"مقارنه", "مقارنة", "قارن", "قارنلي", "مقابل", "بالنسبه", "بالنسبة", "فرق", "الفرق",
			"زاد", "زادت", "قل", "قلت", "طلع", "نزل", "تغير", "التغير", "نمو",
			"compared", "comparison", "versus", "against", "change", "growth", "difference",
			"top", "highest", "lowest", "best", "worst", "most", "least", "biggest",
			"largest", "smallest", "first", "total", "average", "rank", "ranked", "sorted",
			"من", "في", "على", "عن", "الى", "الي", "مع", "بعد", "قبل", "بين", "لكل", "حسب",
			"show", "give", "list", "tell", "want", "need", "please", "what", "which", "who",
			"where", "when", "how", "many", "much", "the", "our", "ours", "and", "for", "with",
			"about", "from", "into", "that", "this", "these", "those", "have", "has", "had",
			"are", "was", "were", "can", "could", "would", "should", "did", "does", "doing",
			"get", "got", "see", "look", "find", "know", "all", "any", "some", "per", "each",
			"company", "business", "data", "info", "information", "report", "number",
		};

		/// Built through the SAME normaliser the question goes through. Writing these folded by hand
		/// is how a list like this rots: Normalise turns ة into ه and أ into ا, so a hand-typed
		/// "سؤال" would sit in the set and never once match the "سوال" that arrives.
		private static readonly HashSet<string> Chatter =
			new(ChatterWords.Select(AskSearch.Normalise), StringComparer.Ordinal);

		protected abstract AskQuery DefaultQuery();

		// AUTHORITY IS THE SURFACE'S, not the handler's. A handler must never be able to widen what
		// the surface it reads is allowed to show, so these are forwarded rather than declared.
		public string Module => _surface.Module;
		public string Action => _surface.Action;

		/// Forwarded, never invented: the handler cannot widen what the surface it reads may show.
		public IReadOnlyCollection<string> ExtraActions => _surface.ColumnActions;

		/// `query` is the composed query as JSON. It is a VALUE that gets validated, never text that
		/// becomes a query — see AskQueryValidator.
		public IReadOnlyCollection<string> Parameters { get; } = new[] { "query" };

		public abstract AskText Description { get; }
		public abstract AskText Example { get; }

		/// SOMETHING THIS SURFACE MUST SAY ABOUT A PARTICULAR QUESTION before its answer.
		///
		/// Not a refusal and not a rule: a sentence for the case where the question is ABOUT
		/// something next door to what this surface holds, and the figure would be read as that
		/// thing. Null for every surface that has no such neighbour.
		public virtual AskText? Caveat(string? question) => null;
		public abstract IReadOnlyCollection<string> Keywords { get; }

		public async Task<AskAnswer> AnswerAsync(AskRequest request, AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			request.Parameters.TryGetValue("query", out var json);
			AskQuery? asked = null;
			// WHICH COMPOSER ANSWERED. Two questions took ten and thirty-four seconds, which means the
			// model composed what the rules should have resolved instantly - and there was no way to
			// tell from the outside. A number nobody can attribute is a number nobody can tune.
			var composedBy = "none";

			// A CALLER-SUPPLIED QUERY WINS, because whatever composed one already knows more than
			// a guess made from the sentence.
			if (!string.IsNullOrWhiteSpace(json)) asked = Parse(json!);

			// THE SENTENCE ITSELF, COMPOSED INTO A QUERY. This is the step that removes the need for
			// anyone to have thought of the question first. Note that the `query` parameter normally
			// carries leftover WORDS rather than JSON — the gate puts the residue in a handler's
			// first declared parameter — so failing to parse it is not a failed query. It is simply
			// no query, and reporting it as an error would blame the asker for the gate's habit.
			if (asked == null && !string.IsNullOrWhiteSpace(request.Question))
			{
				var composed = await _composer.ComposeAsync(request.Question, _surface, ct);
				if (composed != null)
				{
					composedBy = composed.Engine;
					// An empty measure means a RECORD query: rows ordered by the surface's date,
					// not a total. The composer says which it meant; this does not guess.
					var isRecord = string.IsNullOrWhiteSpace(composed.Measure);
					var dateCol = _surface.Columns
						.FirstOrDefault(c => c.Kind == AskColumnKind.Dimension && c.Name == "date");

					asked = isRecord && dateCol != null
						? new AskQuery
						{
							Surface = _surface.Code,
							GroupBy = Array.Empty<string>(),
							Measures = Array.Empty<AskMeasure>(),
							OrderBy = dateCol.Name,
							Descending = composed.Descending,
							Limit = composed.Limit,
						}
						: new AskQuery
						{
							Surface = _surface.Code,
							GroupBy = composed.GroupBy == null ? Array.Empty<string>() : new[] { composed.GroupBy },
							Measures = new[] { new AskMeasure { Column = composed.Measure, Agg = AskAgg.Sum } },
							OrderBy = composed.Measure,
							Descending = composed.Descending,
							Limit = composed.Limit,
							ComparePeriods = composed.Compare,
						};
				}
			}

			// A QUESTION THAT NAMES SOMETHING THIS SURFACE HAS NEVER HEARD OF IS REFUSED.
			//
			// This used to answer the surface's DEFAULT query instead, under a sentence admitting it
			// had not understood. The owner asked «بسأل عن احمد زكريا موظف» — about one named person
			// — and got a table of headcount by job title, beneath a note reading "I could not turn
			// your question into a query, so I answered the most common one: revenue by item", on a
			// surface that holds no revenue at all. Two defects in one answer: a confident table
			// under a question nobody asked, and a note describing a different surface's default.
			// The comment that used to sit here said real figures under a question nobody asked are
			// the only outcome worse than refusing — and then did it anyway, with a disclaimer. A
			// disclaimer does not turn a wrong answer into a right one; the reader sees the table.
			//
			// MY FIRST FIX WAS WRONG, AND THE PROBE CAUGHT IT. I refused whenever the composer
			// composed nothing, reasoning that a bare topic word always composes. That holds in
			// Arabic and not in English: "what did we buy" names the purchases surface exactly — it
			// is a declared keyword — yet composes nothing, because no single word in it is a column
			// or a measure. One regression out of 33, and it would have removed a capability to fix
			// a defect. A fix that quietly narrows the product is not a fix.
			//
			// So the test is not "did anything compose" but "does the question name something this
			// surface cannot address". A general question — "what did we buy", "المبيعات" — is built
			// entirely from words the surface knows: its keywords, its column names, labels and
			// synonyms. The default answer is honest for those. But "احمد زكريا" appears in no
			// vocabulary anywhere, so the asker is pointing at a particular person, document or
			// thing, and this surface groups and totals — it cannot single one out. That is the
			// question to hand back.
			//
			// Short tokens are ignored: Arabic particles (عن، من، في) and English function words
			// carry no subject, and refusing over one would refuse half of ordinary speech.
			// THE CHECK RUNS EVEN WHEN SOMETHING COMPOSED. It used to run only when the composer
			// produced nothing, and that left the worse half of the defect alive: «في عميل اسمه
			// النيل؟» composed fine, because "عميل" is a column — so the surface grouped revenue by
			// customer and returned a RANKING, having silently dropped the one word that said which
			// customer. «في مورد اسمه مورد الطاقة؟» did the same and looked correct purely because
			// that vendor happened to sort first. A name the surface ignores is worse than a name it
			// refuses: the answer arrives dressed as the right one.
			var unknown = string.IsNullOrWhiteSpace(request.Question)
				? (IReadOnlyList<string>)System.Array.Empty<string>()
				: UnknownWordsIn(request.Question!);
			AskFilter? namedFilter = null;
			AskText? namedNote = null;
			AskText? namedUnmatched = null;
			AskFilter? extraFilter = null;

			// A NAMED DAY IS A FILTER, NOT A WORD TO IGNORE. "مبيعات امبارح" reached no surface at
			// all, and the first thing that would have happened if it had is the worse outcome: the
			// surface knows "مبيعات" and not "امبارح", so it would have answered the revenue of ALL
			// TIME under a question about one day. A time word that cannot be honoured must never be
			// quietly dropped - it changes which question is being answered.
			//
			// Surfaces with no date dimension (the workforce) skip this untouched, which is the
			// honest behaviour: a roster has no "yesterday".
			var window = RelativeWindow(request.Question);
			if (window != null)
			{
				namedFilter = window.Filter;
				namedNote = window.Note(arabic);
				// Its trigger words are now accounted for, so they must not also be hunted as names.
				unknown = unknown.Where(w => !window.Words.Contains(AskSearch.Normalise(w))).ToList();
			}

			if (unknown.Count > 0)
			{
				{
					// BEFORE REFUSING, SEE WHETHER THOSE WORDS NAME A RECORD HERE.
					//
					// The owner asked «في موظف اسمه احمد زكريا؟» and the refusal below told him
					// those words meant nothing on this surface. It was true of the surface and
					// false of the product: every employee's name is right there. A question about
					// a particular person is not an exotic request, and «is there an employee
					// called X» has a correct answer either way — the roster either holds one or
					// it does not, and BOTH of those are answers, not refusals.
					//
					// Only a surface that declares NameKey takes this path, so nothing that
					// aggregates lines starts scanning itself for proper nouns.
					// A NAME LOOKUP THAT FOUND NOTHING MUST NOT CLOSE THE DOOR.
					//
					// It used to answer "nobody by that name" and stop. «الاطباء الزائرين» then
					// came back as "I searched 8 records and none matches «الزائرين»" — and the
					// word is not a name at all, it is one of the engagement's VALUES. The right
					// order is: a record by that name, then a value by that name, and only then
					// the admission that nothing was found.
					var found = await LookupByNameAsync(unknown, scope, arabic, ct, request.RepeatsLastRecord);
					if (found != null && found.Rows.Count > 0) return found;

					// NOT A RECORD HERE, BUT PERHAPS A VALUE IN ONE OF THE COLUMNS. "النيل" is not
					// a word this surface knows, and it IS one of its customers — so it becomes a
					// filter, and the question is answered about the thing it named. This is also
					// what makes "مبيعات مياه معدنية" work, which nobody had to build separately.
					var match = await NamedValueAsync(unknown, scope, ct);
					// Nothing named and nothing valued: now the empty name lookup is the answer.
					if (match == null && found != null) return found;
					if (match != null)
					{
						// A window and a name can BOTH be true - "مبيعات مياه معدنية امبارح" is one
						// question with two conditions, and dropping either answers a different one.
						// Filters are a list; this is simply the second entry.
						if (namedFilter == null)
						{
							namedFilter = match.Filter;
							namedNote = match.Note(arabic);
						}
						else
						{
							extraFilter = match.Filter;
							namedNote = AskText.Of(namedNote!.Ar + " · " + match.Note(arabic).Ar,
												   namedNote!.En + " · " + match.Note(arabic).En);
						}
					}
					else if (asked != null)
					{
						// Composed, but the name meant nothing anywhere on this surface. The answer
						// below is about something broader than the question, and saying so is the
						// difference between an assumption and a mistake.
						namedUnmatched = AskText.Of(
							"مالقيتش «" + string.Join("، ", unknown) + "» في البيانات دي، فالرد ده عن الكل:",
							"I could not find \"" + string.Join(", ", unknown) + "\" in this data, so this answer covers everything:");
						namedNote = namedUnmatched;
					}
					else

					{
					var dims = string.Join("، ", _surface.Columns
						.Where(c => c.Kind == AskColumnKind.Dimension)
					.Select(c => c.Label.ForCulture(CultureInfo.CurrentUICulture)));
					return AskAnswer.Refuse(AskRefusal.NotUnderstood, AskText.Of(
						"سؤالك فيه «" + string.Join("، ", unknown) + "» وده مش حاجة أقدر أبحث بيها هنا. "
						+ "أنا بتعامل مع " + _surface.Description.Ar + "، وبقدر أجمّع حسب: " + dims + ".",
						"Your question mentions \"" + string.Join(", ", unknown) + "\", which is not "
						+ "something I can look up here. I work with " + _surface.Description.En
						+ ", and I can group by: " + dims + ".",
						"Votre question mentionne « " + string.Join(", ", unknown) + " », un élément que je ne peux pas rechercher ici. "
						+ "Je traite " + _surface.Description.Fr + " et peux regrouper par : " + dims + "."));
					}
				}
			}

			var query = asked ?? DefaultQuery();

			// "LIST" MEANS A LIST. «جيب قائمة الاطباء» came back as "8" — the owner asked WHO and was
			// told HOW MANY. The word "قائمة" matched the headcount measure's own synonyms, so the
			// question composed a total and the total was true and was not the answer.
			//
			// A surface whose rows ARE records (it declares a NameKey) can be asked for them, and
			// the validator already allows a measureless query, bounded and ordered, up to
			// MaxRecords. A surface that aggregates lines has no list to give and is untouched.
			// ...BUT A LIST **OF** SOMETHING IS A GROUPING, NOT THE RECORDS.
			//
			// «جيب قائمة الاطباء» names no dimension, so the records ARE the answer. «جيب قائمة
			// العيادات» names one — the clinic — and the answer is the clinics, not the doctors
			// sitting in them. Reading both as "give me the records" handed the owner the same
			// list of doctors twice under two different questions.
			// EVERYTHING THIS SURFACE HOLDS ABOUT THE ONE IT JUST NAMED.
			//
			// «جيب بياناته» after finding a doctor is the most ordinary move in a conversation and
			// it was refused: "his" names no value, so nothing could be narrowed. The answer is the
			// same record again — which IS everything here — and the sentence has to say so, or
			// repeating the row looks like the question was ignored.
			if (request.RepeatsLastRecord)
				namedNote = AskText.Of(
					"ده كل اللي مسجَّل عنه هنا. افتح ملفه من الاسم للتفاصيل الكاملة:",
					"This is everything recorded here. Open the file from the name for the rest:");

			// A FOLLOW-UP KEEPS THE SHAPE OF WHAT IT NARROWS.
			//
			// The owner asked for the LIST of doctors, then said «الاسنان فقط», and got a COUNT.
			// He narrowed a list and was handed a different kind of answer about the same subject —
			// true, and not what he asked twice for. ContinuesAList carries that forward.
			if ((WantsAList(request.Question) || request.ContinuesAList)
				&& _surface.NameKey != null && query.Measures.Count > 0
				&& (asked == null || asked.GroupBy.Count == 0 || request.ContinuesAList))
				query = new AskQuery
				{
					Surface = query.Surface,
					GroupBy = System.Array.Empty<string>(),
					Measures = System.Array.Empty<AskMeasure>(),
					Filters = query.Filters,
					// Ordered by the first dimension it has, because a record query must say how it
					// is ordered and "the order they happen to come back in" is not an order.
					OrderBy = _surface.Columns.First(c => c.Kind == AskColumnKind.Dimension).Name,
					Descending = false,
					Limit = AskQueryValidator.MaxRecords,
				};

			// The named value joins the query as an ordinary filter, which means it is validated
			// like any other - the column must exist and be this surface's. Nothing here builds
			// text that becomes a query.
			if (namedFilter != null)
				query = new AskQuery
				{
					Surface = query.Surface,
					GroupBy = query.GroupBy,
					Measures = query.Measures,
					Filters = query.Filters
						.Concat(new[] { namedFilter })
						.Concat(extraFilter == null ? System.Array.Empty<AskFilter>() : new[] { extraFilter })
						.ToList(),
					OrderBy = query.OrderBy,
					Descending = query.Descending,
					Limit = query.Limit,
					ComparePeriods = query.ComparePeriods,
				};

			// A COLUMN THE CALLER MAY NOT READ IS REFUSED, AND NAMED.
			//
			// Dropping it quietly would answer a different question with a number that looks like an
			// answer; refusing without saying which column sends someone to rephrase a question that
			// was never the problem. This is also the first thing that makes AskScope mean anything:
			// it carried the caller's roles for weeks and nothing ever read them.
			var barred = query.Measures.Select(m => m.Column)
				.Concat(query.GroupBy)
				.Select(n => _surface.Column(n))
				.Where(c => c != null && !scope.Holds(c!.Action))
				.Select(c => c!.Label.ForCulture(CultureInfo.CurrentUICulture))
				.Distinct()
				.ToList();

			if (barred.Count > 0)
				return AskAnswer.Refuse(AskRefusal.NotPermitted, AskText.Of(
					"مش من صلاحيتك تشوف " + string.Join("، ", barred) + " هنا. "
					+ "تقدر تسأل عن باقي أعمدة هذا السطح عادي.",
					"You are not permitted to see " + string.Join(", ", barred) + " here. "
					+ "The rest of this surface's columns are open to you.",
					"Vous n'êtes pas autorisé à consulter " + string.Join(", ", barred) + " ici. "
					+ "Vous pouvez demander les autres colonnes de cette vue."));

			var validation = AskQueryValidator.Validate(query, _surface);

			if (!validation.Ok)
			{
				// A REFUSAL THAT NAMES THE FIELD IT DID NOT RECOGNISE. "I do not understand" would
				// send the asker to rephrase a question that was fine; naming the invented column
				// tells whoever composed it exactly what to fix.
				var why = AskQueryValidator.Explain(validation, arabic);
				return new AskAnswer
				{
					Answered = false,
					Refusal = AskRefusal.NotUnderstood,
					Message = why,
					// THIS SURFACE'S OWN EXAMPLE, NOT SALES'. These two were hardcoded to revenue
					// by item and by month, in a base class five surfaces share — so a workforce
					// query that failed validation offered the asker questions about money.
					Suggestions = new[]
					{
						new AskSuggestion
						{
							Label = arabic ? Example.Ar : Example.En,
							Question = arabic ? Example.Ar : Example.En,
						},
					},
				};
			}

			var clean = validation.Clean!;
			var rows = await _surface.RowsAsync(scope, ct);
			var result = AskSurfaceRunner.Run(clean, rows, _surface);

			if (result.Rows.Count == 0)
				return new AskAnswer
				{
					Answered = false,
					Refusal = AskRefusal.NoData,
					// NAMING THE FILTER THAT EMPTIED IT. "Nothing matches those conditions" sends the
					// asker to doubt the whole surface; "nothing for النيل" tells them the one fact
					// they asked for, which is usually the answer rather than the failure.
					Message = AskText.Of(
						rows.Count == 0
							? "لا توجد بيانات في هذه الشركة على هذا السطح."
							: namedFilter != null
								? "مفيش حاجة مسجَّلة لـ«" + namedFilter.Value + "» هنا."
								: "لا يوجد سطر يطابق هذه الشروط. السطور موجودة، لكن التصفية لم تُبقِ شيئًا.",
						rows.Count == 0
							? "There is no data on this surface in this company."
							: namedFilter != null
								? "Nothing is recorded for \"" + namedFilter.Value + "\" here."
								: "No row matches those conditions. Rows exist, but the filter left none.",
						rows.Count == 0
							? "Aucune donnée n'est disponible dans cette vue pour cette entreprise."
							: namedFilter != null
								? "Aucune donnée n'est enregistrée ici pour « " + namedFilter.Value + " »."
								: "Aucune ligne ne correspond à ces critères. Des lignes existent, mais les filtres n'en ont retenu aucune."),
					Understood = _surface.Rules,
				};

			var shaped = Shape(result, clean, arabic);

			return new AskAnswer
			{
				Answered = true,
				IsRecordList = clean.Measures.Count == 0,
				Rows = shaped,
				// AN ASSUMPTION IS SAID OUT LOUD, not filed in the rules line. When the named thing
				// was not found, the answer that follows is about everything — and a reader who
				// only reads the headline would otherwise take a ranking as the answer to a
				// question about one customer. It stays an answer rather than a refusal on purpose:
				// the word might be unknown because the scaffolding list has a gap, and a gap there
				// must not be able to destroy a question that was perfectly good.
				Spoken = Prefixed(
					Prefixed(Speak(result, clean, shaped, arabic), namedUnmatched),
					Caveat(request.Question)),
				// THE NAMED THING IS NAMED IN THE ANSWER. A filtered total and an unfiltered one
				// look identical on screen, so the sentence has to carry the difference.
				Understood = namedNote == null
					? Explain(result, clean, arabic, composedBy)
					: AskText.Of(namedNote.Ar + " · " + Explain(result, clean, arabic, composedBy).Ar,
								 namedNote.En + " · " + Explain(result, clean, arabic, composedBy).En,
								 namedNote.Fr + " · " + Explain(result, clean, arabic, composedBy).Fr),
				Notes = _surface.Rules,
				Provenance = new AskProvenance
				{
					CompanyId = scope.CompanyId,
					Sources = Sources,
					Authority = _surface.Module + ":" + _surface.Action,
					// Rows a NULLABLE measure could not see. Not a permission exclusion, but the
					// same promise: a total that silently skipped part of its input reads complete.
					RowsWithheld = result.Coverage.Sum(c => c.Skipped),
					WithheldNote = result.Coverage.All(c => c.Skipped == 0) ? null : AskText.Of(
						string.Join(" · ", result.Coverage.Where(c => c.Skipped > 0).Select(c =>
							$"{c.Column}: {c.Skipped} من {result.RowsRead} سطر بلا قيمة مسجَّلة، فهي خارج هذا الحساب")),
						string.Join(" · ", result.Coverage.Where(c => c.Skipped > 0).Select(c =>
							$"{c.Column}: {c.Skipped} of {result.RowsRead} lines have no recorded value and are outside this calculation"))),
				},
			};
		}

		/// THE LINK THE ROW EARNED, or none.
		///
		/// Prefers the record's own screen over the invoice it sits on: asked about an item, the
		/// useful place to land is the item. Returns null rather than a guess — see the runner, which
		/// already refused to attach a link to a group covering more than one record.
		private string? LinkOf(IReadOnlyDictionary<string, object?> r, AskQuery query)
		{
			// GROUPED BY ONE THING: open THAT thing. Grouping by vendor and linking to the item would
			// be a link that works and lands somewhere nobody asked about.
			if (query.GroupBy.Count == 1)
				return Pick(r, AskSurface.LinkFor(query.GroupBy[0]));

			// A RECORD ROW, or a grouping across several dimensions, has no single subject. The item
			// is the most useful landing place when there is one, the invoice next. Both are a
			// judgement about usefulness, and both are only reached when the row genuinely is about
			// one record — the runner has already refused to carry a link that was not.
			return Pick(r, AskSurface.LinkFor("item")) ?? Pick(r, AskSurface.LinkFor("invoice"));
		}

		private static string? Pick(IReadOnlyDictionary<string, object?> r, string key) =>
			r.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;

		/// A RECORD ROW reads as itself: what it is, when, and what it was worth. No aggregate to
		/// headline, so the money column carries the row's own value.
		private List<IReadOnlyDictionary<string, object?>> ShapeRecords(
			AskSurfaceRunner.Result result, bool arabic, AskQuery query)
		{
			var inv = CultureInfo.InvariantCulture;
			return result.Rows.Select(r =>
			{
				var invoiceNo = r.TryGetValue("invoice", out var no) ? Convert.ToString(no) : null;
				var date = r.TryGetValue("date", out var d) ? Convert.ToString(d) : null;
				var customer = r.TryGetValue("customer", out var c) ? Convert.ToString(c) : null;
				var item = r.TryGetValue("item", out var it) ? Convert.ToString(it) : null;
				var revenue = r.TryGetValue("revenue", out var v) ? v as decimal? : null;

				// A SURFACE THAT NAMES ITS RECORDS GETS TO NAME THEM. This was written for the sales
				// surface, where a record is an invoice and its name is the invoice number, and it
				// fell back to "(record)" for everything else: «جيب قائمة الاطباء» returned eight
				// rows all called "(سجل)". A surface with a NameKey already knows the answer.
				var named = _surface.NameKey is string nk && r.TryGetValue(nk, out var nv)
					? Convert.ToString(nv) : null;
				var facts = named == null ? null : string.Join(" · ", _surface.Columns
					.Where(c => c.Kind == AskColumnKind.Dimension)
					.Select(c => r.TryGetValue(c.Name, out var fv) ? Convert.ToString(fv) : null)
					.Where(x => !string.IsNullOrWhiteSpace(x)));

				return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = !string.IsNullOrWhiteSpace(named) ? named
						: string.IsNullOrWhiteSpace(invoiceNo)
							? (date ?? (arabic ? "(سجل)" : "(record)"))
							: invoiceNo,
					["code"] = named != null ? facts : date,
					["warehouse"] = named != null ? null : string.Join(" · ", new[] { customer, item }
						.Where(x => !string.IsNullOrWhiteSpace(x))),
					["photo"] = r.TryGetValue(AskSurface.PhotoKey, out var ph) ? Convert.ToString(ph) : null,
					["href"] = named != null && r.TryGetValue(AskSurface.RecordHrefKey, out var hr)
						? Convert.ToString(hr) : LinkOf(r, query),
					["onHand"] = revenue == null ? (decimal?)null : decimal.Round(revenue.Value, 2),
					["onHandText"] = revenue?.ToString("#,##0.00", inv),
				};
			}).ToList();
		}

		/// THE ANSWER IN ONE SENTENCE, from the rows that were already computed.
		///
		/// No model writes this. It names the top group and its figure when the question was grouped,
		/// or the single total when it was not, and it says how many groups there were so the reader
		/// knows whether the grid below holds more. A sentence that cannot be checked against the rows
		/// underneath it would be worse than no sentence.
		private AskText? Speak(
			AskSurfaceRunner.Result result, AskQuery query,
			IReadOnlyList<IReadOnlyDictionary<string, object?>> shaped, bool arabic)
		{
			if (shaped.Count == 0) return null;
			// A RECORD LIST SAYS HOW IT IS ORDERED, because it is not always by time.
			//
			// This said "the N most recent records, newest first" for every measureless query,
			// which was true while the only one was "the last invoices". «جيب قائمة الاطباء» is
			// ordered by DEPARTMENT, and the sentence above it told the reader it was the eight
			// newest doctors — a claim about the list that the list does not make.
			if (query.Measures.Count == 0)
			{
				var byCol = _surface.Column(query.OrderBy ?? "");
				var byName = byCol?.Label.ForCulture(CultureInfo.CurrentUICulture);
				var byTime = query.OrderBy is "date" or "month" && query.Descending;

				if (shaped.Count == 1)
					return byTime
						? AskText.Of("آخر سجل: " + shaped[0]["name"] + ".",
									 "Most recent: " + shaped[0]["name"] + ".",
									 "Le plus récent : " + shaped[0]["name"] + ".")
						: AskText.Of("سجل واحد: " + shaped[0]["name"] + ".",
									 "One record: " + shaped[0]["name"] + ".",
									 "Un seul résultat : " + shaped[0]["name"] + ".");

				return byTime
					? AskText.Of($"آخر {shaped.Count} سجلات، الأحدث أولًا.",
								 $"The {shaped.Count} most recent records, newest first.",
								 $"Les {shaped.Count} résultats les plus récents, du plus récent au plus ancien.")
					: AskText.Of(
						$"{shaped.Count} سجلات" + (byName == null ? "." : $"، مرتَّبة حسب {byName}."),
						$"{shaped.Count} records" + (byName == null ? "." : $", ordered by {byName}."),
						$"{shaped.Count} résultats" + (byName == null ? "." : $", triés par {byName}."));
			}

			var measureCol = _surface.Column(query.Measures[0].Column);
			var measureName = measureCol?.Label.ForCulture(CultureInfo.CurrentUICulture) ?? "";
			var top = shaped[0];
			var value = top.TryGetValue("onHandText", out var v) ? Convert.ToString(v) : null;
			var label = top.TryGetValue("name", out var n) ? Convert.ToString(n) : null;

			// A COMPARISON'S HEADLINE IS THE CHANGE, NOT THE CURRENT NUMBER.
			//
			// "الإيراد: 103039.85" is a true sentence and the wrong answer to "how does it compare".
			// The two periods are NAMED rather than called "this month" and "last month", because
			// they come from the data and may not be the calendar's — a reader has to be able to see
			// which periods were weighed before trusting the direction.
			if (result.Period != null && result.PriorPeriod != null && shaped.Count > 0)
			{
				var lead = shaped[0];
				var prior = lead.TryGetValue("warehouse", out var cw) ? Convert.ToString(cw) : null;
				var who = query.GroupBy.Count == 0
					? ""
					: " (" + Convert.ToString(lead.TryGetValue("name", out var nm) ? nm : "") + ")";

				// The row's own comparison phrase is reused rather than recomputed, so the sentence
				// and the row can never disagree about the same numbers.
				return AskText.Of(
					$"{measureName} في {result.Period}: {value}{who} — مقارنة بـ{result.PriorPeriod}: {prior}.",
					$"{measureName} in {result.Period}: {value}{who} — against {result.PriorPeriod}: {prior}.",
					$"{measureName} en {result.Period} : {value}{who} — comparé à {result.PriorPeriod} : {prior}.");
			}

			// Ungrouped: there is one number and it is the answer.
			if (query.GroupBy.Count == 0)
				return AskText.Of($"{measureName}: {value}.", $"{measureName}: {value}.", $"{measureName} : {value}.");

			var groupCol = _surface.Column(query.GroupBy[0]);
			var groupName = groupCol?.Label.ForCulture(CultureInfo.CurrentUICulture) ?? "";
			// ARABIC DEFINITENESS, WITHOUT GUESSING AT MORPHOLOGY.
			//
			// "أعلى الصنف" is not Arabic — that construction wants an indefinite noun, while the
			// column labels are definite because that is how a table heading reads. This used to
			// strip "ال" off the label's first word, which is right for "الصنف" and wrong the
			// moment a label is more than one word: "المسمى الوظيفي" became "مسمى الوظيفي", an
			// indefinite noun carrying a definite adjective, which agrees with nothing. The owner
			// read that sentence. Stripping every word instead would break an إضافة like "كود
			// الصنف", where the second noun must keep its article — so there is no rule here that
			// is right in both cases, only a rule that is wrong in different places.
			//
			// So the sentence is built to take the label AS IT IS. "الأعلى في X حسب Y" needs no
			// agreement from Y at all, and reads naturally for every label this surface can have.
			var more = result.GroupsFound > shaped.Count
				? (arabic ? $" ومعروض {shaped.Count} من {result.GroupsFound}."
					: CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr"
						? $" Affichage de {shaped.Count} sur {result.GroupsFound}."
						: $" Showing {shaped.Count} of {result.GroupsFound}.")
				: "";

			// NOTHING IS "THE HIGHEST" WHEN THERE IS ONE OF IT.
			//
			// After «الاسنان فقط» narrowed the list to one department, the answer read "the highest
			// department by practitioner count: Dentistry, with 1" — a superlative over a set of
			// one, which is how the owner described it: strange. A ranking sentence is earned by
			// having something to rank.
			if (result.GroupsFound == 1 && shaped.Count == 1)
				return AskText.Of($"{label}: {value} ({measureName}).",
								  $"{label}: {value} ({measureName}).",
								  $"{label} : {value} ({measureName}).");

			return query.Descending
				? AskText.Of(
					$"الأعلى في {measureName} حسب {groupName}: {label} بـ{value}.{more}",
					$"Highest {groupName} by {measureName}: {label} at {value}.{more}",
					$"Valeur la plus élevée pour {groupName}, selon {measureName} : {label}, avec {value}.{more}")
				: AskText.Of(
					$"الأقل في {measureName} حسب {groupName}: {label} بـ{value}.{more}",
					$"Lowest {groupName} by {measureName}: {label} at {value}.{more}",
					$"Valeur la plus faible pour {groupName}, selon {measureName} : {label}, avec {value}.{more}");
		}

		/// Rows in the shape the widget already renders, so no screen change is needed.
		private List<IReadOnlyDictionary<string, object?>> Shape(
			AskSurfaceRunner.Result result, AskQuery query, bool arabic)
		{
			var inv = CultureInfo.InvariantCulture;
			// A record query has no headline measure: each row speaks for itself.
			if (query.Measures.Count == 0) return ShapeRecords(result, arabic, query);
			var headline = query.Measures[0].Column;
			var headlineCol = _surface.Column(headline);
			var extras = query.Measures.Skip(1).ToList();

			return result.Rows.Select(r =>
			{
				var label = query.GroupBy.Count > 0
					? string.Join(" · ", query.GroupBy.Select(g => r.TryGetValue(g, out var v) ? Convert.ToString(v, inv) : null)
						.Where(s => !string.IsNullOrWhiteSpace(s)))
					: (arabic ? "الإجمالي" : "Total");

				var value = r.TryGetValue(headline, out var hv) ? hv as decimal? : null;

				// THE COMPARISON GOES IN THE SECONDARY LINE, in words rather than as a bare number.
				//
				// "كان 40 · +12 (+30%)" is readable; a column of signed numbers beside a column of
				// values is not. And where the previous period has no value at all the row says
				// NEW rather than showing a change, because there is none to show — a baseline that
				// does not exist must never be rendered as zero.
				string? ComparisonText()
				{
					if (result.Period == null) return null;
					var prior = r.TryGetValue(AskSurfaceRunner.PriorValueKey, out var pv) ? pv as decimal? : null;
					var change = r.TryGetValue(AskSurfaceRunner.ChangeKey, out var cv) ? cv as decimal? : null;
					var pct = r.TryGetValue(AskSurfaceRunner.ChangePercentKey, out var pp) ? pp as decimal? : null;

					var cur = r.TryGetValue(headline, out var cvv) ? cvv as decimal? : null;
					if (prior == null)
						return arabic ? "جديد هذه الفترة" : "new this period";
					// THE SAME GROUPING AS THE FIGURE IT SITS BESIDE. The headline learned to write
					// 103,039.85 and this line kept writing 103039.85, so one answer carried two
					// number formats and the comparison read as the less careful of the two.
					string Grouped(decimal d) => decimal.Round(d, 2).ToString("#,##0.##", inv);

					if (cur == null)
						return (arabic ? "اختفى — كان " : "gone — was ") + Grouped(prior.Value);

					var was = (arabic ? "كان " : "was ") + Grouped(prior.Value);
					if (change == null) return was;
					var sign = change.Value >= 0 ? "+" : "-";
					var delta = sign + Grouped(Math.Abs(change.Value));
					// NO PERCENTAGE WHERE THE BASE WAS ZERO. The runner already returned null for
					// it; this only has to refrain from inventing one.
					var pctText = pct == null ? "" : $" ({sign}{decimal.Round(Math.Abs(pct.Value), 1).ToString(inv)}%)";
					return was + " · " + delta + pctText;
				}

				var comparison = ComparisonText();

				var secondary = extras.Count == 0 ? null : string.Join(" · ", extras.Select(m =>
				{
					var mv = r.TryGetValue(m.Column, out var x) ? x as decimal? : null;
					var col = _surface.Column(m.Column);
					var name = col?.Label.ForCulture(CultureInfo.CurrentUICulture) ?? m.Column;
					// UNKNOWN IS SAID, NOT SHOWN AS A DASH THAT READS AS ZERO.
					return mv == null
						? $"{name}: " + (arabic ? "غير معروف" : "unknown")
						: $"{name}: " + decimal.Round(mv.Value, 2).ToString(inv);
				}));

				return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = string.IsNullOrWhiteSpace(label) ? (arabic ? "(فارغ)" : "(blank)") : label,
					["code"] = null,
					// The comparison wins the secondary line when there is one: it is what was asked.
					["warehouse"] = comparison ?? secondary,
					["href"] = LinkOf(r, query),
					["onHand"] = value == null ? (decimal?)null : decimal.Round(value.Value, 2),
					// GROUPED DIGITS. "72124.24" has to be counted a digit at a time before it can be
					// read as seventy-two thousand; "72,124.24" is read at a glance. The figure is
					// the point of the answer, so the one place it must not cost effort is here.
					// Invariant grouping on purpose: the comma-and-dot convention is what the rest
					// of this product's screens already use, and an answer that formats numbers
					// differently from the list beside it reads as a different system.
					["onHandText"] = value == null
						? (arabic ? "غير معروف" : "unknown")
						: (headlineCol?.IsMoney == true
							? value.Value.ToString("#,##0.00", inv)
							: decimal.Round(value.Value, 2).ToString("#,##0.##", inv)),
				};
			}).ToList();
		}

		/// What was actually asked, what it covered, and what the surface had already decided.
		private AskText Explain(AskSurfaceRunner.Result result, AskQuery query, bool arabic, string engine)
		{
			_ = engine;   // recorded by the caller's provenance; never shown to the reader

		string Names(IEnumerable<string> cols) => string.Join(" + ", cols.Select(c =>
			{
				var col = _surface.Column(c);
				return col?.Label.ForCulture(CultureInfo.CurrentUICulture) ?? c;
			}));

			var french = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr";
			var measures = Names(query.Measures.Select(m => m.Column));
			var groups = query.GroupBy.Count == 0
				? (arabic ? "الإجمالي" : french ? "le total" : "the total")
				: Names(query.GroupBy);

			var span = result.FromDate == null
				? ""
				: (arabic
					? $" · الفترة المغطاة فعليًا: {result.FromDate} إلى {result.ToDate}"
					: french
						? $" · période réellement couverte : {result.FromDate} à {result.ToDate}"
						: $" · actual period covered: {result.FromDate} to {result.ToDate}");

			// THE ENGINE THAT COMPOSED THIS IS NOT NEWS TO THE READER. "[rules]" was printing into
			// the answer as if it meant something; it is the name of the composer and belongs in
			// the log, not on screen. The surface's own rules used to be appended here too and are
			// now carried separately as Notes, so this line says ONE thing: what was computed over
			// how much, and when.
			var ar = $"{measures} حسب {groups} · {result.GroupsFound} مجموعة من {result.RowsRead} سطر{span}";
			var en = $"{measures} by {groups} · {result.GroupsFound} group(s) from {result.RowsRead} line(s){span}";
			var fr = $"{measures} par {groups} · {result.GroupsFound} groupe(s) sur {result.RowsRead} ligne(s){span}";
			return AskText.Of(ar, en, fr);
		}

		/// Tolerant of shape, strict about content: anything it cannot read becomes null and the
		/// caller substitutes a known-good question rather than guessing at what was meant.
		private static AskQuery? Parse(string json)
		{
			try
			{
				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;
				if (root.ValueKind != JsonValueKind.Object) return null;

				List<string> Strings(string name) =>
					root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array
						? el.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
							.Select(x => x.GetString()!).ToList()
						: new List<string>();

				var measures = new List<AskMeasure>();
				if (root.TryGetProperty("measures", out var ms) && ms.ValueKind == JsonValueKind.Array)
				{
					foreach (var m in ms.EnumerateArray())
					{
						if (m.ValueKind == JsonValueKind.String)
						{
							measures.Add(new AskMeasure { Column = m.GetString()!, Agg = AskAgg.Sum });
						}
						else if (m.ValueKind == JsonValueKind.Object)
						{
							var col = m.TryGetProperty("column", out var c) ? c.GetString() : null;
							if (string.IsNullOrWhiteSpace(col)) continue;
							var agg = AskAgg.Sum;
							if (m.TryGetProperty("agg", out var a) && a.ValueKind == JsonValueKind.String)
								Enum.TryParse(a.GetString(), ignoreCase: true, out agg);
							measures.Add(new AskMeasure { Column = col!, Agg = agg });
						}
					}
				}

				var filters = new List<AskFilter>();
				if (root.TryGetProperty("filters", out var fs) && fs.ValueKind == JsonValueKind.Array)
				{
					foreach (var f in fs.EnumerateArray())
					{
						if (f.ValueKind != JsonValueKind.Object) continue;
						var col = f.TryGetProperty("column", out var c) ? c.GetString() : null;
						if (string.IsNullOrWhiteSpace(col)) continue;
						var op = AskOp.Equals;
						if (f.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String)
							Enum.TryParse(o.GetString(), ignoreCase: true, out op);
						filters.Add(new AskFilter
						{
							Column = col!,
							Op = op,
							Value = f.TryGetProperty("value", out var v) ? (v.ToString() ?? "") : "",
							Value2 = f.TryGetProperty("value2", out var v2) ? v2.ToString() : null,
						});
					}
				}

				return new AskQuery
				{
					Surface = root.TryGetProperty("surface", out var s) ? (s.GetString() ?? AskSalesSurface.SurfaceCode)
						: AskSalesSurface.SurfaceCode,
					GroupBy = Strings("groupBy"),
					Measures = measures,
					Filters = filters,
					OrderBy = root.TryGetProperty("orderBy", out var ob) ? ob.GetString() : null,
					Descending = !root.TryGetProperty("descending", out var de)
						|| de.ValueKind != JsonValueKind.False,
					Limit = root.TryGetProperty("limit", out var lim) && lim.TryGetInt32(out var l) ? l : 10,
				};
			}
			catch (JsonException)
			{
				return null;
			}
		}
	}
}
