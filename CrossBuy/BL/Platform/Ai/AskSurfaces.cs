using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// A GOVERNED DATA SURFACE — the answer to "you are assuming I can predict the question".
	//
	// THE PROBLEM WITH WHAT CAME BEFORE. Twelve hand-written questions means twelve questions, and
	// every thirteenth one a person asks is a failure. That was demonstrated three times in one
	// session: "أعلى الأصناف مبيعًا" failed, so a handler was written; "هامش الربح" failed, so
	// another was. The ceiling was never the understanding — it was that someone had to have thought
	// of the question first, and nobody can think of them all.
	//
	// WHAT THE CLOSED CATALOGUE WAS ACTUALLY BUYING. Not the list. The list was incidental. What it
	// bought is that NOTHING THE MODEL PRODUCES BECOMES A QUERY: it could only ever return an id,
	// and the company, the authority and the row filter were applied afterwards by the server. That
	// is the property worth keeping, and it does not require a list.
	//
	// SO THE MODEL COMPOSES A QUERY INSTEAD OF PICKING A NAME — over columns a surface DECLARES,
	// validated against that declaration before anything runs, with the scope injected after the
	// model is finished. An undeclared column is refused. An unbounded limit is refused. A filter
	// value is a parameter, never text spliced into anything. The model gains expressiveness and no
	// reach whatsoever.
	//
	// THE REAL COST IS CORRECTNESS, NOT SECURITY, AND THIS IS WHERE IT IS PAID.
	//
	// Every defect fixed in this service was a piece of domain judgement: exclude reversed invoices;
	// multiply by the invoice's own exchange rate; "never moved" is not "balance zero"; an average
	// over the invoices that were collected is not the collection period. A generated query carries
	// none of that and would rediscover all of it, confidently and wrongly.
	//
	// Which is why the judgement moves INTO THE SURFACE rather than disappearing. A surface does not
	// expose a raw column that can be misread: the sales surface has no LineTotal, it has `revenue`
	// already converted to base currency, and it cannot see a draft or a reversal because those rows
	// never enter it. A question asked over such a surface is then correct BY CONSTRUCTION rather
	// than by whoever wrote the handler having remembered. `Rules` states what was baked in, because
	// a guarantee the reader cannot see is a guarantee they cannot rely on.
	// =================================================================================================

	public enum AskColumnKind
	{
		/// Something to group or filter by.
		Dimension,
		/// Something to add up. Only a measure may be aggregated.
		Measure,
	}

	public sealed class AskColumn
	{
		/// The name a query may use. Lower-case, stable, and the ONLY vocabulary a caller gets.
		public string Name { get; init; } = "";
		public AskColumnKind Kind { get; init; }
		public AskText Label { get; init; } = AskText.Of("", "");

		/// Money is shown to two places and is promised to be in the base currency already. A
		/// surface that cannot keep that promise must not mark a column as money.
		public bool IsMoney { get; init; }

		/// Words that MEAN this column, beyond its name and label.
		///
		/// Declared by the surface because they are facts about this data: that "زباين" is the
		/// customer column and "بعنا" is revenue is domain knowledge, not something a composer
		/// should have to be taught once per surface. A deterministic composer can then answer the
		/// ordinary question instantly and leave the language model for the unusual one.
		public IReadOnlyList<string> Synonyms { get; init; } = System.Array.Empty<string>();

		/// AN ACTION THIS COLUMN NEEDS BEYOND THE SURFACE'S OWN. Null means the surface's action is
		/// enough. Margin is the case it exists for: accounting treats what the company EARNED as
		/// strictly more confidential than what it BILLED, and holds that an Accountant who may read
		/// the registers still may not read the margin on them.
		public string? Action { get; init; }

		/// True when this measure can be absent for a row — a cost that was never recorded, for
		/// instance. Such a row is excluded from that measure and COUNTED, never read as zero.
		public bool Nullable { get; init; }
	}

	public enum AskAgg { Sum, Count, Average, Min, Max }

	public enum AskOp { Equals, NotEquals, Contains, GreaterThan, LessThan, Between }

	public sealed class AskMeasure
	{
		public string Column { get; init; } = "";
		public AskAgg Agg { get; init; } = AskAgg.Sum;
	}

	public sealed class AskFilter
	{
		public string Column { get; init; } = "";
		public AskOp Op { get; init; } = AskOp.Equals;
		/// Always a VALUE, never a fragment. It is compared, never concatenated into anything.
		public string Value { get; init; } = "";
		public string? Value2 { get; init; }
	}

	/// What a caller may ask of a surface. Every field is checked against the surface's declaration
	/// before a single row is read.
	public sealed class AskQuery
	{
		public string Surface { get; init; } = "";
		public IReadOnlyList<string> GroupBy { get; init; } = Array.Empty<string>();
		public IReadOnlyList<AskMeasure> Measures { get; init; } = Array.Empty<AskMeasure>();
		public IReadOnlyList<AskFilter> Filters { get; init; } = Array.Empty<AskFilter>();
		public string? OrderBy { get; init; }
		public bool Descending { get; init; } = true;
		public int Limit { get; init; } = 10;

		/// Compare the most recent period against the one before it.
		///
		/// A capability of the SURFACE LAYER rather than of any one surface: declared here, answered
		/// in the runner, and therefore true of sales, purchases and stock movement at once. Ignored
		/// by a surface with no `month` dimension, which is the honest behaviour - a surface with no
		/// time in it cannot be asked how something changed.
		public bool ComparePeriods { get; init; }
	}

	/// Why a query was refused. A CODE, so the reason survives translation and can be acted on.
	public enum AskQueryFault
	{
		None = 0,
		UnknownSurface,
		UnknownColumn,
		NotAMeasure,
		NotADimension,
		NoMeasure,
		LimitOutOfRange,
		TooManyGroups,
		BadFilterValue,
	}

	public sealed class AskQueryValidation
	{
		public bool Ok => Fault == AskQueryFault.None;
		public AskQueryFault Fault { get; init; }
		/// The offending name, so a caller can be told WHICH column it invented.
		public string? Offender { get; init; }
		public AskQuery? Clean { get; init; }

		public static AskQueryValidation Fail(AskQueryFault fault, string? offender = null)
			=> new() { Fault = fault, Offender = offender };

		public static AskQueryValidation Pass(AskQuery clean) => new() { Clean = clean };
	}

	/// One declared surface: what it is, who may read it, what it exposes, and what it has already
	/// decided on the reader's behalf.
	public abstract class AskSurface
	{
		// =============================================================================================
		// A ROW CAN CARRY A WAY TO OPEN IT.
		//
		// The owner's requirement: "لازم يبقى في امكانية اني افتح النتيجة" - if a row is an item, he
		// must be able to open that item; if it names an invoice, go and look at it. A figure he
		// cannot get behind is a figure he has to take on trust.
		//
		// These are carried as ORDINARY COLUMNS on the row rather than computed afterwards, because
		// only the surface knows which single record a line belongs to. Once rows are grouped that
		// knowledge is gone, so it has to travel with them. They are deliberately NOT declared in
		// `Columns`: nobody may group by a link, filter on one, or ask for it as a measure.
		//
		// The runner decides whether a GROUP is allowed to keep one - see AskSurfaceRunner. A group
		// covering several records must not inherit the link of whichever row happened to be first.
		// =============================================================================================
		// ONE LINK PER DIMENSION, not one per row. Grouping by vendor must open the VENDOR, and a
		// single row-level link could only ever point at one kind of thing - which is why the first
		// version left every vendor and customer row unopenable while items worked.
		public const string LinkPrefix = "__link_";
		public static string LinkFor(string dimension) => LinkPrefix + dimension;

		/// THE ROW KEY HOLDING A RECORD'S OWN NAME — set it and the surface can be asked about one.
		///
		/// A surface groups and totals, so a question naming a particular thing had nowhere to go:
		/// the owner asked «في موظف اسمه احمد زكريا؟» and was told the words meant nothing here. The
		/// product knows every employee's name; the assistant simply had no path to it. A surface
		/// that names this key gets that path, and the ones that leave it null are unchanged.
		///
		/// Name it only where a row IS one record of a thing a person would name out loud —
		/// an employee, an item, a customer. A sales LINE is not such a thing.
		public virtual string? NameKey => null;

		/// Every distinct extra action this surface's columns name, for AskService to evaluate.
		public IReadOnlyCollection<string> ColumnActions => Columns
			.Where(c => !string.IsNullOrWhiteSpace(c.Action))
			.Select(c => c.Action!)
			.Distinct(System.StringComparer.OrdinalIgnoreCase)
			.ToList();

		/// Optional siblings of NameKey on the same row: a picture of the record and a way into it.
		/// Both are read only when NameKey is set, and a row may carry either, both or neither.
		public const string PhotoKey = "__photo";
		public const string RecordHrefKey = "__href";

		/// EVERY SPELLING OF THE NAME, FOR MATCHING ONLY — never shown.
		///
		/// NameKey holds the name in the READER'S language, which is right for display and wrong
		/// for search: employee 5 is «احمد زكريا» in Arabic and "ahmed zakareya" in English, so an
		/// English session searching an Arabic question found neither — it fell through to a
		/// partial match and confidently named a different colleague. People type a name in the
		/// script they think in, whatever language the screen is in.
		public const string SearchNameKey = "__namesearch";

		/// The same idea for a DIMENSION's values: every spelling of one customer, item or vendor,
		/// used for matching only. A row carries it or it does not; without one the displayed value
		/// is searched, which is correct for a dimension that has only one spelling.
		public const string SearchPrefix = "__search_";
		public static string SearchKeyFor(string dimension) => SearchPrefix + dimension;

		public abstract string Code { get; }
		public abstract string Module { get; }
		public abstract string Action { get; }
		public abstract AskText Description { get; }
		public abstract IReadOnlyList<AskColumn> Columns { get; }

		/// THE JUDGEMENT BAKED IN, in words. Shown with every answer: a reader who does not know
		/// that reversals are excluded cannot tell whether the figure is the one they wanted.
		public abstract AskText Rules { get; }

		/// The rows, ALREADY scoped to the company and already filtered by this surface's rules.
		/// A surface is responsible for its own row-level authority, exactly as a handler was.
		public abstract Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default);

		public AskColumn? Column(string name) =>
			Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
	}

	// =================================================================================================
	// VALIDATION — the only place a composed query is allowed to become trusted.
	//
	// It is deliberately paranoid and deliberately dumb: it knows nothing about intent, only about
	// what the surface declared. That is what makes it reviewable. A reviewer does not need to
	// reason about what a model might emit; they need only check that nothing undeclared survives.
	// =================================================================================================
	public static class AskQueryValidator
	{
		public const int MaxLimit = 50;

		/// The ceiling on a RECORD query — one that returns rows rather than totals. Lower than
		/// MaxLimit because a list of records is read, not scanned, and because the smaller the
		/// ceiling the less this resembles a way to walk a table.
		public const int MaxRecords = 20;
		public const int MaxGroups = 3;
		public const int MaxFilters = 6;
		public const int MaxFilterValueLength = 120;

		public static AskQueryValidation Validate(AskQuery query, AskSurface? surface)
		{
			if (query == null || surface == null
				|| !string.Equals(query.Surface, surface.Code, StringComparison.OrdinalIgnoreCase))
				return AskQueryValidation.Fail(AskQueryFault.UnknownSurface, query?.Surface);

			if (query.Limit < 1 || query.Limit > MaxLimit)
				return AskQueryValidation.Fail(AskQueryFault.LimitOutOfRange, query.Limit.ToString());

			if (query.GroupBy.Count > MaxGroups)
				return AskQueryValidation.Fail(AskQueryFault.TooManyGroups, query.GroupBy.Count.ToString());

			foreach (var g in query.GroupBy)
			{
				var col = surface.Column(g);
				if (col == null) return AskQueryValidation.Fail(AskQueryFault.UnknownColumn, g);
				if (col.Kind != AskColumnKind.Dimension)
					return AskQueryValidation.Fail(AskQueryFault.NotADimension, g);
			}

			// A QUERY WITH NO MEASURE IS A RECORD LOOKUP, and those are allowed — bounded.
			//
			// This used to be refused outright, on the reasoning that a surface exists to aggregate
			// rather than to page through data. That reasoning was wrong: the list screens show these
			// same rows to the same person under the same authority, so refusing to name one here
			// protected nothing and made the assistant less useful than the menu. "اخر فاتورة بيع" is
			// not an exotic request.
			//
			// What needed bounding was the SIZE and the ORDER. A record query must say how to order
			// itself and may return at most MaxRecords rows, and there is no offset — so it can
			// answer "the last one" and cannot walk a table.
			if (query.Measures.Count == 0)
			{
				if (string.IsNullOrWhiteSpace(query.OrderBy))
					return AskQueryValidation.Fail(AskQueryFault.NoMeasure);
				if (surface.Column(query.OrderBy) == null)
					return AskQueryValidation.Fail(AskQueryFault.UnknownColumn, query.OrderBy);
				if (query.Limit > MaxRecords)
					return AskQueryValidation.Fail(AskQueryFault.LimitOutOfRange, query.Limit.ToString());
			}

			foreach (var m in query.Measures)
			{
				var col = surface.Column(m.Column);
				if (col == null) return AskQueryValidation.Fail(AskQueryFault.UnknownColumn, m.Column);
				// Count is the one aggregate a dimension may carry: "how many customers" is a fair
				// question and needs no measure column.
				if (col.Kind != AskColumnKind.Measure && m.Agg != AskAgg.Count)
					return AskQueryValidation.Fail(AskQueryFault.NotAMeasure, m.Column);
			}

			foreach (var f in query.Filters.Take(MaxFilters + 1))
			{
				var col = surface.Column(f.Column);
				if (col == null) return AskQueryValidation.Fail(AskQueryFault.UnknownColumn, f.Column);
				if (f.Value.Length > MaxFilterValueLength)
					return AskQueryValidation.Fail(AskQueryFault.BadFilterValue, f.Column);
				if (f.Op == AskOp.Between && string.IsNullOrWhiteSpace(f.Value2))
					return AskQueryValidation.Fail(AskQueryFault.BadFilterValue, f.Column);
			}
			if (query.Filters.Count > MaxFilters)
				return AskQueryValidation.Fail(AskQueryFault.BadFilterValue, query.Filters.Count.ToString());

			if (!string.IsNullOrWhiteSpace(query.OrderBy) && query.Measures.Count > 0)
			{
				// Ordering is allowed only by something the answer actually contains. Sorting by a
				// column that was not selected would quietly read a column nobody asked for.
				var named = query.Measures.Any(m => string.Equals(m.Column, query.OrderBy, StringComparison.OrdinalIgnoreCase))
					|| query.GroupBy.Any(g => string.Equals(g, query.OrderBy, StringComparison.OrdinalIgnoreCase));
				if (!named) return AskQueryValidation.Fail(AskQueryFault.UnknownColumn, query.OrderBy);
			}

			// Rebuilt from the validated parts rather than passed through, so nothing a caller put
			// on the object can survive into execution unexamined.
			return AskQueryValidation.Pass(new AskQuery
			{
				Surface = surface.Code,
				GroupBy = query.GroupBy.Select(g => surface.Column(g)!.Name).ToList(),
				Measures = query.Measures
					.Select(m => new AskMeasure { Column = surface.Column(m.Column)!.Name, Agg = m.Agg })
					.ToList(),
				Filters = query.Filters
					.Select(f => new AskFilter
					{
						Column = surface.Column(f.Column)!.Name,
						Op = f.Op,
						Value = f.Value,
						Value2 = f.Value2,
					})
					.ToList(),
				OrderBy = string.IsNullOrWhiteSpace(query.OrderBy) ? null : query.OrderBy,
				Descending = query.Descending,
				Limit = query.Limit,
				// CARRIED EXPLICITLY, because this method REBUILDS the query from its validated
				// parts rather than copying it. Anything not named here is silently dropped, and a
				// dropped flag produces a correct-looking answer to a question nobody asked.
				ComparePeriods = query.ComparePeriods,
			});
		}

		public static AskText Explain(AskQueryValidation v, bool arabic) => v.Fault switch
		{
			AskQueryFault.UnknownSurface => AskText.Of("لا أعرف هذا المصدر.", "I do not know that data surface.",
				"Je ne connais pas cette source de données."),
			AskQueryFault.UnknownColumn => AskText.Of(
				$"لا يوجد حقل باسم «{v.Offender}» في هذا المصدر.",
				$"There is no field called \"{v.Offender}\" on this surface.",
				$"Aucun champ nommé « {v.Offender} » n'existe dans cette vue."),
			AskQueryFault.NotAMeasure => AskText.Of(
				$"«{v.Offender}» ليس حقلًا رقميًا يُجمَع.",
				$"\"{v.Offender}\" is not a numeric field that can be totalled.",
				$"« {v.Offender} » n'est pas un champ numérique pouvant être totalisé."),
			AskQueryFault.NotADimension => AskText.Of(
				$"«{v.Offender}» لا يصلح للتجميع حسبه.",
				$"\"{v.Offender}\" cannot be grouped by.",
				$"« {v.Offender} » ne peut pas servir de critère de regroupement."),
			AskQueryFault.NoMeasure => AskText.Of(
				"لعرض سجلات بدل إجماليات، لا بد من تحديد الترتيب — بأي حقل تريد أحدثها؟",
				"To list records rather than totals, an ordering is required — by which field should the newest be?",
				"Pour afficher des fiches plutôt que des totaux, précisez le tri : quel champ doit déterminer les plus récentes ?"),
			AskQueryFault.LimitOutOfRange => AskText.Of(
				$"عدد الصفوف المطلوب خارج الحدود (1..{MaxLimit}).",
				$"The requested row count is outside the allowed range (1..{MaxLimit}).",
				$"Le nombre de lignes demandé est hors limites (1..{MaxLimit})."),
			AskQueryFault.TooManyGroups => AskText.Of(
				$"أكثر من {MaxGroups} مستويات تجميع.",
				$"More than {MaxGroups} grouping levels.",
				$"Plus de {MaxGroups} niveaux de regroupement."),
			AskQueryFault.BadFilterValue => AskText.Of(
				"شرط تصفية غير صالح.", "An invalid filter condition.", "Un critère de filtrage n'est pas valide."),
			_ => AskText.Of("", ""),
		};
	}
}
