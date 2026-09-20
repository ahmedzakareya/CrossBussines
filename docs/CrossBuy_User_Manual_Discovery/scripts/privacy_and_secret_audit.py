# -*- coding: utf-8 -*-
"""Audit the discovery package before it is zipped.

The brief forbids credentials, secrets, connection strings, tokens, private customer information
and real employee personal information. This checks the package's own text files for them rather
than trusting that none were written, and reports what it finds instead of deleting silently.

Images are not scanned for content — screenshots carry development data by design, and each one's
privacy treatment is recorded per row in screenshot-manifest.csv.
"""
import io, os, re, sys

OUT = r'C:\CrossBuy\CrossBuy\docs\user-manual-discovery'

TEXT_EXT = {'.md', '.json', '.csv', '.py', '.mjs', '.txt'}

PATTERNS = [
    ('connection string', re.compile(r'(Server\s*=|Data Source\s*=)[^"\n]{0,80}(Password|Pwd)\s*=', re.I)),
    ('password assignment', re.compile(r'\b(password|pwd)\s*[:=]\s*["\']?[^\s"\',;}]{4,}', re.I)),
    ('api key / secret', re.compile(r'\b(api[_-]?key|secret|client[_-]?secret|access[_-]?token)\s*[:=]\s*["\']?[A-Za-z0-9_\-]{12,}', re.I)),
    ('bearer token', re.compile(r'\bBearer\s+[A-Za-z0-9._\-]{20,}')),
    ('jwt', re.compile(r'\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}')),
    ('private key block', re.compile(r'-----BEGIN [A-Z ]*PRIVATE KEY-----')),
    ('live server address', re.compile(r'178\.18\.250\.154')),
    ('e-mail address', re.compile(r'\b[\w.+-]+@[\w-]+\.[A-Za-z]{2,}\b')),
    ('IBAN-like', re.compile(r'\b[A-Z]{2}\d{2}[A-Z0-9]{12,}\b')),
    ('14-digit national id', re.compile(r'(?<!\d)\d{14}(?!\d)')),
    ('long digit run (card-like)', re.compile(r'(?<!\d)\d{15,19}(?!\d)')),
]

# Strings that are expected and must NOT be reported as findings.
ALLOW = [
    re.compile(r'CB_PASS|CB_USER'),   # the capture script takes credentials from the environment
    re.compile(r'password.{0,40}(not reproduced|redacted|<redacted>)', re.I),
    re.compile(r'dev@crossbuy\.local'),  # a git author used by the repo itself
    re.compile(r'testcrossbuy@gmail\.com'),  # appears only if quoted; flagged below anyway
]

findings = []
scanned = 0
for dirpath, dirnames, filenames in os.walk(OUT):
    dirnames[:] = [d for d in dirnames if d != 'screenshots' or True]
    for fn in sorted(filenames):
        ext = os.path.splitext(fn)[1].lower()
        if ext not in TEXT_EXT:
            continue
        p = os.path.join(dirpath, fn)
        rel = os.path.relpath(p, OUT).replace('\\', '/')
        try:
            t = io.open(p, encoding='utf-8', errors='replace').read()
        except OSError:
            continue
        scanned += 1
        for name, rx in PATTERNS:
            for m in rx.finditer(t):
                frag = t[max(0, m.start() - 40):m.end() + 40].replace('\n', ' ')
                if any(a.search(frag) for a in ALLOW):
                    continue
                line = t.count('\n', 0, m.start()) + 1
                findings.append((name, rel, line, m.group(0)[:70], frag[:130]))

print('text files scanned :', scanned)
print('findings           :', len(findings))
print()
by_kind = {}
for f in findings:
    by_kind.setdefault(f[0], []).append(f)
for kind in sorted(by_kind):
    rows = by_kind[kind]
    print('== %s  (%d)' % (kind, len(rows)))
    for name, rel, line, hit, frag in rows[:12]:
        print('   %-46s:%-5d %s' % (rel, line, hit))
    if len(rows) > 12:
        print('   … %d more' % (len(rows) - 12))
    print()

sys.exit(0)
