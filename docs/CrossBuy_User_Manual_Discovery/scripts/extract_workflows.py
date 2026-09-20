# -*- coding: utf-8 -*-
"""Workflow skeletons, grounded in what the code actually offers.

READ-ONLY. A workflow in this product is a sequence of screens, each of which shows a form (GET)
and accepts it (POST), moving a document between named statuses and emitting platform events.
This script does not invent the sequences — it extracts the raw material a writer needs and cannot
safely guess at:

  * every GET/POST action pair, which is what makes a screen a step rather than a view
  * the status vocabulary each module assigns and compares against
  * the platform events each document type emits
  * the approval silos that can interrupt a sequence

Writes workflow-catalog.json. The `workflows` array is authored from this material in the
documents; the `raw` object below is the evidence it rests on.
"""
import io, os, re, json, collections

ROOT = r'C:\CrossBuy\CrossBuy'
APP = os.path.join(ROOT, 'CrossBuy')
CTRL = os.path.join(APP, 'Controllers')
BL = os.path.join(APP, 'BL')
OUT = os.path.join(ROOT, 'docs', 'user-manual-discovery')

DECL_RX = re.compile(r'public\s+(?:static\s+)?(?:async\s+)?(?:Task<)?[A-Za-z<>\[\]?]*\s+(\w+)\s*\(')


def read(p):
    try:
        return io.open(p, encoding='utf-8', errors='replace').read()
    except OSError:
        return ''


# ---------------------------------------------------------------- 1. GET / POST action pairs
by_ctrl = collections.defaultdict(lambda: {'get': set(), 'post': set()})
for fn in sorted(os.listdir(CTRL)):
    p = os.path.join(CTRL, fn)
    if not (fn.endswith('.cs') and os.path.isfile(p)):
        continue
    name = fn[:-3].split('.')[0]
    if name.endswith('Controller'):
        name = name[:-len('Controller')]
    pending = []
    for line in read(p).split('\n'):
        s = line.strip()
        inline = re.match(r'((?:\[[^\]]*\]\s*)+)(.*)$', s)
        if inline:
            pending.extend(a.strip() for a in re.findall(r'\[([^\]]+)\]', inline.group(1)))
            s = inline.group(2).strip()
            if not s:
                continue
        m = DECL_RX.search(s)
        if m and ('ActionResult' in s or 'Task<' in s):
            verb = 'post' if any(a.startswith('HttpPost') for a in pending) else 'get'
            by_ctrl[name][verb].add(m.group(1))
            pending = []
        elif s and not s.startswith('//'):
            pending = []

pairs = {}
for ctrl, v in sorted(by_ctrl.items()):
    both = sorted(v['get'] & v['post'])
    post_only = sorted(v['post'] - v['get'])
    pairs[ctrl] = {
        'form_then_save': both,          # the classic step: show the form, then accept it
        'command_only': post_only,       # an action with no form — a button on another screen
        'get_count': len(v['get']), 'post_count': len(v['post']),
    }

# ---------------------------------------------------------------- 2. status vocabulary
assigned = collections.Counter()
compared = collections.Counter()
for base in (BL, CTRL):
    for dirpath, _d, filenames in os.walk(base):
        for fn in filenames:
            if not fn.endswith('.cs'):
                continue
            t = read(os.path.join(dirpath, fn))
            for m in re.finditer(r'\.(?:Status|State)\s*=\s*"([A-Za-z]+)"', t):
                assigned[m.group(1)] += 1
            for m in re.finditer(r'(?:Status|State)\s*==\s*"([A-Za-z]+)"', t):
                compared[m.group(1)] += 1

statuses = sorted(set(assigned) | set(compared))

# ---------------------------------------------------------------- 3. events per document family
events = sorted(set(re.findall(r'"([A-Za-z]+\.[A-Za-z]+)"',
                               read(os.path.join(BL, 'Platform', 'BusinessEventTypes.cs')))))
families = collections.defaultdict(list)
for e in events:
    fam, ev = e.split('.', 1)
    families[fam].append(ev)

# ---------------------------------------------------------------- 4. approval silos
silo_src = read(os.path.join(BL, 'Approvals', 'ApprovalReadContracts.cs'))
silos = [{'constant': a, 'value': b}
         for a, b in re.findall(r'public const string (\w+)\s*=\s*"([^"]+)"', silo_src)]

# ---------------------------------------------------------------- 5. posting services
posting = sorted({
    fn for fn in os.listdir(BL)
    if fn.endswith('.cs') and re.search(r'IJournalEntryService|PostJournal|CreateJournalAsync',
                                        read(os.path.join(BL, fn)))
})

raw = {
    'action_pairs_by_controller': pairs,
    'status_vocabulary': {
        'all': statuses,
        'assigned_in_code': assigned.most_common(),
        'compared_in_code': compared.most_common(),
        'note': ('Status is a STRING COLUMN, not an enum, in most of this product. Both "Void" and '
                 '"Voided" are assigned, which a manual should not present as one status.'),
    },
    'event_families': {k: sorted(v) for k, v in sorted(families.items())},
    'event_total': len(events),
    'approval_silos': silos,
    'ledger_posting_services': posting,
}

io.open(os.path.join(OUT, 'reference', 'workflow-raw-evidence.json'), 'w', encoding='utf-8').write(
    json.dumps(raw, ensure_ascii=False, indent=1))

print('controllers with form+save pairs :',
      sum(1 for v in pairs.values() if v['form_then_save']))
print('form-then-save actions           :', sum(len(v['form_then_save']) for v in pairs.values()))
print('command-only POST actions        :', sum(len(v['command_only']) for v in pairs.values()))
print('distinct status values           :', len(statuses))
print('event families                   :', len(families), '/', len(events), 'events')
print('approval silos                   :', len(silos))
print('services that post to the ledger :', len(posting))
print()
print('top controllers by form+save pairs:')
for c, v in sorted(pairs.items(), key=lambda kv: -len(kv[1]['form_then_save']))[:10]:
    print('   %-16s %d pairs, %d command-only' % (c, len(v['form_then_save']), len(v['command_only'])))
