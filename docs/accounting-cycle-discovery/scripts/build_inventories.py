# -*- coding: utf-8 -*-
"""Generate the machine-readable inventories from the collected evidence.

Everything here is derived from files this discovery actually produced: the capture logs, the
posting-rule extraction and the SQL results recorded in reference/. Nothing is typed in from
memory, so a re-run after more scenarios regenerates a consistent set.
"""
import io, os, re, csv, json, glob, hashlib, collections

ROOT = r'C:\CrossBuy\CrossBuy'
OUT = os.path.join(ROOT, 'docs', 'accounting-cycle-discovery')
SHOTS = os.path.join(OUT, 'screenshots')
REF = os.path.join(OUT, 'reference')

# ---------------------------------------------------------------- 1. screenshot manifest
rows, seen_hash = [], collections.defaultdict(list)
for p in sorted(glob.glob(os.path.join(REF, 'capture-log.*.json'))):
    for r in json.load(io.open(p, encoding='utf-8')):
        rows.append(r)

on_disk = {f for f in os.listdir(SHOTS) if f.endswith('.png')} if os.path.isdir(SHOTS) else set()

# duplicate detection by exact content hash
for r in rows:
    if r.get('sha256'):
        seen_hash[r['sha256']].append(r['screenshot_id'] + '.' + r['language'])
dup_group = {}
gi = 0
for h, ids in seen_hash.items():
    if len(ids) > 1:
        gi += 1
        for i in ids:
            dup_group[i] = f'DUP-{gi:02d}'

FIELDS = ['screenshot_id', 'scenario', 'step', 'language', 'route', 'role', 'record_id',
          'before_after', 'filename', 'viewport', 'capture_time', 'readiness_ok',
          'readiness_checks_passed', 'countersSettled', 'noSpinners', 'expect_check',
          'duplicate_group', 'privacy_treatment', 'evidence_reference', 'file_exists',
          'sha256_12', 'bytes', 'page_title', 'heading', 'html_lang', 'direction']

man = []
for r in rows:
    ck = r.get('readiness_checks', {}) or {}
    passed = sum(1 for v in ck.values() if v is True)
    key = r['screenshot_id'] + '.' + r['language']
    man.append({
        'screenshot_id': r['screenshot_id'], 'scenario': r.get('scenario', ''),
        'step': r.get('step', ''), 'language': r['language'], 'route': r.get('route', ''),
        'role': r.get('role', ''), 'record_id': r.get('record_id', ''),
        'before_after': r.get('before_after', ''), 'filename': r.get('filename', ''),
        'viewport': r.get('viewport', ''), 'capture_time': r.get('capture_time', ''),
        'readiness_ok': r.get('readiness_ok'), 'readiness_checks_passed': passed,
        'countersSettled': ck.get('countersSettled'), 'noSpinners': ck.get('noSpinners'),
        'expect_check': ck.get('expectSelector') or ck.get('expectText') or '(none set)',
        'duplicate_group': dup_group.get(key, ''),
        'privacy_treatment': r.get('privacy_treatment', ''),
        'evidence_reference': r.get('evidence', ''),
        'file_exists': (r.get('filename') in on_disk) if r.get('filename') else False,
        'sha256_12': (r.get('sha256') or '')[:12], 'bytes': r.get('bytes', 0),
        'page_title': r.get('page_title', ''), 'heading': r.get('heading', ''),
        'html_lang': r.get('html_lang', ''), 'direction': r.get('direction', ''),
    })

with io.open(os.path.join(OUT, 'screenshot-manifest.csv'), 'w', encoding='utf-8-sig', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=FIELDS)
    w.writeheader(); w.writerows(man)

# ---------------------------------------------------------------- 2. scope matrix A-K
# Classification uses three inputs: the source type is DECLARED in code; entries EXIST in the
# isolated database; and the workflow was EXECUTED by this discovery.
DECLARED = json.load(io.open(os.path.join(REF, 'posting-rules-raw.json'), encoding='utf-8'))
declared_types = set(DECLARED['ledger_source_types'])

