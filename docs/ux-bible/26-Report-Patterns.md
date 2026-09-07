# 26 — Report Patterns

Report Center: searchable catalog, domain/category, description, permission-aware availability, favorites/recent where supported. Viewer: provenance header, parameters, run status, command bar, result, drill detail. Embed/print: reduced shell with identity, filters/as-of and export/print fidelity.

Applied filters are always visible. Changing draft parameters does not silently alter displayed results; mark results stale until Run. Exports reflect the displayed filter/context and disclose background generation. Drill-through preserves report state and offers a return path.

Use `Views/Reports/Index.cshtml`, `Viewer.cshtml`, `_ReportContent.cshtml`, and `_LayoutReporting.cshtml` as current evidence. Do not duplicate report viewers per module. Empty-zero, no permission, parameter invalid, provider unavailable, timed out and partial results are separate states.
