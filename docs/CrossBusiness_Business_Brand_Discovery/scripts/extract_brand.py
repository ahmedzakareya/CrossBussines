# -*- coding: utf-8 -*-
"""Extract the brand token layer and the asset inventory into the discovery package.

Evidence only: reads the repository, writes brand-tokens.json and asset-manifest.csv.
Nothing in the application is touched.
"""
import io, os, re, json, csv, hashlib, collections

ROOT = r'C:\CrossBuy\CrossBuy'
APP = os.path.join(ROOT, 'CrossBuy')
OUT = os.path.join(ROOT, 'docs', 'business-brand-discovery')
WWW = os.path.join(APP, 'wwwroot')

# ---------------------------------------------------------------- 1. tokens
brand_css = os.path.join(WWW, 'Backend-assets', 'css', 'crossbuy-brand.css')
src = io.open(brand_css, encoding='utf-8', errors='replace').read()

# Every custom property declaration, with the selector block it was declared in, so a dark-theme
# override is never reported as if it were the light value.
tokens = collections.OrderedDict()
pos = 0
block_re = re.compile(r'([^{}]+)\{([^{}]*)\}', re.S)
for m in block_re.finditer(src):
    selector = ' '.join(m.group(1).split())
    # strip comments from the selector text
    selector = re.sub(r'/\*.*?\*/', '', selector, flags=re.S).strip()
    body = m.group(2)
    for d in re.finditer(r'(--[A-Za-z0-9\-]+)\s*:\s*([^;]+);', body):
        name, value = d.group(1), ' '.join(d.group(2).split())
        tokens.setdefault(name, []).append({'selector': selector, 'value': value})

def light_value(name):
    for e in tokens.get(name, []):
        if 'dark' not in e['selector']:
            return e['value']
    return tokens.get(name, [{}])[0].get('value')

def dark_value(name):
    for e in tokens.get(name, []):
        if 'data-bs-theme="dark"' in e['selector'] and 'not(' not in e['selector']:
            return e['value']
    return None

# ---------------------------------------------------------------- 2. contrast, measured not asserted
def srgb(c):
    c = c / 255.0
    return c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4

def lum(hexs):
    hexs = hexs.strip().lstrip('#')
    if len(hexs) == 3:
        hexs = ''.join(ch * 2 for ch in hexs)
    if len(hexs) != 6:
        return None
    r, g, b = (int(hexs[i:i + 2], 16) for i in (0, 2, 4))
    return 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b)

def ratio(a, b):
    la, lb = lum(a), lum(b)
    if la is None or lb is None:
        return None
    hi, lo = max(la, lb), min(la, lb)
    return round((hi + 0.05) / (lo + 0.05), 2)

WHITE, INK = '#FFFFFF', '#071437'

scale = {}
for n, v in tokens.items():
    lv = light_value(n)
    if lv and re.fullmatch(r'#[0-9A-Fa-f]{3,8}', lv):
        scale[n] = lv

measured = {}
for n, v in scale.items():
    measured[n] = {
        'value': v,
        'contrast_on_white': ratio(v, WHITE),
        'contrast_white_on_it': ratio(WHITE, v),
        'contrast_ink_on_it': ratio(INK, v),
        'aa_normal_text_white_on_it': (ratio(WHITE, v) or 0) >= 4.5,
        'aa_normal_text_it_on_white': (ratio(v, WHITE) or 0) >= 4.5,
        'dark_theme_override': dark_value(n),
    }

# Gradients, shadows and other non-colour tokens are kept verbatim.
non_colour = {n: light_value(n) for n in tokens if n not in scale}

gradients = sorted(set(re.findall(r'(linear-gradient\([^;\)]*\)[^;]*)', src)))

# Where the sheet is loaded from, and in what order — the cascade claim must be checkable.
loaders = []
for dirpath, _dirnames, filenames in os.walk(os.path.join(APP, 'Views')):
    for fn in filenames:
        if not fn.endswith('.cshtml'):
            continue
        p = os.path.join(dirpath, fn)
        t = io.open(p, encoding='utf-8', errors='replace').read()
        if 'crossbuy-brand.css' in t:
            loaders.append(os.path.relpath(p, ROOT).replace('\\', '/'))

