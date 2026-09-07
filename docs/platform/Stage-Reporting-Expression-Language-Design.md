# Stage — Reporting: Calculated Fields and the Safe Expression Language (REX)

**Platform:** CrossBusiness Reporting Platform
**Companion to:** `ADR-037-Reporting-Platform-Architecture.md`, `Stage-Reporting-Dataset-Layer-Design.md`
**Status:** DESIGN ONLY — no evaluator is implemented in this increment.
**Identity:** CrossBusiness Blue.

---

## 1. What this is for

A Report Studio user needs `Margin = (Revenue - Cost) / Revenue`, `IF(DaysOverdue > 90, "Legal", "Chase")`, and a
running total. They must get that **without** anyone giving them a scripting engine.

The name **REX** (Reporting EXpressions) exists so the language can be versioned and referred to precisely.

### 1.1 The threat this design exists to close

An expression language inside a reporting product is a remote code execution surface with a friendly name. Every
one of these has shipped in a real reporting tool and been exploited:

| Attack | What it looks like in a report designer |
|---|---|
| Arbitrary code | An expression that compiles to C# and calls `System.Diagnostics.Process.Start` |
| Reflection escape | `"".GetType().Assembly.GetType("System.IO.File").GetMethod("Delete")` |
| SQL injection | An expression concatenated into the data source's WHERE clause |
| SSRF / exfiltration | An expression that fetches a URL, carrying the row's data in the query string |
| Denial of service | `REPT("x", 1000000000)` or a recursive calculated field |
| Tenant escape | An expression that reads a value the row was filtered to exclude |

**The design answer is not sandboxing. It is a language that cannot express any of them.** REX has no way to name
a type, call a method, reach a namespace, open anything, or loop. There is nothing to escape from.

---

## 2. Non-negotiable exclusions

REX **does not have, and must never gain**:

- arbitrary C#, JavaScript, VB, Python or any general-purpose language
- compilation to IL, `CSharpScript`, `DataTable.Compute`, `DynamicExpresso`, or any eval-shaped host API
- reflection, type names, `GetType`, assembly or namespace access
- file, network, environment, registry, process or clock access other than the injected `NOW()` (§6.4)
- raw SQL, or any construct that reaches a data source as an identifier
- user-defined functions, lambdas, recursion or loops of any kind
- assignment, statements, or side effects of any sort — **an expression is a pure function of one row**

> **The identifier rule, restated from ADR-037 §Query:** nothing a user types ever reaches a query builder as an
> identifier. A REX field reference resolves against the dataset's **declared field list** and becomes an index,
> not a name. An unknown identifier is a compile error, not a lookup.

---

## 3. Grammar

```
expression  := ternary
ternary     := orExpr [ "?" expression ":" expression ]
orExpr      := andExpr { "||" andExpr }
andExpr     := notExpr { "&&" notExpr }
notExpr     := [ "!" ] comparison
comparison  := additive [ ( "=" | "<>" | "<" | "<=" | ">" | ">=" ) additive ]
additive    := multiplicative { ( "+" | "-" ) multiplicative }
multiplicative := unary { ( "*" | "/" | "%" ) unary }
unary       := [ "-" ] primary
primary     := NUMBER | STRING | BOOLEAN | NULL
             | FIELD                       -- [FieldKey], resolved against the dataset
             | PARAM                       -- @ParameterKey, resolved against the report's parameters
             | FUNCTION "(" [ args ] ")"   -- from the frozen catalogue only
             | "(" expression ")"
```

That is the **whole** grammar. There is no member access (`.`), no indexing (`[]` other than a field reference),
no call on a value, and no way to introduce a name. Those absences are the security model.

**Field references are bracketed** — `[Revenue]` — so a field key can never be confused with a function name, and
so adding a function to the catalogue can never shadow somebody's field.

---

## 4. Type system

Static, checked at **save** time, over the same closed set the platform already uses (`ReportFieldType`):

`String · Integer · Decimal · Money · Date · DateTime · Boolean · Percent · EntityRef`

### 4.1 Rules

