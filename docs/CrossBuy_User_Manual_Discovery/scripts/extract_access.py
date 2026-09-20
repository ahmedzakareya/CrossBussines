# -*- coding: utf-8 -*-
"""Roles, permissions and route protection, for the manual's access chapter.

READ-ONLY. Reads the module access services, the platform vocabularies and the controller
attributes, and writes:

    role-permission-matrix.csv   every declared role and action, per module scope
    route-protection.csv         every routable action with the attributes that guard it

WHAT THIS CANNOT DO, and it matters for the manual: a declared role is not an observed one.
Nothing here proves what a user holding a role actually sees. The discovery ran on a single
administrator session, so every row is REPOSITORY-VERIFIED and none is RUNTIME-VERIFIED.
"""
import io, os, re, csv, json, collections

ROOT = r'C:\CrossBuy\CrossBuy'
APP = os.path.join(ROOT, 'CrossBuy')
BL = os.path.join(APP, 'BL')
CTRL = os.path.join(APP, 'Controllers')
OUT = os.path.join(ROOT, 'docs', 'user-manual-discovery')


def read(p):
    try:
        return io.open(p, encoding='utf-8', errors='replace').read()
    except OSError:
        return ''


def iter_cs(root):
    for dirpath, _d, filenames in os.walk(root):
        for fn in sorted(filenames):
            if fn.endswith('.cs'):
                yield os.path.join(dirpath, fn)


# ---------------------------------------------------------------- 1. role / action vocabularies
CLASS_RX = re.compile(
    r'(?:(?P<doc>(?:\s*///[^\n]*\n)+))?\s*public\s+static\s+class\s+(?P<name>\w*(?:Roles|Actions))\b(?P<body>.*?)\n\s*\}',
    re.S)
CONST_RX = re.compile(
    r'(?:(?P<comment>(?:[ \t]*//[^\n]*\n)+))?[ \t]*public\s+const\s+string\s+(?P<field>\w+)\s*=\s*"(?P<value>[^"]*)"\s*;')

vocab = collections.defaultdict(list)
for p in iter_cs(BL):
    t = read(p)
    rel = os.path.relpath(p, ROOT).replace('\\', '/')
    for cm in CLASS_RX.finditer(t):
        cname, body = cm.group('name'), cm.group('body')
        module = re.sub(r'(Roles|Actions)$', '', cname)
        kind = 'role' if cname.endswith('Roles') else 'action'
        for m in CONST_RX.finditer(body):
            note = ' '.join(l.strip().lstrip('/ ').strip()
                            for l in (m.group('comment') or '').split('\n') if l.strip())
            vocab[(module, kind)].append({
                'module': module, 'kind': kind, 'field': m.group('field'),
                'value': m.group('value'), 'note': note[:300], 'source': rel,
            })

# ---- vocabularies that exist ONLY as an interface comment -------------------------------------
# Inventory, CRM and Accounting do not publish their action words as constants. They are written on
# the interface instead:
#     Task<bool> CanAsync(string action);   // read | doc | purchase | manage
# A manual still has to name those words, and a reader still has to be told that they are documented
# in a comment rather than in a typed vocabulary — a rename cannot be caught by the compiler here.
COMMENT_VOCAB_RX = re.compile(
    r'Task<bool>\s+CanAsync\(string\s+action\)\s*;\s*//\s*([^\n]+)')
for p in iter_cs(BL):
    t = read(p)
    rel = os.path.relpath(p, ROOT).replace('\\', '/')
    m = COMMENT_VOCAB_RX.search(t)
    if not m:
        continue
    module = re.sub(r'AccessService\.cs$', '', os.path.basename(p))
    words = [w.strip() for w in m.group(1).split('|') if w.strip()]
    if len(words) < 2:
        continue
    for w in words:
        if not re.fullmatch(r'[a-z][a-z\-]*', w):
            continue
        vocab[(module, 'action')].append({
            'module': module, 'kind': 'action', 'field': '(comment only)',
            'value': w, 'note': 'declared in an interface comment, not as a typed constant',
            'source': rel,
        })


# ---------------------------------------------------------------- 2. scopes the kernel knows
reg = read(os.path.join(BL, 'Platform', 'EntityRegistry.cs'))
scopes = sorted(set(re.findall(r'public const string Scope(\w+)\s*=\s*"([^"]+)"', reg)))

# ---------------------------------------------------------------- 3. permission checks in code
checks = collections.Counter()
for p in list(iter_cs(BL)) + list(iter_cs(CTRL)):
    t = read(p)
    for m in re.finditer(r'CanAsync\(\s*"([^"]+)"', t):
        checks[m.group(1)] += 1