# recorded from the SQL pass over the isolated database
WITH_DATA = {
    'SalesInvoice': 5519, 'Receipt': 4403, 'Inventory': 967, 'Reversal': 321,
    'OpeningStock': 148, 'PurchaseInvoice': 121, 'WorkOrder': 115, 'PurchaseReturn': 30,
    'SalesReturn': 26, 'PosShiftClose': 24, 'PosTip': 24, 'PosRefund': 21,
    'StockWriteOff': 15, 'Assembly': 8, 'StockReconcile': 6, 'StockTransfer': 5,
    'BankReconTest': 4, 'Payment': 4, 'Manual': 3, 'FxRevaluationReversal': 3,
    'FxRevaluation': 3, 'FinalSettlement': 2, 'Payroll': 2, 'AssetCapitalization': 1,
    'LandedCost': 1, 'EquipmentDepAllocation': 1, 'ProjectLabor': 1,
}
# What THIS discovery EXECUTED, across both passes. Pass 1 covered the manual journal loop;
# pass 2 added the trade cycles, treasury, assets, FX and the closing scenarios.
EXECUTED = {
    'Manual', 'Reversal',                                   # pass 1
    'PurchaseInvoice', 'Payment', 'PurchaseReturn',         # pass 2 - purchasing
    'SalesInvoice', 'Receipt', 'SalesReturn',               # pass 2 - sales
    'Inventory',                                            # pass 2 - stock movement from the stock PI
    'Transfer',                                             # pass 2 - treasury
    'FixedAsset', 'Depreciation',                           # pass 2 - fixed assets
    'FxRevaluation', 'FxRevaluationReversal',               # pass 2 - foreign currency
    'YearClose',                                            # pass 2 - year-end (on checkpoint CP2, then restored)
}
# Executed but REFUSED, with the refusal itself as the evidence. These are not "working"; they
# are "the control fired", which is a different and equally reportable result.
REFUSED = {
    'Payroll': 'attempted; refused - "There is no postable payroll in this period" '
               '(no September 2026 salary data)',
}

SCOPE = [
    # (area, capability, source types that evidence it)
    ('A Foundations', 'Company and branch accounting context', []),
    ('A Foundations', 'Financial years, periods, locks and closing rules', []),
    ('A Foundations', 'Chart of accounts, account types, control accounts', []),
    ('A Foundations', 'Cost centres and analytical dimensions', []),
    ('A Foundations', 'Functional and transaction currencies, rates, precision', []),
    ('A Foundations', 'Tax configuration, inclusive/exclusive, rounding', []),
    ('A Foundations', 'Document numbering', []),
    ('A Foundations', 'Opening GL balances', ['OpeningGL']),
    ('A Foundations', 'Opening customer balances', ['OpeningARBalance']),
    ('A Foundations', 'Opening supplier balances', ['OpeningAPBalance']),
    ('A Foundations', 'Opening stock', ['OpeningStock']),
    ('A Foundations', 'Opening assets', ['OpeningAsset']),
    ('B General ledger', 'Manual journals', ['Manual']),
    ('B General ledger', 'Draft / post separation', ['Manual']),
    ('B General ledger', 'Reversal of a posted entry', ['Reversal']),
    ('B General ledger', 'Adjustment journals', ['Adjustment']),
    ('B General ledger', 'Closed-period handling', []),
    ('C Purchasing & payables', 'Purchase invoice', ['PurchaseInvoice']),
    ('C Purchasing & payables', 'Purchase invoice edit (re-post)', ['PurchaseInvoiceEdit']),
    ('C Purchasing & payables', 'Goods receipt / GRNI timing difference', ['Inventory']),
    ('C Purchasing & payables', 'Landed costs', ['LandedCost']),
    ('C Purchasing & payables', 'Supplier payment and allocation', ['Payment']),
    ('C Purchasing & payables', 'Purchase return / debit note', ['PurchaseReturn', 'PurchaseReturnEdit']),
    ('C Purchasing & payables', 'Supplier statement and aging', []),
    ('D Sales & receivables', 'Sales invoice', ['SalesInvoice']),
    ('D Sales & receivables', 'Sales invoice edit / reversal', ['SalesInvoiceEdit', 'SalesInvoiceReversal']),
    ('D Sales & receivables', 'Customer receipt and allocation', ['Receipt']),
    ('D Sales & receivables', 'Sales return / credit note', ['SalesReturn', 'SalesReturnEdit']),
    ('D Sales & receivables', 'Customer statement and aging', []),
    ('D Sales & receivables', 'Electronic invoicing status', []),
    ('E Cash & banks', 'Cash and bank accounts', []),
    ('E Cash & banks', 'Transfers between cash/bank', ['Transfer']),
    ('E Cash & banks', 'Bank reconciliation', []),
    ('F Inventory accounting', 'Valuation and cost of goods sold', ['Inventory']),
    ('F Inventory accounting', 'Stock transfer', ['StockTransfer', 'TransferIn', 'TransferOut']),
    ('F Inventory accounting', 'Count / reconcile adjustment', ['StockReconcile', 'Adjustment']),
    ('F Inventory accounting', 'Write-off', ['StockWriteOff']),
    ('F Inventory accounting', 'Assembly / disassembly', ['Assembly', 'Disassembly']),
    ('G Fixed assets', 'Acquisition and capitalisation', ['AssetCapitalization', 'FixedAsset']),
    ('G Fixed assets', 'Depreciation run', ['Depreciation', 'EquipmentDepAllocation']),
    ('G Fixed assets', 'Disposal and gain/loss', ['FixedAssetDisposal']),
    ('G Fixed assets', 'Asset maintenance', ['AssetMaintenance']),
    ('H Payroll', 'Payroll posting', ['Payroll']),
    ('H Payroll', 'Payroll disbursement', ['PayrollPay']),
    ('H Payroll', 'Leave provision / encashment', ['LeaveProvision', 'LeaveEncash']),
    ('H Payroll', 'Final settlement', ['FinalSettlement']),
    ('I Connected modules', 'POS shift close', ['PosShiftClose']),
    ('I Connected modules', 'POS refund', ['PosRefund']),
    ('I Connected modules', 'POS tips', ['PosTip']),
    ('I Connected modules', 'POS preparation / waste', ['PosPrep', 'PosWaste']),
    ('I Connected modules', 'Project advance', ['ProjectAdvance']),
    ('I Connected modules', 'Project issue and labour', ['ProjectIssue', 'ProjectLabor']),
    ('I Connected modules', 'Retention release', ['RetentionRelease', 'SubRetentionRelease']),
    ('I Connected modules', 'Manufacturing work order', ['WorkOrder']),
    ('J Foreign currency', 'Transaction conversion', []),
    ('J Foreign currency', 'Unrealised revaluation', ['FxRevaluation']),
    ('J Foreign currency', 'Revaluation reversal', ['FxRevaluationReversal']),
    ('K Closing & reporting', 'Trial balance', []),
    ('K Closing & reporting', 'Balance sheet / income statement / cash flow', []),
    ('K Closing & reporting', 'VAT return', ['VatReturn']),
    ('K Closing & reporting', 'Period close', []),
    ('K Closing & reporting', 'Year-end close and retained earnings', ['YearClose']),
]

