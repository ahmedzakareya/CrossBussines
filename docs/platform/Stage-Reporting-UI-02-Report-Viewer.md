# Stage Reporting UI — 02 · The Report Viewer

**Platform:** CrossBusiness Reporting Platform
**Phase:** R3 Phase 3
**Routes:** `GET /Reports/Viewer/{code}` · `GET /Reports/Export/{code}` · `GET /Reports/Download/{id}` ·
`GET /Reports/Rerun/{runId}`
**Files:** `Views/Reports/Viewer.cshtml`, `BL/Reporting/ReportsCenterPresenter.cs`,
`Controllers/ReportsController.cs`

---

## 1. Everything the brief asked for

| Required | Present |
|---|---|
| report title · description | page head, with the module chip and the definition's icon |
| parameters | left rail, input type derived from the declared parameter type |
| validation messages | against the input that caused them, not in a banner |
| HTML preview | right pane, the renderer's own markup |
| CSV export · Excel export | buttons, only for formats the deployment can actually produce |
| run warnings | above the result |
| truncation warning | **first**, above everything |
| history record | per-report run history panel |
| archive status | per-report archive panel with retrievability state |
| save current configuration · fork template · favourite/unfavourite | rendered, **disabled**, with a reason — see §5 |

**No PDF. No Stimulsoft. No designer.** Stopping short of the Report Studio is the brief.

---

## 2. Running is an act

`?run=true` separates opening the screen from executing the report. Landing on a report with an expensive
default range and having it run unasked is how a reporting product becomes the thing that slows the database
down every morning. First open shows the parameter form and an empty pane that says so.

Running is a **GET**, so the URL after a run *is* the report: bookmarkable, shareable, re-runnable. And it is
not a shared grant — the destination authorizes on arrival, so a link forwarded to someone without the
permission gets a 404.

---

## 3. Authorization before fetch

`BuildViewerAsync`'s first statement that touches the report is the gate:

```csharp
var definition = await _reports.DescribeAsync(query.Code, cancellationToken);
if (definition is null) return null;
```

`DescribeAsync` returns null when the caller may not see it, so an unpermitted code leaves the method before a
parameter is bound, before a template is resolved, and before the data source is constructed.

`A_run_requested_by_an_unpermitted_caller_never_executes` proves it with a recording data source: `run=true`
was requested, the model is null, and `WasEntered` is false.

`null` covers both "no such report" and "not yours", and the controller turns both into **404**. Telling them
apart would hand an unauthorized caller a catalog-enumeration oracle.

---

## 4. Truncation

Above the table, not below it and not in a footer. **A partial answer that announces itself after the reader
has already drawn a conclusion has announced nothing.**

`ReportPreviewModel.Truncated` is `required` and non-nullable, so the compiler refuses a preview model that
does not set it — and it carries the engine's verdict rather than being recomputed from the row count, which
would go wrong the moment a report legitimately returns exactly the cap.
`The_truncation_flag_cannot_be_omitted_from_a_preview_model` asserts the `required` modifier through
reflection, so removing it to fix an unrelated build error breaks a test instead of shipping a preview that can
silently lose the flag.

On export the flag rides an `X-Report-Truncated` header as well: a downloaded file has no envelope to carry
"this is partial", and a silently partial export is a wrong export.

---

## 5. The write controls are disabled, not hidden

Save configuration · Fork template · Favourite are **rendered and dimmed**, carrying the reason in a tooltip
and a notice.

Hiding them would make the product look finished and quietly lose a feature. A disabled control with an
explanation is the honest state of a system waiting on an approval — and it makes activation a flag flip
rather than a UI rewrite. `ReportingWriteSurface.IsActivated` is the single switch; see
`Stage-Reporting-UI-03`.

---

## 6. Export is the same shaped result as preview

There is **one** request builder, `ReportsCenterPresenter.BuildRequest`, used by both. Preview and export
differ in exactly two fields, `Kind` and `Format`, and in nothing else — which is what makes "the file is what
you looked at" true by construction rather than by two call sites happening to agree today.
`Preview_and_export_build_the_same_request_apart_from_kind_and_format` pins it.

A preview never carries an archive instruction whatever the caller asks: archiving a capped render would put a
partial artifact in the permanent record under the same name as the full one.

The export buttons carry the run's own query string forward, so the file matches what is on screen.

---

## 7. Two smaller decisions

**A denial is not an error list.** The engine encodes refusal as an `Error` diagnostic. Surfacing that in the
error strip would show an authorization reason code to somebody who cannot act on it, and would tell a prober
exactly which gate stopped them. `Denied` is its own flag; the error list is empty in that case, and the screen
says "you are not permitted to run this report".

**`@Html.Raw` on the preview is justified, not assumed.** The HTML is produced by `HtmlReportRenderer`, which
encodes every cell through `WebUtility.HtmlEncode`.
`A_cell_containing_markup_is_encoded_by_the_renderer_the_viewer_injects` feeds a `<script>` tag through a data
source and asserts it comes out as `&lt;script&gt;`. The renderer's markup is **confined** (overflow) but not
restyled — the preview must predict the exported artifact, and a screen that repainted it would break that
promise.

---

## 8. Parameters

Input type comes from the declared parameter type — `Date` → date picker, `Integer`/`Decimal`/`Currency` →
number, options → `<select>` — so the browser rejects locally what the binder would reject on the server.

Submitted values are echoed back, so a failed run does not clear the form. Field-level diagnostics are hoisted
onto their parameter: a banner reading "From is required" above eight inputs makes the user hunt.

`Answerable()` drops `SystemSupplied` parameters. `Visible()` drops `Internal` columns. Both projections happen
**once**, in the presenter, which is why "internal fields cannot be filtered" is structural rather than
checked — a column the UI never learns about cannot be offered as a filter.

---

## 9. Re-run

`GET /Reports/Rerun/{runId}` rebuilds the Viewer URL from the parameters a past run recorded and **redirects**
rather than rendering: the resulting URL is the shareable one, and the user sees and can edit what they are
about to re-run before it executes.

System-supplied parameters are **not** replayed. `CompanyId` in particular — replaying a recorded company would
be a caller-supplied tenant arriving through the back door of a history link.