brand = {
    'source_of_truth': 'CrossBuy/wwwroot/Backend-assets/css/crossbuy-brand.css',
    'source_lines': src.count('\n') + 1,
    'loaded_by_views': sorted(loaders),
    'declaration_count': sum(len(v) for v in tokens.values()),
    'distinct_tokens': len(tokens),
    'colour_tokens': measured,
    'non_colour_tokens': non_colour,
    'gradients': gradients,
    'contrast_reference': {'white': WHITE, 'ink': INK, 'aa_normal': 4.5, 'aa_large': 3.0},
}
io.open(os.path.join(OUT, 'brand-tokens.json'), 'w', encoding='utf-8').write(
    json.dumps(brand, ensure_ascii=False, indent=2))

# ---------------------------------------------------------------- 3. asset manifest
BRAND_HINTS = ('logo', 'favicon', 'brand', 'wordmark', 'lockup', 'icon-', 'apple-touch')
ASSET_EXT = {'.svg', '.png', '.jpg', '.jpeg', '.ico', '.webp', '.gif',
             '.ttf', '.otf', '.woff', '.woff2'}
SKIP_DIRS = {'node_modules', 'plugins', 'obj', 'bin', '.git'}

rows = []
for base in (WWW, os.path.join(APP, 'BL', 'Reporting', 'Fonts')):
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d.lower() not in SKIP_DIRS]
        for fn in filenames:
            ext = os.path.splitext(fn)[1].lower()
            if ext not in ASSET_EXT:
                continue
            p = os.path.join(dirpath, fn)
            rel = os.path.relpath(p, ROOT).replace('\\', '/')
            low = rel.lower()
            is_font = ext in {'.ttf', '.otf', '.woff', '.woff2'}
            is_brand = any(h in os.path.basename(low) for h in BRAND_HINTS)
            if not (is_font or is_brand):
                continue
            try:
                data = io.open(p, 'rb').read()
            except OSError:
                continue
            rows.append({
                'path': rel,
                'file': fn,
                'kind': 'font' if is_font else 'brand-mark',
                'ext': ext.lstrip('.'),
                'bytes': len(data),
                'sha256_12': hashlib.sha256(data).hexdigest()[:12],
            })

# Where each asset is referenced, so a file nobody loads is visible as such.
refs = collections.defaultdict(set)
for base in (os.path.join(APP, 'Views'), os.path.join(APP, 'Controllers'),
             os.path.join(APP, 'BL'), os.path.join(WWW, 'Backend-assets', 'css'),
             os.path.join(WWW, 'assets'), os.path.join(WWW, 'assets-en')):
    if not os.path.isdir(base):
        continue
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d.lower() not in SKIP_DIRS]
        for fn in filenames:
            if os.path.splitext(fn)[1].lower() not in {'.cshtml', '.cs', '.css', '.js', '.html'}:
                continue
            p = os.path.join(dirpath, fn)
            try:
                t = io.open(p, encoding='utf-8', errors='replace').read()
            except OSError:
                continue
            rp = os.path.relpath(p, ROOT).replace('\\', '/')
            for r in rows:
                if r['file'] in t:
                    refs[r['path']].add(rp)

for r in rows:
    used = sorted(refs.get(r['path'], ()))
    r['reference_count'] = len(used)
    r['referenced_by'] = '; '.join(used[:4]) + (' …' if len(used) > 4 else '')

rows.sort(key=lambda r: (r['kind'], r['path']))
with io.open(os.path.join(OUT, 'asset-manifest.csv'), 'w', encoding='utf-8-sig', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=['path', 'file', 'kind', 'ext', 'bytes',
                                       'sha256_12', 'reference_count', 'referenced_by'])
    w.writeheader()
    w.writerows(rows)

print('tokens declared      :', brand['declaration_count'], 'over', brand['distinct_tokens'], 'names')
print('colour tokens        :', len(measured))
print('gradients            :', len(gradients))
print('views loading brand  :', len(loaders))
print('assets catalogued    :', len(rows),
      '(%d marks, %d fonts)' % (sum(1 for r in rows if r['kind'] == 'brand-mark'),
                                sum(1 for r in rows if r['kind'] == 'font')))
print('unreferenced assets  :', sum(1 for r in rows if r['reference_count'] == 0))
