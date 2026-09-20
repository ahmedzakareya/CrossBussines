# -*- coding: utf-8 -*-
"""Build business-catalog.json: modules, screens, entities, reports, and the brand-drift metrics.

Repository evidence only. Nothing is executed, nothing is written outside the discovery folder.
"""
import io, os, re, json, collections

ROOT = r'C:\CrossBuy\CrossBuy'
APP = os.path.join(ROOT, 'CrossBuy')
OUT = os.path.join(ROOT, 'docs', 'business-brand-discovery')

def read(p):
    return io.open(p, encoding='utf-8', errors='replace').read()

# ---------------------------------------------------------------- 1. the menu = the product's own map
menu_src = read(os.path.join(APP, 'Models', 'Menu', 'MainMenu.cs'))
modules = collections.OrderedDict()
cur_mod = cur_cat = None
for line in menu_src.split('\n'):
    m = re.search(r'public static List<MenuCategory> (\w+)\(\)', line)
    if m:
        cur_mod = m.group(1)
        modules[cur_mod] = {'categories': []}
        cur_cat = None
        continue
    if cur_mod is None:
        continue
    c = re.search(r'LabelAr = "([^"]*)", LabelEn = "([^"]*)", Icon = "([^"]*)"', line)
    if c:
        cur_cat = {'en': c.group(2), 'ar': c.group(1), 'icon': c.group(3), 'items': []}
        modules[cur_mod]['categories'].append(cur_cat)
        continue
    i = re.search(r'LabelAr = "([^"]*)", LabelEn = "([^"]*)", Action = "([^"]*)"'
                  r'(?:,\s*\n?\s*Controller = "([^"]*)")?', line)
    if i and cur_cat is not None:
        cur_cat['items'].append({'en': i.group(2), 'ar': i.group(1),
                                 'action': i.group(3), 'controller': i.group(4)})

# A Controller on its own line follows the label; stitch those up.
for mod in modules.values():
    for cat in mod['categories']:
        for it in cat['items']:
            if it['controller'] is None:
                it['controller'] = '(same line continuation — see MainMenu.cs)'

total_items = sum(len(c['items']) for m in modules.values() for c in m['categories'])

# ---------------------------------------------------------------- 2. controllers and their actions
controllers = {}
for base, api in ((os.path.join(APP, 'Controllers'), False),
                  (os.path.join(APP, 'Controllers', 'Api'), True)):
    for fn in sorted(os.listdir(base)):
        if not fn.endswith('.cs'):
            continue
        p = os.path.join(base, fn)
        if os.path.isdir(p):
            continue
        t = read(p)
        name = fn[:-3]
        acts = re.findall(r'public\s+(?:async\s+)?(?:Task<)?I?ActionResult>?\s+(\w+)\s*\(', t)
        gets = len(re.findall(r'\[HttpGet', t))
        posts = len(re.findall(r'\[HttpPost', t))
        controllers[name] = {
            'file': os.path.relpath(p, ROOT).replace('\\', '/'),
            'api': api,
            'lines': t.count('\n') + 1,
            'actions': len(set(acts)),
            'httpGet': gets, 'httpPost': posts,
            'authorize': bool(re.search(r'\[Authorize', t)),
            'allowAnonymous': len(re.findall(r'\[AllowAnonymous', t)),
        }

# ---------------------------------------------------------------- 3. views
views = collections.Counter()
view_files = []
vroot = os.path.join(APP, 'Views')
for dirpath, _d, filenames in os.walk(vroot):
    for fn in filenames:
        if fn.endswith('.cshtml'):
            folder = os.path.relpath(dirpath, vroot).split(os.sep)[0]
            views[folder] += 1
            view_files.append(os.path.join(dirpath, fn))

# ---------------------------------------------------------------- 4. persisted business objects
ctx = read(os.path.join(APP, 'Models', 'Context', 'CrossDbContext.cs'))
entities = sorted(set(re.findall(r'DbSet<([A-Za-z0-9_.]+)>', ctx)))

# ---------------------------------------------------------------- 5. the report catalogue
rep_dir = os.path.join(APP, 'BL', 'Reporting')
rep_src = '\n'.join(read(os.path.join(rep_dir, f)) for f in os.listdir(rep_dir)
                    if f.endswith('.cs'))
report_codes = sorted(set(re.findall(
    r'"((?:Inventory|Accounting|Crm|Admin|Roster|Platform)\.[A-Za-z0-9.]+)"', rep_src)))
report_perms = sorted(set(re.findall(r'"([a-z]+\.[a-z.]+\.(?:view|export|notes))"', rep_src)))
renderers = sorted(set(re.findall(r'EngineName => \$?"([^"]+)"', rep_src)))

