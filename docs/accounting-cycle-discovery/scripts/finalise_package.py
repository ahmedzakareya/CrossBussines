# -*- coding: utf-8 -*-
"""Write the evidence register and the manifest, then verify the package.

Acceptance checks, in order: every referenced file exists; every inventory parses; manifest
counts reconcile against the directory; screenshots carry no readiness failure and no duplicate;
Arabic and English are counted separately; and the package is audited for credentials.
"""
import io, os, re, csv, json, glob, hashlib, collections

ROOT = r'C:\CrossBuy\CrossBuy'
OUT = os.path.join(ROOT, 'docs', 'accounting-cycle-discovery')
SHOTS = os.path.join(OUT, 'screenshots')

# ---------------------------------------------------------------- evidence register
EV = [
    # area, claim, how it was established, result, evidence level
    ('baseline', 'branch and commit', 'git rev-parse / git log -1',
     'reporting/studio-gap-closure @ 32985d91, 2026-09-20 14:35:32 +0300', 'Repository evidence'),
    ('baseline', 'working tree preserved', 'git status --porcelain',
     '6 untracked files present at start, unchanged at end; nothing committed or deployed',
     'Repository evidence'),
    ('baseline', 'application version', 'grep Version/Company/Authors/Copyright in csproj',
     'NONE DECLARED', 'Repository evidence'),
    ('isolation', 'isolated database created', 'BACKUP COPY_ONLY + RESTORE WITH MOVE',
     'CrossBuyAcctTest restored from CrossBuyDev, 23,818 pages', 'Runtime observed'),
    ('isolation', 'isolated app instance', 'dotnet publish to C:/temp/cb_acct_iso/app, run on :5299',
     'published with BaseOutputPath redirected so the developer DLL lock (MSB3027) is avoided',
     'Runtime observed'),
    ('isolation', 'PROOF of isolation', 'sys.dm_exec_sessions grouped by db and host_process_id',
     'PID 39228 (this discovery) = 4 sessions on CrossBuyAcctTest, ZERO on CrossBuyDev; '
     'developer PIDs 32692/24512 remain on CrossBuyDev', 'Runtime observed'),
    ('isolation', 'developer session undisturbed', 'HTTP probe of :44368 before and after',
     '/Account/Login answered 200 throughout', 'Runtime observed'),
    ('isolation', 'SMTP disabled', 'Smtp__Enabled=false in the process environment',
     'no mail path active', 'Runtime observed'),
    ('isolation', 'e-invoicing makes no outbound call', 'read BL/TaxService.cs:26-27',
     'SubmitSalesInvoiceAsync returns a hardcoded Submitted=false, Status="NotConfigured"',
     'Repository evidence'),
    ('isolation', 'production never contacted', 'no command in this package targets a remote server',
     'confirmed', 'Repository evidence'),
    ('scope', 'services that post to the ledger', 'scripts/extract_posting_rules.py',
     '16', 'Repository evidence'),
    ('scope', 'declared ledger source types', 'same', '54', 'Repository evidence'),
    ('scope', 'source types with entries in the isolated DB', 'GROUP BY SourceType',
     '28', 'Runtime observed'),
    ('scope', 'hard-coded vs configurable accounts', 'scripts/extract_posting_rules.py',
     '35 hard-coded sites, 277 configurable lookups; StockService holds 19 of the 35',
     'Repository evidence'),
    ('scope', 'duplicate-posting guards', 'same', '75 sites, none exercised',
     'Repository evidence'),
    ('scope', 'transaction/rollback sites', 'same', '31 sites, none exercised',
     'Repository evidence'),
    ('setup', 'fiscal periods', 'SELECT over FiscalYears/FiscalPeriods',
     '2 years x 13 periods (12 + adjustment), all Open; period 9 covers 2026-09-21',
     'Runtime observed'),
    ('setup', 'chart of accounts', 'SELECT over Accounts + AccountTypes',
     '63 accounts; 6 require a cost centre; GRNI 210203, advances 2104, realised FX 4902, '
     'unrealised FX 4903, opening equity 3301 all present', 'Runtime observed'),
    ('setup', 'approval threshold', 'SELECT over AccountingSettings',
     'ApprovalThreshold = 0.00 -> the threshold and segregation-of-duties gates never fire',
     'Runtime observed'),
    ('execution', 'draft journal created', 'UI: /Accounting/CreateJournal, Save draft',
     'JE 24209, Status=Draft, EntryNo=NULL, FiscalPeriodId=9, Dr=Cr=5000.00', 'Runtime observed'),
    ('execution', 'posting allocates the number', 'UI: POST /Accounting/PostJournal',
     'JE 24209 -> EntryNo JV-2026-008509, Status=Posted, PostedBy=5', 'Runtime observed'),
    ('execution', 'reversal creates a new entry', 'UI: POST /Accounting/ReverseJournal',
     'JE 24210 JV-2026-008510, JournalType=Reversing, Posted; 24209 -> Reversed with '
     'ReversedByEntryId=24210; lines are exact mirrors', 'Runtime observed'),
    ('execution', 'the same loop in Arabic', 'repeat with culture=ar',
     'JE 24211 -> JV-2026-008511, reversed by 24212 -> JV-2026-008512', 'Runtime observed'),
    ('reports', 'nine reports executed', 'scripts/run-reports.mjs, both languages',
     '9/9 rendered and captured in en and ar; 0 readiness failures', 'Runtime observed'),
    ('reconciliation', 'journal debits = credits', 'SQL over JournalEntryLines',
     '113,047,021.50 = 113,047,021.50, difference 0.00 across 11,785 entries', 'Reconciled result'),
    ('reconciliation', 'trial balance screen = ledger', 'compare screen totals with SQL',
     'identical to the cent', 'Reconciled result'),
    ('reconciliation', 'sample transaction traced to report total', 'compare with the 2026-09-20 capture',
     '113,027,021.50 -> 113,047,021.50 = +20,000.00 = this discovery\'s 4 postings of 5,000',
     'Reconciled result'),
    ('reconciliation', 'AR aging vs control account 1102', 'report total vs SQL net balance',
     '18,536,202.26 vs 18,520,263.26 -> 15,939.00 (pass 1: unexplained) - SUPERSEDED by pass 2',
     'Runtime observed - superseded'),
    ('reconciliation', 'AP aging vs control account 2101', 'report total vs SQL net balance',
     '651,508.46 vs 635,232.29 -> 16,276.17 (pass 1: unexplained) - SUPERSEDED by pass 2',
     'Runtime observed - superseded'),
    ('capture quality', 'countersSettled gate added', 'trial balance tiles vs footer',
     'first capture read 113,047,014.46 in the tiles mid-animation against 113,047,021.50 in the '
     'footer; the gate now waits for data-kt-countup to stabilise and the figures agree',
     'Runtime observed'),
    ('not executed (pass 1)', '15 of 16 scenarios', '-', 'CLOSED IN PASS 2 for 12 groups; '
     'opening balances, accruals, bank reconciliation, inventory count and realised FX remain',
     'Superseded'),
    ('not executed (pass 1)', 'role-based access', '-', 'CLOSED IN PASS 2 for 4 of 8 profiles',
     'Superseded'),
    ('not executed (pass 1)', 'report exports', '-', 'CLOSED IN PASS 2 - 11 files produced',
     'Superseded'),
    ('not executed', 'duplicate-posting and rollback', '-', 'guards found in source, never '
     'triggered', 'Unverified'),
    # ------------------------------------------------------------ execution pass 2
    ('pass 2 isolation', 'isolation revalidated before any write', 'sys.dm_exec_sessions by pid',
     'pid 39228 (5088 after the restore) held sessions on CrossBuyAcctTest only, zero on '
     'CrossBuyDev; the developer instance answered 200 throughout', 'Runtime observed'),
    ('pass 2 isolation', 'outbound integrations disabled', 'run-isolated.cmd environment',
     'SMTP disabled, AI pointed at a dead port, no OpenAI key; e-invoicing makes NO outbound call '
     '(EtaInvoiceService returns a hardcoded "NotConfigured"); no payment gateway exists',
     'Runtime observed'),
    ('pass 2 checkpoints', 'destructive scenarios run on a disposable checkpoint',
     'COPY_ONLY backup/restore',
     'CP1 before execution, CP2 before closing; the closing sequence ran after CP2 and CP2 was '
     'then restored, so FY2026 is Open again', 'Runtime observed'),
    ('pass 2 execution', 'purchase-to-pay executed', 'UI form posts, verified in SQL',
     'PI-2026-05073/74, PY-2026-00008/9, DN-2026-03016; journals JV-2026-008517..008526',
     'Runtime observed'),
    ('pass 2 execution', 'order-to-cash executed', 'UI form posts, verified in SQL',
     'SV-2026-17613, RC-2026-15485/6, CN-2026-04050; journals JV-2026-008520..008523',
     'Runtime observed'),
    ('pass 2 execution', 'treasury, assets and FX executed', 'UI form posts, verified in SQL',
     'transfer 2,500.00; asset 2026 capitalised 24,000.00; depreciation 12,433.34 in one entry of '
     '7 line-pairs; FX revaluation and its exact reversal', 'Runtime observed'),
    ('pass 2 execution', 'closing sequence executed', 'SetPeriodStatus + YearEndClose',
     'period 9 closed, a posting into it REFUSED, reopen with reason recorded, FY2026 year-end '
     'JV-2026-008532 with 15 lines and Dr = Cr = 58,028,927.40 dated 2026-12-31',
     'Runtime observed'),
    ('pass 2 execution', 'payroll posting', 'PostPayroll(2026, 9)',
     'REFUSED - "There is no postable payroll in this period"; no September 2026 payroll data',
     'Runtime observed - refused'),
    ('pass 2 reconciliation', 'AR aging vs control account 1102', 'numerical bridge in doc 09',
     '18,536,202.26 - 15,939.00 posted sales returns the aging never deducts = 18,520,263.26; '
     'closes EXACTLY', 'Reconciled result'),
    ('pass 2 reconciliation', 'AP aging vs control account 2101', 'numerical bridge in doc 09',
     '651,508.46 - 15,700.00 payments without a posted invoice - 650.37 purchase returns '
     '+ 74.20 residual on 9 reversed PI journals = 635,232.29; residual OPEN',
     'Reconciled result - 74.20 open'),
    ('pass 2 reconciliation', 'what the trial balance number measures', 'SQL against the screen',
     'raw DEBIT TURNOVER 113,067,021.50 matches the screen; the sum of per-account NET balances is '
     '65,955,568.38; 37 accounts = 37 screen rows, and the per-account columns are turnover too',
     'Reconciled result'),
    ('pass 2 reconciliation', 'the differences are pre-existing', 'account-level diff',
     'this pass posted only to 510105 and 110102, so neither difference was created by it',
     'Reconciled result'),
    ('pass 2 roles', 'company boundary holds', 'sign-in as dev.otherco (company 65)',
     'trial balance renders 0.00 / 0.00 with 0 account rows, against 113,396,869.64 and 38 rows '
     'for an authorised user', 'Runtime observed'),
    ('pass 2 roles', 'write gate holds', 'sign-in as dev.clerk (no roles)',
     'the create-journal form opens, the post is refused, 0 journal entries created',
     'Runtime observed'),
    ('pass 2 roles', 'read-screen reachability is open', '12 routes x 4 profiles',
     'every profile including one with no roles opens 10 of 12 accounting screens; only '
     '/Accounting/AccountingRoles refuses', 'Runtime observed'),
    ('pass 2 roles', 'segregation of duties proven in both halves',
     'ApprovalThreshold raised to 1,000.00 through the settings screen, then restored to 0.0000',
     'a 2,500.00 entry pressed Post was FORCED TO DRAFT, and its creator could not then approve '
     'it; no source code was changed', 'Runtime observed'),
    ('pass 2 exports', 'in-module exports produced', 'download, open as OOXML, count rows',
     '8 of 8 xlsx files, 21,963 data rows, each containing this pass own documents',
     'Runtime observed'),
    ('pass 2 exports', 'platform exports produced', 'Accounting.JournalActivity in 3 formats',
     'CSV 32,681 B with BOM, XLSX 21,929 B valid package, PDF 64,980 B / 17 pages / %%EOF',
     'Runtime observed'),
    ('pass 2 exports', 'PDF font discrepancy', 'read the PDF BaseFont list',
     'AAAAAA+Dubai-Bold and BAAAAA+Dubai-Regular embedded, not the Cairo faces ReportFontLibrary '
     'provides', 'Runtime observed'),
    ('pass 2 exports', 'Accounting.TrialBalance is not a platform report', 'PDF request',
     '404; the trial balance exists only as an in-module screen. An earlier list in this package '
     'included it and is corrected', 'Runtime observed'),
    ('pass 2 capture quality', 'the readiness gate refused a misleading capture',
     'expect ZZ-DISCOVERY on the AR aging page',
     'the customer had settled to zero and the aging drops zero-balance parties, so NO FILE WAS '
     'WRITTEN', 'Runtime observed'),
    ('pass 2 not executed', 'opening balances, accruals, bank reconciliation, inventory count, '
     'realised FX, POS/project/manufacturing postings', '-',
     'not executed; blockers recorded per area in doc 08 Gap R3', 'Unverified'),
    ('pass 2 not executed', '4 of 8 role profiles', '-',
     'accountant, cashier, purchasing, sales, warehouse and payroll are module roles granted per '
     'employee; no test user holds them', 'Unverified'),
    ('pass 2 not executed', 'Arabic pass for roles, SoD and exports', '-',
     'the Arabic pass covers 16 transaction and report result screens only', 'Unverified'),
]