# view-level checks too
for dirpath, _d, filenames in os.walk(os.path.join(APP, 'Views')):
    for fn in filenames:
        if fn.endswith('.cshtml'):
            for m in re.finditer(r'CanAsync\(\s*"([^"]+)"', read(os.path.join(dirpath, fn))):
                checks[m.group(1)] += 1

# ---------------------------------------------------------------- 4. route protection
DECL_RX = re.compile(
    r'public\s+(?:static\s+)?(?:async\s+)?(?:Task<)?[A-Za-z<>\[\]?]*\s+(\w+)\s*\(')

rows = []
for base, is_api in ((CTRL, False), (os.path.join(CTRL, 'Api'), True)):
    if not os.path.isdir(base):
        continue
    for fn in sorted(os.listdir(base)):
        p = os.path.join(base, fn)
        if not (fn.endswith('.cs') and os.path.isfile(p)):
            continue
        name = fn[:-3].split('.')[0]
        if name.endswith('Controller'):
            name = name[:-len('Controller')]
        t = read(p)
        cm = re.search(
            r'((?:\[[^\]]+\]\s*)+)public\s+(?:sealed\s+|partial\s+|abstract\s+)*class\s+\w*Controller', t)
        class_attrs = sorted(set(a.strip() for a in re.findall(r'\[([^\]]+)\]', cm.group(1)))) if cm else []
        pending = []
        for line in t.split('\n'):
            s = line.strip()
            inline = re.match(r'((?:\[[^\]]*\]\s*)+)(.*)$', s)
            if inline:
                pending.extend(a.strip() for a in re.findall(r'\[([^\]]+)\]', inline.group(1)))
                s = inline.group(2).strip()
                if not s:
                    continue
            m = DECL_RX.search(s)
            if m and ('ActionResult' in s or 'Task<' in s):
                verbs = [a for a in pending if a.startswith('Http')] or ['HttpGet (implicit)']
                attrs = [a for a in pending if not a.startswith('Http')]
                guards = sorted(set(attrs + class_attrs))
                anon = any('AllowAnonymous' in g for g in guards)
                rows.append({
                    'controller': name,
                    'action': m.group(1),
                    'route': '/%s/%s' % (name, m.group(1)),
                    'kind': 'api' if is_api else 'mvc',
                    'verbs': '; '.join(verbs),
                    'action_attributes': '; '.join(attrs),
                    'controller_attributes': '; '.join(class_attrs),
                    'anonymous': 'yes' if anon else 'no',
                    'evidence': 'repository-verified',
                })
                pending = []
            elif s and not s.startswith('//'):
                pending = []

# ---------------------------------------------------------------- outputs
with io.open(os.path.join(OUT, 'role-permission-matrix.csv'), 'w', encoding='utf-8-sig', newline='') as fh:
    w = csv.writer(fh)
    w.writerow(['module_scope', 'kind', 'constant', 'value', 'checked_in_code_times',
                'notes_from_source', 'source_file', 'evidence_level'])
    for (module, kind) in sorted(vocab):
        for e in vocab[(module, kind)]:
            w.writerow([e['module'], e['kind'], e['field'], e['value'],
                        checks.get(e['value'], 0), e['note'], e['source'],
                        'repository-verified'])

with io.open(os.path.join(OUT, 'route-protection.csv'), 'w', encoding='utf-8-sig', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=['controller', 'action', 'route', 'kind', 'verbs',
                                       'action_attributes', 'controller_attributes',
                                       'anonymous', 'evidence'])
    w.writeheader()
    w.writerows(sorted(rows, key=lambda r: (r['kind'], r['controller'], r['action'])))

summary = {
    'platform_scopes': [{'constant': 'Scope' + a, 'value': b} for a, b in scopes],
    'vocabularies': {'%s.%s' % (m, k): len(v) for (m, k), v in sorted(vocab.items())},
    'distinct_permission_strings_checked': len(checks),
    'most_checked': checks.most_common(25),
    'routable_actions': len(rows),
    'anonymous_actions': sum(1 for r in rows if r['anonymous'] == 'yes'),
    'caveat': ('Every row is repository-verified only. No role other than the administrator account '
               'was exercised at runtime, so no row here is runtime-verified.'),
}
io.open(os.path.join(OUT, 'reference', 'access-summary.json'), 'w', encoding='utf-8').write(
    json.dumps(summary, ensure_ascii=False, indent=1))

print('platform scopes           :', len(scopes))
for (m, k), v in sorted(vocab.items()):
    print('   %-18s %-7s %d' % (m, k, len(v)))
print('distinct permission words checked in code :', len(checks))
print('routable actions          :', len(rows), '(%d anonymous)' % summary['anonymous_actions'])
