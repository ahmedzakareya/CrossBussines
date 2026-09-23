# -*- coding: utf-8 -*-
"""Derive the posting matrix from the actual posting logic.

READ-ONLY over the repository. Finds every place a journal entry is constructed, the account
each line uses, and whether that account is CONFIGURABLE (read from settings or a mapping) or
HARD-CODED (a literal account code in the source). That distinction is one the brief asks for
explicitly and it cannot be answered from a screen.

Writes reference/posting-rules-raw.json and posting-matrix.csv.
"""
import io, os, re, json, csv, collections

ROOT = r'C:\CrossBuy\CrossBuy'
APP = os.path.join(ROOT, 'CrossBuy')
BL = os.path.join(APP, 'BL')
OUT = os.path.join(ROOT, 'docs', 'accounting-cycle-discovery')


def read(p):
    try:
        return io.open(p, encoding='utf-8', errors='replace').read()
    except OSError:
        return ''


cs_files = []
for dp, _dn, fns in os.walk(BL):
    for f in fns:
        if f.endswith('.cs'):
            cs_files.append(os.path.join(dp, f))

# ---------------------------------------------------------------- 1. who creates journal entries
CREATORS = collections.defaultdict(list)
CREATE_RX = re.compile(r'(CreateAndPostAsync|CreateAsync|PostAsync)\s*\(', re.I)
for p in cs_files:
    t = read(p)
    if 'JournalEntryInput' not in t and '_journals' not in t:
        continue
    rel = os.path.relpath(p, ROOT).replace('\\', '/')
    for m in re.finditer(r'_journals\.(\w+)\s*\(', t):
        line = t.count('\n', 0, m.start()) + 1
        CREATORS[rel].append({'call': m.group(1), 'line': line})

# ---------------------------------------------------------------- 2. account resolution
# A literal code in the source is HARD-CODED; a lookup through settings/mapping is CONFIGURABLE.
HARD_RX = re.compile(r'"(\d{4,8})"')                       # "510101"
CODE_START_RX = re.compile(r'Code\s*(?:==|\.StartsWith\()\s*"(\d{3,8})"')
SETTING_RX = re.compile(r'(AccountingSettings|GetSettingsAsync|MappingAsync|AccountMap|'
                        r'DefaultAccountId|\w+AccountId)\b')

accounts = collections.defaultdict(lambda: {'hardcoded': [], 'configurable': []})
for p in cs_files:
    t = read(p)
    rel = os.path.relpath(p, ROOT).replace('\\', '/')
    if not re.search(r'JournalEntryInput|JournalEntryLine|_journals', t):
        continue
    for m in CODE_START_RX.finditer(t):
        accounts[rel]['hardcoded'].append({
            'code': m.group(1), 'line': t.count('\n', 0, m.start()) + 1,
            'snippet': ' '.join(t[max(0, m.start() - 90):m.end() + 40].split())[-140:],
        })
    for m in SETTING_RX.finditer(t):
        accounts[rel]['configurable'].append({
            'via': m.group(1), 'line': t.count('\n', 0, m.start()) + 1,
        })

# ---------------------------------------------------------------- 3. duplicate-posting protection
DUP_RX = re.compile(r'(SourceType\s*==\s*"?\w+"?\s*&&\s*\w*SourceId|AlreadyPosted|'
                    r'AnyAsync\([^)]*SourceType|IsPosted|Status\s*==\s*"Posted")')
dup = []
for p in cs_files:
    t = read(p)
    rel = os.path.relpath(p, ROOT).replace('\\', '/')
    for m in DUP_RX.finditer(t):
        dup.append({'file': rel, 'line': t.count('\n', 0, m.start()) + 1,
                    'snippet': ' '.join(t[max(0, m.start() - 100):m.end() + 60].split())[-180:]})

# ---------------------------------------------------------------- 4. transactions / rollback
tx = []
for p in cs_files:
    t = read(p)
    rel = os.path.relpath(p, ROOT).replace('\\', '/')
    for m in re.finditer(r'BeginTransactionAsync|CreateExecutionStrategy|SaveChangesAsync\(\)\s*;\s*\n\s*await\s+\w*tx', t):
        tx.append({'file': rel, 'line': t.count('\n', 0, m.start()) + 1, 'what': m.group(0)[:40]})

# ---------------------------------------------------------------- 5. the source types in the ledger
posting_service = read(os.path.join(BL, 'JournalEntryService.cs'))
source_types = sorted(set(re.findall(r'SourceType\s*=\s*"(\w+)"',
                                     '\n'.join(read(p) for p in cs_files))))

raw = {
    'journal_creators': {k: v for k, v in sorted(CREATORS.items()) if v},
    'creator_count': len([k for k, v in CREATORS.items() if v]),
    'account_resolution': {k: v for k, v in sorted(accounts.items())
                           if v['hardcoded'] or v['configurable']},
    'duplicate_protection_sites': dup[:60],
    'transaction_sites': tx[:60],
    'ledger_source_types': source_types,
}
io.open(os.path.join(OUT, 'reference', 'posting-rules-raw.json'), 'w', encoding='utf-8').write(
    json.dumps(raw, ensure_ascii=False, indent=1))

# ---------------------------------------------------------------- 6. the matrix
hard_total = sum(len(v['hardcoded']) for v in accounts.values())
conf_total = sum(len(v['configurable']) for v in accounts.values())

rows = []
for rel, v in sorted(accounts.items()):
    svc = os.path.basename(rel).replace('.cs', '')
    for h in v['hardcoded']:
        rows.append({
            'service': svc, 'source_file': rel, 'line': h['line'],
            'account_code': h['code'], 'resolution': 'HARD-CODED in source',
            'evidence': 'repository evidence', 'snippet': h['snippet'],
        })
by_service = collections.Counter(r['service'] for r in rows)

with io.open(os.path.join(OUT, 'posting-account-resolution.csv'), 'w',
             encoding='utf-8-sig', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=['service', 'source_file', 'line', 'account_code',
                                       'resolution', 'evidence', 'snippet'])
    w.writeheader()
    w.writerows(rows)

print('services that create journal entries :', raw['creator_count'])
print('ledger SourceType values             :', len(source_types))
print('   ', ', '.join(source_types))
print('hard-coded account-code sites        :', hard_total)
print('configurable-lookup sites            :', conf_total)
print('duplicate-protection sites           :', len(dup))
print('explicit transaction sites           :', len(tx))
print()
print('hard-coded codes by service:')
for s, n in by_service.most_common(12):
    print('   %-34s %d' % (s, n))