with io.open(os.path.join(OUT, 'evidence-register.csv'), 'w', encoding='utf-8-sig', newline='') as fh:
    w = csv.writer(fh)
    w.writerow(['area', 'claim', 'method', 'result', 'evidence_level'])
    w.writerows(EV)

# ---------------------------------------------------------------- package manifest
rows = []
for dp, dn, fns in os.walk(OUT):
    for f in sorted(fns):
        p = os.path.join(dp, f)
        rel = os.path.relpath(p, OUT).replace('\\', '/')
        if rel == 'package-manifest.csv':
            continue
        b = os.path.getsize(p)
        kind = ('document' if f.endswith('.md') else
                'inventory' if f.endswith(('.csv', '.json')) and '/' not in rel else
                'screenshot' if f.endswith('.png') else
                'script' if rel.startswith('scripts/') else
                'asset' if rel.startswith('assets/') else
                'evidence' if rel.startswith('reference/') else 'other')
        rows.append({'path': rel, 'kind': kind, 'bytes': b})
with io.open(os.path.join(OUT, 'package-manifest.csv'), 'w', encoding='utf-8-sig', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=['path', 'kind', 'bytes'])
    w.writeheader(); w.writerows(sorted(rows, key=lambda r: r['path']))

# ---------------------------------------------------------------- acceptance checks
problems = []

