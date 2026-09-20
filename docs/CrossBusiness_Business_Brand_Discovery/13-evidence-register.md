# 13 — Evidence register

Every claim in this package, traced to the command that produced it. Re-run anything here to
check it.

**Pinned state:** branch `reporting/studio-gap-closure`, `HEAD = 2020ee5a` (2026-09-20 10:05:28
+0300), working tree carrying 74 uncommitted entries. See document 01 §2–3 before concluding that
a differing answer means a wrong claim.

Unless noted, commands run from the repository root `C:\CrossBuy\CrossBuy` with
`PYTHONIOENCODING=utf-8` set (without it, Python's default Windows console codepage cannot print
Arabic and the command dies at the first label).

---

## 1. The scripts

All four are in `scripts/`, exactly as run.

| Script | Run as | Produces |
|---|---|---|
| `extract_brand.py` | `python scripts/extract_brand.py` | `brand-tokens.json`, `asset-manifest.csv` |
| `extract_catalog.py` | `python scripts/extract_catalog.py` | `business-catalog.json` |
| `shots.mjs` | `node <browser-automation>/browser.mjs http://localhost:5268/Account/Login --script scripts/shots.mjs --timeout 60000` | `screenshots/*.png`, `screenshots/capture-log.json` |
| `probe2.mjs` | same, with `--script scripts/probe2.mjs` | `screenshots/*-ar.png`, `screenshots/probe-rtl-and-amber.json` |

The two `.mjs` scripts require the application running as in document 01 §4.2.

**Reading the extraction scripts is part of the evidence.** `extract_brand.py` computes WCAG
contrast from first principles rather than trusting the CSS comments; `extract_catalog.py` parses
`MainMenu.cs` line by line rather than by regexing the whole file. Both choices are visible in the
source and both matter to whether the numbers are trustworthy.

---

## 2. Baseline — document 01

| Claim | Command |
|---|---|
| Branch, commit, date, subject | `git log -1 --format="%H%n%ad%n%s"` ; `git rev-parse --abbrev-ref HEAD` |
| 245 commits | `git rev-list --count HEAD` |
| 74 dirty entries (52 ` M`, 22 `??`) | `git status --porcelain \| wc -l` ; `\| cut -c1-2 \| sort \| uniq -c` |
| 4 projects | `grep -oE 'Project\("[^"]*"\) = "[^"]+"' *.sln` |
| Top-level directories | `ls -d */` |
| `net8.0`, SDK 10.0.401 | `grep -oE "<TargetFramework>[^<]*" CrossBuy/CrossBuy.csproj` ; `dotnet --version` |
| 134 BL services, 13 sub-namespaces | `ls CrossBuy/BL/*.cs \| wc -l` ; `ls -d CrossBuy/BL/*/` |
| App runs, login 200 | `dotnet run --launch-profile http` ; `curl -o /dev/null -w "%{http_code}" http://localhost:5268/Account/Login` |

---

## 3. Identity — document 02

| Claim | Command / file |
|---|---|
| Six brand copy strings × 4 resource files | `Resources/SharedResources{,.ar,.en,.fr}.resx`, keys `BrandTagline`, `BrandHeadlineLead`, `BrandHeadlineAccent`, `BrandSubLine1`, `BrandSubLine2`, `BrandFootline` |
| Every layout's `<title>` | `for f in CrossBuy/Views/Shared/_Layout*.cshtml; do grep -oE '<title>.*</title>' $f; done` |
| `PageTitle` resource values ×3 layouts ×3 cultures | `Resources/Views/Shared/_Layout{Inventory,Accounting,Backend}.{ar,en,fr}.resx` |
| Which layout each screen uses | `grep -oE 'Layout = "[^"]*"' CrossBuy/Views/<Folder>/<View>.cshtml` |
| Titles as rendered | `screenshots/capture-log.json` → `title` |
| `CrossBusiness` in 22 files | `grep -rl "CrossBusiness" --include=*.cshtml --include=*.cs --include=*.resx .` |
| `_LayoutWorkspace` unreferenced | `grep -rl "_LayoutWorkspace" CrossBuy/Views --include=*.cshtml` → no output |
| Sign-in body copy | `screenshots/capture-log.json`, `screenshots/01-login.png` |
| Portal tiles and targets | `grep -nE 'msys-t\|Url.Action\|Localizer\[' CrossBuy/Views/Portal/Choose.cshtml` |

---

## 4. Modules — document 03

| Claim | Source |
|---|---|
| 10 menus, 41 categories, 186 items, all labels | `business-catalog.json` → `modules`; parsed from `CrossBuy/Models/Menu/MainMenu.cs` |
| Every `MainMenu.X()` call site | `grep -rn "MainMenu\.[A-Za-z]*()" CrossBuy/Views CrossBuy/Controllers` |
| 57 controllers / 1,203 actions | `business-catalog.json` → `controllers` |
| 372 views in 36 folders | `business-catalog.json` → `views_by_folder` |
| Menu renderer behaviour, permission hooks | `CrossBuy/Views/Shared/_MainMenu.cshtml` |

