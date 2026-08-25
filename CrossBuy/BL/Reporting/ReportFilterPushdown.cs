using System.Linq.Expressions;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // FILTER PUSHDOWN — the V1 defect §10 names, fixed at its cause.
    //
    // THE DEFECT, proved at runtime: a Studio filter is applied by the SHAPER, which runs after the data
    // source has already capped its fetch. Asking for "Status = Draft" over a busy month returned zero rows
    // while six Drafts existed — they were simply outside the newest-N window the source had fetched. The
    // report was not wrong about the rows it had; it had the wrong rows.
    //
    // RAISING MaxRows IS NOT THE FIX and §10 forbids it: it moves the boundary without removing it, and pays
    // for the move by materialising more of the table on every preview. Loading the whole table and filtering
    // in memory is the same trade at a worse price.
    //
    // THE FIX is to let a filter reach SQL before the TOP(n). This is the narrowest mechanism that does that:
    //
    //   · a source OPTS IN per field, by naming the column it maps to. There is no reflection, no expression
    //     parsing and no string interpolation — the caller hands a typed selector and EF translates it, so a
    //     filter value can never become syntax.
    //   · a field a source does not map falls through to the shaper exactly as before, so this is additive:
    //     no existing dataset changes behaviour until its source opts a field in.
    //   · every pushed filter is DECLARED in appliedFilters, which is how the shaper knows not to apply it a
    //     second time.
    //
    // SECURITY ORDER IS PRESERVED, and it is the reason Apply takes an already-filtered queryable rather than
    // building the whole query: the company predicate is applied by the source BEFORE this is called, so a
    // user filter can only ever narrow what isolation already permitted. Nothing here can widen a scope.
    // ============================================================================================
    public static class ReportFilterPushdown
    {
        // A source's opt-in table: field key → how to narrow the query for that field.
        public delegate IQueryable<T>? Push<T>(IQueryable<T> query, ReportFilterOperator op, string value);

        public static IQueryable<T> Apply<T>(IQueryable<T> query, IReadOnlyList<ReportFilter> filters,
            List<ReportFilter> applied, IReadOnlyDictionary<string, Push<T>> map)
        {
            if (filters == null || filters.Count == 0 || map.Count == 0) return query;

            foreach (var filter in filters)
            {
                if (!map.TryGetValue(filter.Field, out var push)) continue;

                var value = filter.Values.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(value)) continue;

                var narrowed = push(query, filter.Operator, value!);
                if (narrowed == null) continue;   // the operator is not pushable for that column

                query = narrowed;
                applied.Add(filter);              // tells the shaper this one is already done
            }
            return query;
        }

        // ---- typed helpers ----------------------------------------------------------------------------
        //
        // Each builds an expression tree, so EF renders a parameterised predicate. The VALUE is always a
        // parameter and never text spliced into SQL — which is what makes "no raw SQL path" a structural fact
        // rather than a review item.

        public static IQueryable<T>? Text<T>(IQueryable<T> q, Expression<Func<T, string?>> selector,
            ReportFilterOperator op, string value)
        {
            var p = selector.Parameters[0];
            var member = selector.Body;
            var constant = Expression.Constant(value, typeof(string));

            Expression? body = op switch
            {
                ReportFilterOperator.Equals => Expression.Equal(member, constant),
                ReportFilterOperator.NotEquals => Expression.NotEqual(member, constant),
                ReportFilterOperator.Contains => Expression.AndAlso(
                    Expression.NotEqual(member, Expression.Constant(null, typeof(string))),
                    Expression.Call(member, ContainsMethod, constant)),
                ReportFilterOperator.StartsWith => Expression.AndAlso(
                    Expression.NotEqual(member, Expression.Constant(null, typeof(string))),
                    Expression.Call(member, StartsWithMethod, constant)),
                _ => null,
            };

            return body == null ? null : q.Where(Expression.Lambda<Func<T, bool>>(body, p));
        }

        public static IQueryable<T>? Number<T>(IQueryable<T> q, Expression<Func<T, decimal>> selector,
            ReportFilterOperator op, string value)
        {
            if (!decimal.TryParse(value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return null;

            return Compare(q, selector, Expression.Constant(parsed, typeof(decimal)), op);
        }

        public static IQueryable<T>? Integer<T>(IQueryable<T> q, Expression<Func<T, int>> selector,
            ReportFilterOperator op, string value)
        {
            if (!int.TryParse(value, out var parsed)) return null;
            return Compare(q, selector, Expression.Constant(parsed, typeof(int)), op);
        }

        // Nullable columns get their own entry points rather than a nullable-aware Compare: a NULL must never
        // satisfy a comparison, and EF's three-valued logic would otherwise let "Amount < 100" quietly include the
        // rows that have no amount at all.
        public static IQueryable<T>? Integer<T>(IQueryable<T> q, Expression<Func<T, int?>> selector,
            ReportFilterOperator op, string value)
        {
            if (!int.TryParse(value, out var parsed)) return null;
            return Compare(q, selector, Expression.Constant((int?)parsed, typeof(int?)), op);
        }

        public static IQueryable<T>? Number<T>(IQueryable<T> q, Expression<Func<T, decimal?>> selector,
            ReportFilterOperator op, string value)
        {
            if (!decimal.TryParse(value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return null;

            return Compare(q, selector, Expression.Constant((decimal?)parsed, typeof(decimal?)), op);
        }

        public static IQueryable<T>? Date<T>(IQueryable<T> q, Expression<Func<T, DateTime>> selector,
            ReportFilterOperator op, string value)
        {
            if (!DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed))
                return null;

            // A DATE comparison is inclusive of the whole day on the upper bound — the same rule every date
            // range in this platform follows, and the one that silently drops the last day when forgotten.
            if (op == ReportFilterOperator.LessOrEqual)
                return Compare(q, selector, Expression.Constant(parsed.Date.AddDays(1), typeof(DateTime)),
                               ReportFilterOperator.LessThan);

            return Compare(q, selector, Expression.Constant(parsed.Date, typeof(DateTime)), op);
        }

        private static IQueryable<T>? Compare<T, TValue>(IQueryable<T> q, Expression<Func<T, TValue>> selector,
            ConstantExpression constant, ReportFilterOperator op)
        {
            var p = selector.Parameters[0];
            var member = selector.Body;

            Expression? body = op switch
            {
                ReportFilterOperator.Equals => Expression.Equal(member, constant),
                ReportFilterOperator.NotEquals => Expression.NotEqual(member, constant),
                ReportFilterOperator.GreaterThan => Expression.GreaterThan(member, constant),
                ReportFilterOperator.GreaterOrEqual => Expression.GreaterThanOrEqual(member, constant),
                ReportFilterOperator.LessThan => Expression.LessThan(member, constant),
                ReportFilterOperator.LessOrEqual => Expression.LessThanOrEqual(member, constant),
                _ => null,
            };

            return body == null ? null : q.Where(Expression.Lambda<Func<T, bool>>(body, p));
        }

        private static readonly System.Reflection.MethodInfo ContainsMethod =
            typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!;

        private static readonly System.Reflection.MethodInfo StartsWithMethod =
            typeof(string).GetMethod(nameof(string.StartsWith), new[] { typeof(string) })!;
    }
}
