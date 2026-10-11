# TAB-5 — KPI registry over existing Accounting and Projects datasets

Approved base: `c0f97af481d38457fa25c5e9ded529a80aab41a3`. Separate detached worktree; no guessed branch and no edits to any existing checkout. No SQL execution, push, deployment, new dataset, business formula, or screen.

The user approved this chat as shared-file coordinator. This wave adds one Program.cs registration immediately after AddProjectsReporting. TAB-3 adds its owner resolver near the document registrations; the coordinator must retain both additions during future integration. No merge is performed here.

## Delivered behavior

KpiRegistry validates dataset allowlist and existence, unique keys, business owner, value field, permitted aggregate, filter field/operator/value, date field, drill target and fixed parameter names/values when constructed. Definitions freeze their filter values and fixed parameters. Required dataset authority is never replaced: evaluation checks dataset permission AND KPI permission, then sensitivity of all referenced value/filter fields, before fetching anything.

KpiEvaluator resolves employee/company from the server context, refuses unresolved and portal identities, and reuses the existing parameter binder, registered dataset source and report shaper. Caller CompanyId is ignored by the existing system parameter binder. Fixed KPI parameters override caller input, so posted sales cannot be widened to drafts. Partial fetches, missing sum values and malformed source shapes return no numeric value. Freshness is stamped from IReportClock. No cached result crosses requests or companies.

The 10 definitions are:

- Posted sales invoice totals in company functional currency (`GrandTotalBase`), including tax. This is labelled posted sales, not net revenue.
- Five authoritative AR aging sums: Current, Days30, Days60, Days90Plus and Total. Invoice age is not payment lateness.
- Authoritative customer Margin, gated by accounting.reports.profitability in addition to the dataset's accounting.reports.view.
- Active project count, milestone count and overdue milestone count using the existing dataset/model meanings and resolved project scope.

AR aging and margin inherit the existing Accounting service's amount interpretation; this layer introduces no currency conversion or financial rule. AP aging, expenses, overdue debt, Work, CRM, HR/roster, executive dashboards and extra reporting subjects are outside this wave. No AP aging dataset exists in the approved pack. Drill navigation is not added; an optional declared target must resolve to an existing dataset drill target and future navigation must re-authorize the destination report.

## Validation

Registry-driven tests cover every shipped KPI with permission denial before fetch, resolved company forwarding, valid evaluation, and rejection of truncated totals. Additional tests exercise invalid definitions, confidential margin authority, unresolved company, actual Projects tenant isolation with asymmetric tenants and CrossCompany breadth, denied project scope, and actual Accounting source isolation with fixed Posted status and functional-currency values despite malicious Status/CompanyId input. Production registration resolves both registry and evaluator with scope validation; existing module authority and field security suites remain covered.

Final acceptance: 132 passed, 0 failed, 0 skipped. The successful build included normal analyzers; the final artifact run reused that build and added the existing field-authority suite. No SQL Server tests ran.

Source isolation is inherited from the existing source implementations; no raw query, IgnoreQueryFilters, all-company fallback or caller-selected tenant is introduced. Scoped registry construction requires both existing packs to be registered. AddKpiReporting is deliberately separate from generic AddCrossBusinessReporting so minimal reporting hosts do not acquire an unrelated Projects dependency.

Results are on demand, without a dashboard or caching layer. Live deployment/schema and SQL Server behavior remain unverified; no production database was accessed.