1. **No implicit string↔number coercion.** `"5" + 3` is a compile error, not `8` and not `"53"`. Silent coercion
   is how a report shows a plausible wrong number.
2. **`Integer` widens to `Decimal` automatically; nothing narrows.** Narrowing needs explicit `ROUND`/`TRUNC`.
3. **`Money` is `Decimal` with a currency tag.** `Money + Money` is `Money`. `Money * Decimal` is `Money`.
   **`Money * Money` is a compile error** — there is no such quantity, and permitting it is how a "total" ends up
   being a square.
4. **`Money` of two different currencies cannot be combined.** No implicit FX. Conversion is a business decision
   owned by the module that computed the number, exactly as ADR-037 §Numbers says of rounding.
5. **`Percent` is a percentage (12.5 means 12.5%), not a ratio.** `Decimal → Percent` requires explicit
   `TOPERCENT`, because the two conventions differ silently by 100×.
6. **`EntityRef` supports only `=`, `<>` and `ISNULL`.** It is an identity, not a value.
7. **`Date` arithmetic goes through `DATEADD`/`DATEDIFF`.** `date + 1` is a compile error: "plus one" is
   ambiguous between a day, a month and a tick.
8. **The declared result type must match the field's declared type.** A calculated field declaring `Money` whose
   expression yields `String` fails validation.

### 4.2 Money and rounding — the rule inherited from ADR-037

> **REX never rounds implicitly.** A division yielding 33.333… stays at full decimal precision until it is
> FORMATTED. Rounding is `ROUND(x, n)`, written by the author, or it does not happen.

This is the platform's existing rule (`ReportFieldType.Money`: *"the reporting platform never ROUNDS a money
value — it formats what the data source gave it"*), extended to calculated values. A report that re-rounded
would produce totals that disagree with the ledger.

---

## 5. Function catalogue (frozen)

A function not in this table does not exist. Adding one is a code change plus a version bump (§8), never
configuration.

### Logic
| Function | Signature | Notes |
|---|---|---|
| `IF` | `IF(bool, T, T) → T` | Both branches must be the same type. |
| `CASE` | `CASE(bool, T, [bool, T]…, T) → T` | Pairs plus a mandatory ELSE — no implicit null default. |
| `ISNULL` | `ISNULL(T?) → Boolean` | |
| `COALESCE` | `COALESCE(T?, T) → T` | |
| `NULLIF` | `NULLIF(T, T) → T?` | |

### Aggregates — group scope only
| Function | Signature |
|---|---|
| `SUM` / `AVG` / `MIN` / `MAX` | `(numeric field) → numeric` |
| `COUNT` / `COUNTDISTINCT` | `(field) → Integer` |

**Aggregates are legal ONLY in a group-footer or grand-total context.** An aggregate in a detail-row expression
is a compile error naming the scope rule — SQL's silent answer to that mistake is a whole class of wrong report.

### Numbers
`ROUND(x, digits)` · `TRUNC(x, digits)` · `ABS(x)` · `SIGN(x)` · `FLOOR(x)` · `CEILING(x)` ·
`TOPERCENT(x)` · `TODECIMAL(x)` · `SAFEDIVIDE(a, b, fallback)`

### Dates
`DATEADD(part, n, date)` · `DATEDIFF(part, from, to)` · `DATEPART(part, date)` · `TODAY()` · `NOW()` ·
`STARTOFMONTH(date)` · `ENDOFMONTH(date)` · `STARTOFYEAR(date)` · `ENDOFYEAR(date)`

`part ∈ { Day, Week, Month, Quarter, Year }` — a **keyword**, not a string, so it cannot be built at runtime.

### Strings
`CONCAT(a, b, …)` · `UPPER(s)` · `LOWER(s)` · `TRIM(s)` · `LEFT(s, n)` · `RIGHT(s, n)` · `SUBSTRING(s, start, len)` ·
`LENGTH(s)` · `CONTAINS(s, part)` · `STARTSWITH(s, part)` · `FORMATNUMBER(x, pattern)` · `FORMATDATE(d, pattern)`

**`REPT`, `REPLICATE` and `PADLEFT` are deliberately absent** — they are the standard memory-amplification
primitives in a spreadsheet language.