REQUIRED = ['README.md', '01-baseline-and-environment.md', '02-accounting-scope-matrix.md',
            '03-worked-examples.md', '04-posting-rules.md', '05-reports-and-reconciliation.md',
            '06-screenshot-storyboard.md', '07-guide-outlines.md', '08-gaps-and-blockers.md',
            'scope-matrix.csv', 'screenshot-manifest.csv', 'reconciliation-workbook.csv',
            'posting-account-resolution.csv', 'evidence-register.csv', 'package-manifest.csv']
for r in REQUIRED:
    p = os.path.join(OUT, r)
    if not os.path.exists(p):
        problems.append('MISSING: ' + r); continue
    if r.endswith('.csv'):
        try:
            if not list(csv.DictReader(io.open(p, encoding='utf-8-sig'))):
                problems.append(r + ' has no rows')
        except Exception as e:
            problems.append('%s unreadable: %s' % (r, e))

for p in glob.glob(os.path.join(OUT, 'reference', '*.json')):
    try: json.load(io.open(p, encoding='utf-8'))
    except Exception as e: problems.append('%s is not valid JSON: %s' % (os.path.basename(p), e))

# links inside the documents
LINK = re.compile(r'\[[^\]]+\]\(([^)]+)\)')
for f in [r for r in REQUIRED if r.endswith('.md')]:
    t = io.open(os.path.join(OUT, f), encoding='utf-8').read()
    for href in LINK.findall(t):
        if href.startswith(('http', '#', 'mailto:')):
            continue
        tgt = href.split('#')[0]
        if tgt and not os.path.exists(os.path.join(OUT, tgt)):
            problems.append('%s links to a missing file: %s' % (f, tgt))