---

## 5. Domain — document 04

| Claim | Source |
|---|---|
| 265 entity sets, by namespace | `business-catalog.json` → `persisted_entities`; `grep -oE "DbSet<[A-Za-z0-9_.]+>" CrossBuy/Models/Context/CrossDbContext.cs` |
| 28 registered entity codes | `grep -oE 'public const string \w+ = "[A-Za-z]+"' CrossBuy/BL/Platform/EntityRegistry.cs` |
| 47 business event types | `grep -oE '"[A-Za-z]+\.[A-Za-z]+"' CrossBuy/BL/Platform/BusinessEventTypes.cs` |
| Dispatch settings (50/15s/5/30s/10min) | `appsettings.json` → `Platform:EventDispatch` |
| Company resolution; the `?companyId=` defect | `CrossBuy/Controllers/Api/AiController.cs` lines 41–72 |
| `Hierarchicals` has no `CompanyID` | `CrossBuy/BL/Reporting/OrgStructureDatasets.cs`, header comment |
| `Store:StoreCompanyId = 1` | `appsettings.json` |

---

## 6. Journeys — document 05

| Claim | Source |
|---|---|
| Route table; no `action=Index` default | `grep -n "MapControllerRoute" CrossBuy/Program.cs` → line 959–961 |
| `/Inventory`, `/Accounting`, `/Crm`, `/Admin` 404 | First capture run's `requests failed` report |
| Landing on `/Portal/Choose` | `screenshots/capture-log.json` → `landedAfterLogin` |
| 21 services post to the ledger | `grep -rln "IJournalEntryService\|PostJournal\|CreateJournalAsync" CrossBuy/BL/*.cs` |
| 4 approval silos | `CrossBuy/BL/Approvals/ApprovalReadContracts.cs` → `ApprovalSilos` |
| Payroll line composition | `CrossBuy/BL/AccountingPostingService.cs` → `PayrollEmployeeLine` |

> **Unverified and flagged in the document:** whether a CRM opportunity converts to a quotation in
> the product. Not traced in this discovery. Do not assume either answer.

---

## 7. Reporting — document 06

| Claim | Source |
|---|---|
| 60 files / 25,354 lines | `ls CrossBuy/BL/Reporting/*.cs \| wc -l` ; `cat CrossBuy/BL/Reporting/*.cs \| wc -l` |
| 41 report codes | `business-catalog.json` → `report_codes` |
| 7 permission keys | `business-catalog.json` → `report_permission_keys` |
| 5 output formats, with reasons | `CrossBuy/BL/Reporting/ReportContracts.cs` lines 22–41 |
| Engine names | `grep -oE 'EngineName => \$?"[^"]+"' CrossBuy/BL/Reporting/*.cs` |
| Registration pattern | `CrossBuy/BL/Reporting/ReportingRegistration.cs` lines 44–180 |
| Scheduler off; `NullReportMailSender` | `ReportingRegistration.cs:95`; `git log --oneline` 2026-09-17 |
| Executive dashboard KPI labels | `grep -oE 'Localizer\["[^"]{3,50}"\]\|SR\["[^"]{3,50}"\]' CrossBuy/Views/Accounting/Executive.cshtml \| sort -u` |

---

## 8. AI — document 07

| Claim | Source |
|---|---|
| Service description, ports, phase | `crossbuy_ai/README.md` |
| ML modules are local, no LLM | `crossbuy_ai/app/ml/{anomaly,forecast,inventory}.py` docstrings |
| Dependencies | `crossbuy_ai/requirements.txt` |
| 7 .NET endpoints | `grep -nE '\[Http(Get\|Post)\("[^"]+"\)\]' CrossBuy/Controllers/Api/AiController.cs` |
| The egress matrix | `CrossBuy/BL/Platform/Ai/AiEgressPolicy.cs` lines 45–92 (`Permitted`) |
| Classifications, purposes, fail-closed zero values | `AiEgressPolicy.cs` lines 16–70; `AiDataClassification` enum |
| Config: `Internal` / `LocalLoopback` / `localhost:8000` | `appsettings.json` → `AiService` |
| `Platform.AiEgressAudit` persisted | `business-catalog.json` → `persisted_entities` |

> **Unverified and flagged:** no AI call was made. Whether the CRM insight/scoring/automation
> screens and `Tasks/MatchSuggestions` are model-backed was not established.

---

## 9. Visual identity — document 08