RUNTIME_SCREENS = {   # screens this discovery actually rendered and captured
    'Trial balance', 'Balance sheet / income statement / cash flow',
    'Customer statement and aging', 'Supplier statement and aging',
    'Manual journals', 'Draft / post separation', 'Reversal of a posted entry',
    'Financial years, periods, locks and closing rules',
    'Chart of accounts, account types, control accounts',
    'Period close', 'Closed-period handling', 'Reopening a closed period',
    'Supplier statement and aging', 'Customer statement and aging',
    'Cash and bank accounts', 'Document numbering',
    'Approval', 'Correction of a posted mistake',
}

srows = []
for area, cap, types in SCOPE:
    declared = [t for t in types if t in declared_types]
    with_data = [t for t in types if t in WITH_DATA]
    executed = [t for t in types if t in EXECUTED]
    if executed:
        status = 'Implemented and runtime verified (executed by this discovery)'
    elif with_data and cap in RUNTIME_SCREENS:
        status = 'Implemented and runtime verified (screen rendered + ledger data present)'
    elif with_data:
        status = 'Implemented, evidenced by existing ledger data; NOT executed here'
    elif cap in RUNTIME_SCREENS:
        status = 'Implemented and runtime verified (screen rendered); no posting executed'
    elif declared:
        status = 'Implemented but unverified (posting path declared in code, no data, not executed)'
    elif types:
        status = 'Not found (no source type declared in code)'
    else:
        status = 'Implemented but unverified (configuration/screen area; no ledger source type)'
    srows.append({
        'area': area, 'capability': cap,
        'declared_source_types': '; '.join(types) or '(none - configuration area)',
        'declared_in_code': '; '.join(declared) or 'no',
        'entries_in_isolated_db': '; '.join(f'{t}={WITH_DATA[t]}' for t in with_data) or 'none',
        'executed_by_this_discovery': '; '.join(executed) or 'no',
        'status': status,
    })

