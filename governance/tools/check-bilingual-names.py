"""
R-i18n — every name/caption is captured in BOTH languages and rendered in the reader's.

WHY THIS EXISTS: making English the default culture turned a class of silent data gaps into
visible defects. A screen that read `entity.Name` directly showed Arabic to an English reader; a
screen that read `entity.NameEn` with no fallback showed a BLANK where the English name had never
been filled in. Neither is visible in a build, and neither is visible in Arabic — which is why
they survived. This script is the check that keeps them from coming back.

WHAT IT CHECKS

  1. PAIRS          every bilingual pair declared in Models/Context
  2. INPUT          a pair whose Arabic side is editable on a form but whose English side is not
  3. DISPLAY        a service that copies the Arabic column into a DTO without choosing a language
                    and without passing the English twin along for the view to choose

WHAT IT DELIBERATELY DOES NOT FLAG

  A SNAPSHOT column. `PosOrderLine.ItemName`, `Payslip.EmployeeName`, `FinalSettlement.EmployeeName`
  and the `ItemDescription` copied from one document line to the next record what a document SAID
  when it was issued. They must not vary by UI language — the same invoice would otherwise read
  differently for two clerks. Add a snapshot target to SNAPSHOT_TARGETS rather than "fixing" it.

  Category 3 is ADVISORY, not a gate: distinguishing a display DTO from a snapshot needs judgement,
  so this prints a review list and exits 0 unless --strict is passed.

USAGE
    python governance/tools/check-bilingual-names.py
    python governance/tools/check-bilingual-names.py --strict     # fail on a missing INPUT
"""
import os, re, sys, collections

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..'))
APP = os.path.join(REPO, 'CrossBuy')
MODELS = os.path.join(APP, 'Models', 'Context')

# a column that records what a document said, frozen on purpose
SNAPSHOT_TARGETS = {
    'ItemName', 'ItemDescription', 'EmployeeName', 'CustomerNameOverride',
    'MemberName', 'GuestName', 'WorkerName', 'FileName', 'AttachName',
}

PROP = re.compile(r'public\s+(?:virtual\s+)?(string\??)\s+(\w+)\s*\{\s*get')
CLS = re.compile(r'public\s+class\s+(\w+)')
BIND = re.compile(r'(?:name|asp-for|id)\s*=\s*"([^"]{1,80})"')
ASSIGN = re.compile(r'\b(\w+)\s*=\s*([A-Za-z_]\w*(?:\.\w+)*)\.(\w+)\s*[,;\)]')
SAFE = re.compile(
    r'DisplayName\.|EmployeeNames\.|\bisAr\b|\bIsAr\b|\barabic\b|\bArabic\b|\bAr\(\)'
    r'|CurrentUICulture|\bPick\(|\bT\s*\(')


def read(p):
    return open(p, encoding='utf-8-sig').read()


def find_pairs():
    out = []
    for dp, dn, fn in os.walk(MODELS):
        dn[:] = [d for d in dn if d not in ('bin', 'obj')]
        for f in fn:
            if not f.endswith('.cs'):
                continue
            cls, props, order = None, [], []
            for ln in read(os.path.join(dp, f)).split('\n'):
                m = CLS.search(ln)
                if m:
                    if cls:
                        order.append((cls, props))
                    cls, props = m.group(1), []
                m = PROP.search(ln)
                if m and cls:
                    props.append(m.group(2))
            if cls:
                order.append((cls, props))
            for cls, props in order:
                s = set(props)
                for n in props:
                    if n.endswith(('En', 'EN')):
                        continue
                    if n + 'En' in s:
                        out.append((cls, n, n + 'En'))
                    elif n.endswith('Ar') and n[:-2] + 'En' in s:
                        out.append((cls, n, n[:-2] + 'En'))
    return out


def bound_identifiers():
    bound = collections.defaultdict(set)
    for dp, dn, fn in os.walk(os.path.join(APP, 'Views')):
        for f in fn:
            if not f.endswith('.cshtml'):
                continue
            rel = os.path.relpath(os.path.join(dp, f), APP)
            for m in BIND.finditer(read(os.path.join(dp, f))):
                bound[m.group(1).split('.')[-1].lower()].add(rel)
    return bound


def main():
    strict = '--strict' in sys.argv
    pairs = find_pairs()
    bound = bound_identifiers()

    missing_input = []
    for cls, ar, en in pairs:
        if bound.get(ar.lower()) and not bound.get(en.lower()):
            missing_input.append((cls, ar, en, sorted(bound[ar.lower()])[:3]))

    twin = {}
    for cls, ar, en in pairs:
        twin.setdefault(ar, set()).add(en)
    twin = {a: sorted(e)[0] for a, e in twin.items() if len(e) == 1}

    blind = collections.defaultdict(list)
    for sub in ('BL', 'Controllers'):
        for dp, dn, fn in os.walk(os.path.join(APP, sub)):
            dn[:] = [d for d in dn if d not in ('bin', 'obj')]
            for f in fn:
                if not f.endswith('.cs') or f == 'DevSeedController.cs':
                    continue
                p = os.path.join(dp, f)
                rel = os.path.relpath(p, APP)
                lines = read(p).split('\n')
                for i, ln in enumerate(lines):
                    if ln.strip().startswith('//') or SAFE.search(ln):
                        continue
                    for m in ASSIGN.finditer(ln):
                        lhs, recv, prop = m.groups()
                        if prop not in twin or lhs.endswith(('Ar', 'AR')):
                            continue
                        if lhs in SNAPSHOT_TARGETS:
                            continue
                        block = '\n'.join(lines[max(0, i - 12): i + 13])
                        if twin[prop] in block or SAFE.search(block):
                            continue
                        blind[rel].append((i + 1, f'{lhs} = {recv}.{prop}', twin[prop]))

    print(f'[1] bilingual pairs declared            : {len(pairs)}')
    print(f'[2] pairs missing an ENGLISH INPUT      : {len(missing_input)}')
    for cls, ar, en, vs in sorted(missing_input):
        print(f'      {cls}.{ar} -> no editable {en}')
        for v in vs:
            print(f'          {v}')
    n = sum(len(v) for v in blind.values())
    print(f'[3] language-blind DTO copies (review)  : {n} in {len(blind)} files')
    for rel, v in sorted(blind.items(), key=lambda kv: -len(kv[1])):
        print(f'      {len(v):3d}  {rel}')
        for i, expr, en in v[:3]:
            print(f'             {i}: {expr}   (twin {en})')

    if strict and missing_input:
        print('\nFAIL: a name is capturable in Arabic but not in English.')
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