| Claim | Source |
|---|---|
| 129 tokens / 149 declarations / 114 colours / 8 gradients / 23 loading views | `brand-tokens.json` (from `extract_brand.py`) |
| **Every contrast ratio** | `brand-tokens.json` → `colour_tokens.*.contrast_*` — computed, not copied |
| Role split and its reasoning | `wwwroot/Backend-assets/css/crossbuy-brand.css` lines 1–30 |
| `btn-warning` dark label at 10.70 | `crossbuy-brand.css` lines 419–455 |
| **Logo is blue + amber; wordmark is live text** | `assets/logos/crossbuy-logo.svg`, `crossbuy-logo-light.svg` — read them, they are 10 lines each |
| Asset dimensions and sizes | `asset-manifest.csv`; PNG header parse in §1's script |
| Cairo embedded for PDF | `CrossBuy.csproj` line 135 |
| Variable-TTF / PDF constraint | `CrossBuy/BL/Reporting/ReportFontLibrary.cs` |
| OFL licence present | `assets/fonts/OFL.txt` |
| Cairo loaded at runtime; Inter failed | `screenshots/probe-rtl-and-amber.json` → `cairoLoaded`, `fontFaces`; capture run `requests failed` |

---

## 10. Localisation — document 10

| Claim | Command |
|---|---|
| 891 resx / 308 units / ar 308, fr 293, en 289 | `business-catalog.json` → `localization` |
| The 19 / 15 / 12 missing-culture lists | `extract_catalog.py`'s walk, reproduced by the inline Python in this discovery over `CrossBuy/Resources` |
| 0 Arabic localizer keys; 25 hardcoded Arabic literals in 10 views | Python scan with `re.compile(r'[\u0600-\u06FF]')` over all 372 `.cshtml`. **Do not use `grep -E '[؀-ۿ]'` for this** — this environment's grep matched the range bytewise and returned English strings containing `«»` and `✓` |
| 56 twin-column pairs | Python scan of `CrossBuy/Models` for `X` / `XEn` string property pairs |
| Cultures and `FixNumbers` | `CrossBuy/Program.cs` lines 59–72 |
| `dir=rtl`, RTL bundle served | `screenshots/probe-rtl-and-amber.json` → `arabicPass[].dir`, `styleBundles` |

---

## 11. Runtime and drift — documents 11, 12

| Claim | Source |
|---|---|
| All 24 screens, status, title, lang, dir, tokens, sidebar counts | `screenshots/capture-log.json` |
| RTL pass + amber located by class and text | `screenshots/probe-rtl-and-amber.json` |
| 13 amber elements across 4 screens | same → `amber` |
| **0 `btn-warning` in 372 views** | `grep -rn "btn-warning" CrossBuy/Views --include=*.cshtml \| wc -l` |
| 6 `bg-warning` in views (vs 13 at runtime → the rest come from partials/JS) | `grep -rn "bg-warning" CrossBuy/Views --include=*.cshtml \| wc -l` |
| 285/372 views with literal hex; per-view counts | `business-catalog.json` → `brand_drift` |
| Top-40 colours, token or not | same → `most_used_colours` |
| `Portal/Choose` palette (23 distinct, 1 token) | `grep -oE '#[0-9A-Fa-f]{6}' CrossBuy/Views/Portal/Choose.cshtml \| sort \| uniq -c` |
| 102 unreferenced assets | `asset-manifest.csv` → `reference_count == 0` |
| SSO configured and live | `screenshots/01-login.png`; `grep -n "AddMicrosoftAccount\|AddGoogle" CrossBuy/Program.cs` → 712, 724; `CrossBuy/Views/Account/Login.cshtml:663-668` |
| Three `-en` screenshots deleted as byte-identical | `sha256sum` comparison, recorded in document 11 §2 |

---

## 12. Traps this discovery hit, so the next reader does not

1. **`grep -E '[؀-ۿ]'` does not match Arabic reliably here.** It returned English strings
   containing `«»`. Use Python with an explicit `\u0600-\u06FF` range.
2. **`os.listdir('Resources')` finds 4 resx files; `os.walk` finds 891.** The per-screen files are
   under `Resources/Views/…`. A flat listing understates the localisation estate by 99%.
3. **A failed `page.goto` leaves a pending navigation.** Without parking on `about:blank` after a
   failure, every subsequent `goto` dies with *"interrupted by another navigation"* — one 404 took
   down 19 of 20 captures on the first run.
4. **`nohup … &` inside the Bash tool does not survive.** Run the long process as the backgrounded
   command itself.
5. **Bash heredocs mangle backslashes** in Python payloads (`stem.replace('\\', '/')` became a
   syntax error). Write script files with a file tool instead.
6. **`/Inventory` is a 404, not a redirect.** Any probe of this product must use
   `/Controller/Action`.
7. **`HEAD` can move under you.** It did, 34 minutes in.

---

## 13. What could not be verified

Stated so nobody infers coverage that was not achieved:

- **Nothing was checked against the live database or a production install.** All runtime evidence
  is one development instance with development data.
- **The Flutter client (`crossbuy_mobile/`) was not examined** beyond noting its presence.
- **The AI service was not started.** Document 07 is code and configuration, not behaviour.
- **Dashboard figures, row counts and any data-dependent number** are development data and are
  deliberately absent from this package.
- **No permission matrix was exercised.** Every runtime observation is as a single admin account.
  What a restricted user sees was not measured.
- **The CRM → quotation conversion path** was not traced.
- **Whether the 102 unreferenced assets are genuinely dead** was not confirmed; the scan searches
  for filenames in source text and will miss a path assembled at runtime.