**`FORMATNUMBER`/`FORMATDATE` patterns are validated against an allow-list** of .NET format strings. An arbitrary
pattern is a compile error: custom format strings are a small language of their own.

---

## 6. Evaluation semantics — decided, not discovered

### 6.1 Null
**Null propagates.** Any arithmetic or comparison with a null operand yields null; a null `Boolean` in a
condition is **false**. `ISNULL`/`COALESCE`/`NULLIF` are the only functions that observe null.

*Why:* the alternative (null as zero) makes "no invoices" and "invoices totalling zero" indistinguishable — and
a blank cell is honest where a `0` is a claim.

### 6.2 Divide by zero
**`x / 0` yields NULL**, never an exception and never infinity. The row renders blank and the run carries an
Information diagnostic naming the field.

A whole report must not fail because one row had a zero denominator; an exception here would make a 50 000-row
report fail on row 49 999. Authors who want a different answer write
`SAFEDIVIDE([a], [b], 0)` — explicitly.

### 6.3 Determinism
The same row plus the same parameters **must** yield the same value. This is what makes an archived artifact
reproducible and a scheduled run comparable to yesterday's. Consequences:

- No random, no sequence, no row-position function in detail scope.
- `NOW()`/`TODAY()` are **injected once per run** from `IReportClock` — the platform's existing clock — and are
  identical for every row. A per-row clock would make a long report's first and last pages disagree.
- Aggregates evaluate over the shaped, filtered, authorized row set, in the order the shaper produced.

### 6.4 Culture
- **Parsing is INVARIANT.** `.` is always the decimal separator and `,` is always the argument separator, in
  every UI language. A stored expression must mean the same thing when an Arabic and an English user open it —
  a culture-sensitive parser makes `ROUND(1,5)` a different expression per user.
- **Formatting is CULTURAL**, applied after evaluation by the renderer, using the report's culture.
- Arabic-Indic digits are accepted on input and normalised at parse time.
- String comparison is **ordinal**; `UPPER`/`LOWER` are invariant-culture. Turkish-I would otherwise make the
  same expression classify differently per user.

### 6.5 Execution limits
Enforced per expression, per run:

| Limit | Default | Why |
|---|---|---|
| Source length | 4 000 chars | |
| AST node count | 500 | Caps parse and eval cost independent of length |
| Nesting depth | 32 | A deep tree is the stack-overflow vector |
| Function calls per expression | 100 | |
| String result length | 8 000 chars | Caps `CONCAT` amplification |
| Total eval budget per run | 2 s CPU | A cap the whole run shares, so 50 000 rows × a cheap expression is fine and a pathological one is not |

Exceeding a limit at **save** time is a validation error. Exceeding the run budget **fails the run** with a
diagnostic — it does not truncate, because a partially-calculated column is a wrong report.

### 6.6 Security within a row
- An expression may reference **only** fields of its own dataset, and **only** fields the caller may see. A
  reference to a field the caller lacks permission for (`Confidential`/`Restricted` per the Dataset Layer) makes
  the calculated field **unavailable**, not null — silently yielding null would let a user infer the hidden
  value's presence and, with arithmetic, sometimes its magnitude.
- A reference to a `Never`-sensitivity field is a **compile** error.
- An expression cannot reference another row, another group, or a parameter the caller did not supply.
- `@Parameter` references resolve to already-bound, already-validated values (`ReportParameterBinder`), so the
  engine-supplied `CompanyId` cannot be overridden through an expression any more than through a request.

---

## 7. Where evaluation happens

**In the shaper, after fetch, filter and authorization — never in the data source, and never in SQL.**

```
fetch (authorized rows) → filter → sort → group → EVALUATE CALCULATED FIELDS → aggregate → render
```

Consequences, already enforced by `ReportDatasetValidator`:

- A calculated field **cannot be filterable or sortable server-side** — the source never produced it. The
  validator refuses the combination rather than leaving it to be discovered.
- A calculated field **must declare `DependsOn`**, so the source knows which underlying fields to fetch even when
  only the calculated one is displayed.