# ---------------------------------------------------------------- 6. brand drift, measured
HEX = re.compile(r'#[0-9A-Fa-f]{6}\b')
TOKEN = re.compile(r'var\(--(?:cb|bs|kt)-[A-Za-z0-9\-]+\)')
drift = []
for p in view_files:
    t = read(p)
    hexes = HEX.findall(t)
    toks = TOKEN.findall(t)
    if not hexes:
        continue
    drift.append({
        'view': os.path.relpath(p, ROOT).replace('\\', '/'),
        'hardcoded_hex': len(hexes),
        'distinct_hex': len(set(h.upper() for h in hexes)),
        'token_refs': len(toks),
    })
drift.sort(key=lambda r: -r['hardcoded_hex'])

all_hex = collections.Counter()
for p in view_files:
    for h in HEX.findall(read(p)):
        all_hex[h.upper()] += 1

# Which of the most-used colours are actually brand tokens?
brand_json = json.load(io.open(os.path.join(OUT, 'brand-tokens.json'), encoding='utf-8'))
token_values = {v['value'].upper() for v in brand_json['colour_tokens'].values()
                if v.get('value')}

top_colours = [{'hex': h, 'uses': n, 'is_brand_token': h in token_values}
               for h, n in all_hex.most_common(40)]

# ---------------------------------------------------------------- 7. bilingual coverage
res_dir = os.path.join(APP, 'Resources')
def keys_of(fn):
    p = os.path.join(res_dir, fn)
    if not os.path.exists(p):
        return set()
    return set(re.findall(r'<data name="([^"]+)"', read(p)))

neutral, ar, en, fr = (keys_of('SharedResources.resx'), keys_of('SharedResources.ar.resx'),
                       keys_of('SharedResources.en.resx'), keys_of('SharedResources.fr.resx'))

# The per-screen resource files live under Resources/Views/<Folder>/<View>.<culture>.resx, so a
# flat listing of Resources/ finds only the four shared ones — which is how a 891-file localization
# estate can be mistaken for a 4-file one.
per_view = collections.Counter()
resx_total = 0
for dirpath, _d, filenames in os.walk(res_dir):
    for fn in filenames:
        if not fn.endswith('.resx'):
            continue
        resx_total += 1
        stem = os.path.join(os.path.relpath(dirpath, res_dir), fn)
        stem = re.sub(r'\.(ar|en|fr)\.resx$', '', stem)
        stem = re.sub(r'\.resx$', '', stem)
        per_view[stem.replace(os.sep, '/')] += 1

catalog = {
    'generated_for': 'CrossBuy business & brand discovery',
    'repository_root': ROOT,
    'modules': modules,
    'module_count': len(modules),
    'menu_entry_count': total_items,
    'controllers': controllers,
    'controller_count': len(controllers),
    'views_by_folder': dict(views.most_common()),
    'view_count': sum(views.values()),
    'persisted_entities': entities,
    'persisted_entity_count': len(entities),
    'report_codes': report_codes,
    'report_permission_keys': report_perms,
    'report_engines': renderers,
    'localization': {
        'cultures': ['ar', 'en', 'fr'],
        'shared_keys': {'neutral': len(neutral), 'ar': len(ar), 'en': len(en), 'fr': len(fr)},
        'ar_missing_vs_neutral': sorted(neutral - ar)[:40],
        'ar_missing_count': len(neutral - ar),
        'fr_missing_count': len(neutral - fr),
        'resx_files': resx_total,
        'units_by_culture_count': dict(collections.Counter(per_view.values())),
        'localized_view_units': len(per_view),
        'units_with_all_four': sum(1 for k, v in per_view.items() if v >= 4),
    },
    'brand_drift': {
        'views_with_hardcoded_hex': len(drift),
        'views_total': len(view_files),
        'worst_offenders': drift[:20],
        'most_used_colours': top_colours,
        'colours_that_are_brand_tokens': sum(1 for c in top_colours if c['is_brand_token']),
    },
}

io.open(os.path.join(OUT, 'business-catalog.json'), 'w', encoding='utf-8').write(
    json.dumps(catalog, ensure_ascii=False, indent=2))

print('modules            :', len(modules))
print('menu entries       :', total_items)
print('controllers        :', len(controllers), '(%d api)' % sum(1 for c in controllers.values() if c['api']))
print('actions total      :', sum(c['actions'] for c in controllers.values()))
print('views              :', sum(views.values()), 'in', len(views), 'folders')
print('persisted entities :', len(entities))
print('report codes       :', len(report_codes))
print('resx files         :', catalog['localization']['resx_files'],
      'over', catalog['localization']['localized_view_units'], 'units;',
      catalog['localization']['units_with_all_four'], 'have all four')
print('ar missing keys    :', len(neutral - ar), ' fr missing:', len(neutral - fr))
print('views w/ hex       :', len(drift), 'of', len(view_files))
print('top colours that are tokens:', catalog['brand_drift']['colours_that_are_brand_tokens'], 'of 40')
