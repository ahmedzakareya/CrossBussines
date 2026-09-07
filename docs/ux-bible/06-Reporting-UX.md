# 06 — Reporting UX

Reporting is an analytical platform with discovery, parameterization, viewing, export, drill-through, and provenance. Current evidence: `Views/Reports/Index.cshtml`, `Viewer.cshtml`, `_ReportContent.cshtml`, `Views/Workspace/Reports.cshtml`, `Views/Shared/_LayoutReporting.cshtml`, and `wwwroot/Backend-assets/css/crossbusiness-reporting.css`.

## Contract

- **Users/goal:** authorized operators, analysts, and managers; answer a business question and act on trustworthy data.
- **Layout:** report identity/provenance; parameter bar; command bar; result canvas; optional details/drill panel.
- **Filters:** explicit applied vs draft state; validate dependencies; display timezone, company/branch scope, currency and as-of time.
- **Actions:** Run is primary before results; Export/Print/Save view are secondary; row drill-through is contextual.
- **Permissions:** authorize report, dataset, fields, rows, export, and target drill route independently.

## States and behavior

Skeleton only stable report chrome; show determinate progress for queued generation. Empty distinguishes valid zero results from missing required filters. Unavailable retains parameters. Denied reveals no sensitive dataset metadata. Validation is adjacent to parameters with a summary. Partial/stale output is visibly marked. Success announces row count/time and preserves parameters.

Tables use `28-Table-Patterns.md`; charts use approved design-system chart semantics. Desktop may use sticky parameters/headers; mobile becomes parameter drawer plus card/priority columns. Numbers remain LTR/tabular in RTL. Keyboard users can reach parameters, run, result summary, grid, and drill target. Paginate/stream large results and cancel obsolete runs. Future evolution includes saved views and asynchronous exports without changing authorization.