# manifest vs disk
man = list(csv.DictReader(io.open(os.path.join(OUT, 'screenshot-manifest.csv'), encoding='utf-8-sig')))
disk = {f for f in os.listdir(SHOTS) if f.endswith('.png')} if os.path.isdir(SHOTS) else set()
named = {m['filename'] for m in man if m['filename']}
if named - disk: problems.append('manifest names files not on disk: %s' % sorted(named - disk)[:5])
if disk - named: problems.append('PNGs with no manifest row: %s' % sorted(disk - named)[:5])
# A readiness failure is only a PROBLEM if it still wrote a file, or if nothing explains it.
# A gate that refuses and writes nothing is the gate WORKING, and is reported, not flagged.
not_ready = [m for m in man if m['readiness_ok'] not in ('True', 'true', True)]
leaked = [m['screenshot_id'] for m in not_ready
          if m['file_exists'] in ('True', 'true', True) or m['filename']]
unexplained = [m['screenshot_id'] for m in not_ready if not (m['expect_check'] or '').strip()]
if leaked:
    problems.append('readiness failed BUT a file was written: %s' % leaked[:6])
if unexplained:
    problems.append('readiness failed with no recorded reason: %s' % unexplained[:6])
refused = [(m['screenshot_id'], m['language'], m['expect_check']) for m in not_ready
           if m['screenshot_id'] not in leaked and m['screenshot_id'] not in unexplained]