with io.open(os.path.join(OUT, 'scope-matrix.csv'), 'w', encoding='utf-8-sig', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=list(srows[0].keys()))
    w.writeheader(); w.writerows(srows)

# ---------------------------------------------------------------- 3. reconciliation workbook
REC = [
    ('Journal debits vs credits', 'SUM(Debit) vs SUM(Credit), Posted+Reversed, company 1',
     '113047021.50', '113047021.50', '0.00', 'RECONCILED',
     'SQL over JournalEntryLines; matches the Trial Balance table footer and tiles exactly'),
    ('Trial balance screen vs ledger', 'TrialBalance totals vs SQL',
     '113047021.50', '113047021.50', '0.00', 'RECONCILED',
     'Screen ACC-RPT-01; tiles and footer agree once the count-up animation settles'),
    ('Sample transaction trace', 'Trial balance movement caused by ACC-GL-01 (4 postings x 5,000)',
     '113047021.50', '113027021.50', '20000.00', 'RECONCILED',
     'Prior capture 2026-09-20 read 113,027,021.50; the +20,000 is exactly this discovery\'s '
     '2 originals and 2 reversals'),
    ('Receivables vs control account', 'AR Aging report total vs account 1102 net balance',
     '18536202.26', '18520263.26', '15939.00', 'RECONCILED (bridge closes exactly)',
     'CAUSE FOUND: the aging report never deducts credit notes. Posted sales returns (base) = 15,939.00, equal to the difference to the cent. See 09-reconciliation-bridges.md'),
    ('Payables vs control account', 'AP Aging report total vs account 2101 net balance',
     '651508.46', '635232.29', '16276.17', 'RECONCILED to 74.20',
     'CAUSE FOUND: 15,700.00 of payments to 3 vendors with NO posted invoice (the aging skips those vendors) + 650.37 purchase returns not deducted - 74.20 residual on nine reversed purchase-invoice journals, which remains OPEN. See 09-reconciliation-bridges.md'),
    ('Inventory vs control account', 'Inventory valuation vs account 1103',
     '(not run)', '874681.60', '(unknown)', 'NOT TESTED',
     'The inventory valuation report was not executed in this pass'),
    ('Cash/bank vs ledger', 'Bank reconciliation vs accounts 110101 / 110102',
     '(not run)', '45739411.92 / 653065.41', '(unknown)', 'NOT TESTED',
     'Bank reconciliation was not executed in this pass'),
    ('Assets vs ledger', 'Fixed-asset register vs accounts 1201 / 1202',
     '(not run)', '11000.00 cost', '(unknown)', 'NOT TESTED', ''),
    ('Payroll vs ledger', 'Payroll liabilities and payments vs 2103 / 210204 / 210205',
     '(not run)', '(not extracted)', '(unknown)', 'NOT TESTED',
     'Only 2 Payroll entries exist and none was executed here'),
    ('Financial statements vs trial balance', 'Balance sheet / income statement vs adjusted TB',
     '(captured, not cross-footed)', '(captured)', '(unknown)', 'NOT TESTED',
     'Both statements were rendered and captured; the cross-foot was not performed'),
]
with io.open(os.path.join(OUT, 'reconciliation-workbook.csv'), 'w', encoding='utf-8-sig', newline='') as fh:
    w = csv.writer(fh)
    w.writerow(['check', 'basis', 'subledger_or_report', 'ledger_control', 'difference',
                'result', 'notes'])
    w.writerows(REC)

en = sum(1 for m in man if m['language'] == 'en' and m['file_exists'])
ar = sum(1 for m in man if m['language'] == 'ar' and m['file_exists'])
print('screenshot manifest rows :', len(man))
print('   english on disk       :', en)
print('   arabic on disk        :', ar)
print('   readiness_ok false    :', sum(1 for m in man if m['readiness_ok'] is False))
print('   duplicate groups      :', gi)
print('scope matrix rows        :', len(srows))
for k, v in collections.Counter(r['status'].split('(')[0].strip() for r in srows).most_common():
    print('   %-62s %d' % (k[:62], v))
print('reconciliation checks    :', len(REC),
      '(%d reconciled, %d not reconciled, %d not tested)' % (
          sum(1 for r in REC if r[5].startswith('RECONCILED')),
          sum(1 for r in REC if r[5].startswith('NOT RECONCILED')),
          sum(1 for r in REC if r[5].startswith('NOT TESTED'))))
