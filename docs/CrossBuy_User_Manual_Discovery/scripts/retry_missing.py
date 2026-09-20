# -*- coding: utf-8 -*-
"""Work out which routes still have no screenshot, and write a retry list.

The capture runs against an application instance that has been up for hours and holds well over a
gigabyte. Its session store is in-memory, so under that pressure a session is evicted mid-run and
SessionValidation bounces the next navigation to the sign-in page. Short slices with a fresh
sign-in each survive it; long ones do not. A second, dedicated instance could not be started
because the running one holds the build output (MSB3027), so the answer is to retry the misses in
small batches rather than to fight the memory.
"""
import io, os, json, sys, re

ROOT = r'C:\CrossBuy\CrossBuy'
OUT = os.path.join(ROOT, 'docs', 'user-manual-discovery')
SHOTS = os.path.join(OUT, 'screenshots')
SCRATCH = (r'C:\Users\Lenovo\AppData\Local\Temp\claude\c--CrossBuy'
           r'\7d5e59b2-fa47-4fc5-945e-07614ef3105c\scratchpad')

lang = sys.argv[1] if len(sys.argv) > 1 else 'en'
routes = json.load(io.open(os.path.join(SCRATCH, 'routes.json'), encoding='utf-8'))


def slug(r):
    return re.sub(r'[^A-Za-z0-9]+', '-', r.lstrip('/')).lower()


have = {f for f in os.listdir(SHOTS) if f.endswith('.%s.png' % lang)}
missing = [r for r in routes if '%s.%s.png' % (slug(r), lang) not in have]

io.open(os.path.join(SCRATCH, 'routes.json.retry'), 'w', encoding='utf-8').write(
    json.dumps(missing, ensure_ascii=False))

print('language          :', lang)
print('routes total      :', len(routes))
print('already captured  :', len(routes) - len(missing))
print('still missing     :', len(missing))
print('retry list written:', os.path.join(SCRATCH, 'routes.json.retry'))