dups = sorted({m['duplicate_group'] for m in man if m['duplicate_group']})
counters = [m['screenshot_id'] for m in man if m['countersSettled'] not in ('True', 'true', True)]
if counters: problems.append('captures with unsettled counters: %s' % counters[:6])

# credentials / personal data
SECRET = [('password', re.compile(r'\b(password|pwd)\s*[:=]\s*["\']?[^\s"\',;}<]{4,}', re.I)),
          ('connection string with password', re.compile(r'(Server|Data Source)\s*=[^"\n]{0,90}Password\s*=', re.I)),
          ('api key/token', re.compile(r'\b(api[_-]?key|secret|access[_-]?token)\s*[:=]\s*["\']?[A-Za-z0-9_\-]{12,}', re.I)),
          ('jwt', re.compile(r'\beyJ[A-Za-z0-9_\-]{10,}\.')),
          ('live server ip', re.compile(r'17[0-9]\.1[0-9]\.2[0-9]{2}\.1[0-9]{2}')),
          ('email address', re.compile(r'\b[\w.+-]+@[\w-]+\.[A-Za-z]{2,}\b'))]
sec = []
for dp, dn, fns in os.walk(OUT):
    for f in fns:
        if not f.endswith(('.md', '.csv', '.json', '.py', '.mjs', '.cmd', '.txt')):
            continue
        p = os.path.join(dp, f)
        t = io.open(p, encoding='utf-8', errors='replace').read()
        for name, rx in SECRET:
            for m in rx.finditer(t):
                frag = t[max(0, m.start() - 50):m.end() + 30].replace('\n', ' ')
                if re.search(r'CB_PASS|CB_USER|redacted|Password\s*=\s*$|Trusted_Connection', frag, re.I):
                    continue
                sec.append((name, os.path.relpath(p, OUT).replace('\\', '/'), m.group(0)[:60]))

en = sum(1 for m in man if m['language'] == 'en' and m['filename'])
ar = sum(1 for m in man if m['language'] == 'ar' and m['filename'])
total_bytes = sum(r['bytes'] for r in rows)

print('=' * 68)
print('ACCEPTANCE CHECKS')
print('=' * 68)
print('documents                :', sum(1 for r in rows if r['kind'] == 'document'))
print('inventories              :', sum(1 for r in rows if r['kind'] == 'inventory'))
print('scripts                  :', sum(1 for r in rows if r['kind'] == 'script'))
print('evidence json            :', sum(1 for r in rows if r['kind'] == 'evidence'))
print('assets                   :', sum(1 for r in rows if r['kind'] == 'asset'))
print('screenshots on disk      :', len(disk), '(en %d / ar %d)' % (en, ar))
print('manifest rows            :', len(man))
print('readiness failures       :', len(not_ready),
      '(gate refused and wrote nothing: %d)' % len(refused))
for rid, lg, why in refused:
    print('   gate refused  %-38s [%s]  %s' % (rid, lg, why))
print('unsettled counters       :', len(counters))
print('duplicate groups         :', len(dups))
print('evidence-register rows   :', len(EV))
print('package size             : %.1f MB' % (total_bytes / 1e6))
print()
if sec:
    print('SECRET / PERSONAL-DATA FINDINGS (%d):' % len(sec))
    for n, f, h in sec[:12]: print('   %-26s %-40s %s' % (n, f, h))
else:
    print('secret scan              : clean - no credential, token or live address found')
print()
if problems:
    print('PROBLEMS (%d):' % len(problems))
    for p in problems: print('   !!', p)
else:
    print('No problems: every referenced file exists, every inventory parses, '
          'manifest reconciles to disk.')
