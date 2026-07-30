using System.Linq.Expressions;

namespace CrossBuy.BL
{
	// Parses the Tagify search payload: tags are sent joined by '|' (so multi-word tags stay intact).
	// Falls back to whitespace-splitting when no '|' is present (plain typed text). Caps at 8 terms.
	public static class SearchTerms
	{
		public static List<string> Parse(string? q)
		{
			if (string.IsNullOrWhiteSpace(q)) return new();
			var raw = q.Contains('|')
				? q.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				: q.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
			return raw.Select(t => t.Trim()).Where(t => t.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
		}
	}

	// Combines per-term predicates with OR so a Tagify multi-tag search matches ANY tag.
	// EF Core translates the resulting OrElse tree to a single SQL OR chain.
	public static class PredicateBuilder
	{
		public static Expression<Func<T, bool>> False<T>() => _ => false;

		public static Expression<Func<T, bool>> Or<T>(this Expression<Func<T, bool>> a, Expression<Func<T, bool>> b)
		{
			var p = Expression.Parameter(typeof(T), "x");
			var left = new Rebind(a.Parameters[0], p).Visit(a.Body)!;
			var right = new Rebind(b.Parameters[0], p).Visit(b.Body)!;
			return Expression.Lambda<Func<T, bool>>(Expression.OrElse(left, right), p);
		}

		// Build an OR over a set of search terms, each turned into a per-term predicate.
		public static Expression<Func<T, bool>>? AnyTerm<T>(IEnumerable<string> terms, Func<string, Expression<Func<T, bool>>> perTerm)
		{
			Expression<Func<T, bool>>? acc = null;
			foreach (var t in terms)
			{
				var e = perTerm(t);
				acc = acc == null ? e : acc.Or(e);
			}
			return acc;
		}

		private sealed class Rebind : ExpressionVisitor
		{
			private readonly Expression _from, _to;
			public Rebind(Expression from, Expression to) { _from = from; _to = to; }
			public override Expression? Visit(Expression? node) => node == _from ? _to : base.Visit(node);
		}
	}
}