- **Dependency cycles are rejected at startup** by an iterative detector (a recursive walk would stack-overflow
  on exactly the input the check exists to catch).

Evaluation order within a row is a topological sort of `DependsOn`, so a calculated field may depend on another.

---

## 8. Versioning

`ExpressionLanguageVersion` is stamped on every stored expression.

- **Adding** a function or widening a type rule → **minor** bump. Stored expressions keep working.
- **Removing** a function, narrowing a type rule, or changing an evaluation semantic → **major** bump. Stored
  expressions are validated against their stamped version and flagged for migration; they are **never silently
  reinterpreted**.

An expression stored under v1 evaluates under v1 semantics. That is what stops a null-handling change from
quietly altering last year's archived numbers.

---

## 9. Validation pipeline (save time)

```
1. LEX      reject unknown characters                        → REX001
2. PARSE    grammar only                                     → REX002
3. RESOLVE  every [field] and @parameter exists and is visible → REX010/REX011
4. TYPE     check every operator and call                    → REX020
5. SCOPE    aggregates only in aggregate contexts            → REX030
6. LIMITS   length, nodes, depth, calls                      → REX040
7. CYCLE    dependency graph is acyclic                      → REX050
```

Each stage yields a machine code plus a **character offset**, so the Studio can underline the exact token. A UI
localizes from the code; the message is never a server-composed English string on its own.

---

## 10. Implementation guidance for the next increment

A hand-written recursive-descent parser producing an immutable AST, plus a tree-walking evaluator. Roughly
900–1 200 lines.

**Do not** reach for `DataTable.Compute`, `System.Linq.Dynamic`, `NCalc`, `Roslyn` scripting, or any expression
library, without a security review that specifically covers §2 — several of those can reach a type or a method
by design, which is the one thing REX must not be able to do.

**Test shape** — the parser is a pure function, so it is exhaustively testable:

- a golden corpus of valid expressions with expected types and values;
- a corpus of **rejections**, one per rule in §2 and §4, each asserting the specific error code;
- adversarial inputs: 10 000-deep nesting, a 1 MB literal, a cycle, an aggregate in detail scope, a reference to a
  `Never` field, a Turkish-I casing pair, `1,5` vs `1.5` under an Arabic culture;
- determinism: the same row twice yields the same value, and `NOW()` is stable across rows within one run.

---

## 11. Worked examples

```rex
-- margin %, safe against a zero denominator
SAFEDIVIDE([Revenue] - [Cost], [Revenue], 0)

-- ageing bucket
CASE([DaysOverdue] > 90,  "90+",
     [DaysOverdue] > 60,  "61-90",
     [DaysOverdue] > 30,  "31-60",
                          "Current")

-- days to due date, from the run's single clock reading
DATEDIFF(Day, TODAY(), [DueDate])

-- group footer only
SUM([LineTotal])

-- REJECTED, and the rule each one breaks
[Revenue] * [Cost]                    -- REX020: Money * Money is not a quantity
[SaleDate] + 1                        -- REX020: ambiguous date arithmetic; use DATEADD
"5" + 3                               -- REX020: no implicit string↔number coercion
SUM([Amount]) / [Qty]                 -- REX030: aggregate in detail scope
[CostPrice]                           -- REX011: caller lacks the field's permission
[InternalRowKey]                      -- REX011: field sensitivity is Never
```

---

## 12. Open decisions for the owner

| # | Question | Recommendation |
|---|---|---|
| E1 | Should authors be able to define a **reusable named expression** shared across datasets? | Defer. It is a second namespace, and therefore a second thing to version and permission. |
| E2 | Cross-row window functions (`RUNNINGTOTAL`, `RANK`, `PREVIOUS`)? | Defer to a v2. Running totals need an ordering contract the shaper does not yet publish, and `PREVIOUS` breaks per-row determinism. |
| E3 | Should a calculated field be filterable via a **post-evaluation** filter pass? | Yes eventually — but as a separate, clearly-named "post-filter" stage, so the row cap and the truncation declaration stay honest. |
| E4 | Localised function names (`إذا` for `IF`)? | No. A stored expression must parse identically for every user; localise the *picker*, not the language. |
