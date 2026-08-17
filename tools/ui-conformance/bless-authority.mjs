// Re-bless the AUTHORITY AGGREGATES in baseline/authority-baseline.json.
//
// WHY THIS EXISTS. `ui-conformance.mjs --bless` records SCREENSHOT baselines only. The aggregate
// hashes in authority-baseline.json are read by the C# gate
// (CrossBuy.Tests/UiConformance/UiConformanceTests.The_visual_authority_matches_its_recorded_baseline)
// and NO tool wrote them — the README calls re-blessing "a deliberate act by the module owner".
// Doing it by hand would mean typing expected hashes, which is exactly how a baseline stops being
// falsifiable. So the values are DERIVED here with the gate's own documented algorithm:
//
//     aggregate = sha256( concat( "<repo-relative-path>:<sha256-of-file>\n" ),
//                         for files matching glob, sorted by ORDINAL relative path )
//
// mirroring UiConformanceTests.Aggregate()/Rel()/Sha256() line for line:
//   - Directory.GetFiles(dir, pattern)         -> readdirSync + exact glob-tail match, files only
//   - .OrderBy(Rel, StringComparer.Ordinal)    -> sort on the '/'-normalised relative path, ordinal
//   - Rel                                      -> path relative to the repo root, '\' -> '/'
//   - Sha256                                   -> lowercase hex
//
// Only the `aggregate` values change. Globs, notes and the explanatory keys are preserved, and the
// file is rewritten with the same 2-space JSON shape it already uses.
//
//   node bless-authority.mjs            dry run - prints old -> new, writes nothing
//   node bless-authority.mjs --write    applies
import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, '..', '..');            // tools/ui-conformance -> repo root
const FILE = join(HERE, 'baseline', 'authority-baseline.json');
const WRITE = process.argv.includes('--write');

const sha256 = (buf) => createHash('sha256').update(buf).digest('hex');

function aggregate(glob) {
    const sep = glob.lastIndexOf('/');
    const dir = join(REPO, glob.slice(0, sep).split('/').join('\\'));
    const pattern = glob.slice(sep + 1);           // e.g. "*.cshtml" or an exact file name

    let names;
    try { names = readdirSync(dir); } catch { return { hash: null, files: [], error: `directory not found: ${dir}` }; }

    const matches = names.filter((n) => {
        if (!pattern.includes('*')) return n === pattern;
        const tail = pattern.slice(pattern.indexOf('*') + 1);
        return n.endsWith(tail);
    }).filter((n) => { try { return statSync(join(dir, n)).isFile(); } catch { return false; } });

    // Rel(): repo-relative, forward slashes. Sort ordinal on that string, as the C# does.
    const rel = (n) => (glob.slice(0, sep) + '/' + n);
    matches.sort((a, b) => (rel(a) < rel(b) ? -1 : rel(a) > rel(b) ? 1 : 0));

    let s = '';
    for (const n of matches) s += `${rel(n)}:${sha256(readFileSync(join(dir, n)))}\n`;
    return { hash: sha256(Buffer.from(s, 'utf8')), files: matches };
}

const raw = readFileSync(FILE, 'utf8');
const doc = JSON.parse(raw);
const changes = [];

for (const [key, val] of Object.entries(doc)) {
    if (!val || typeof val !== 'object' || !val.glob) continue;
    const { hash, files, error } = aggregate(val.glob);
    if (error) { console.log(`  !! ${key}: ${error}`); process.exitCode = 1; continue; }
    changes.push({ key, glob: val.glob, files: files.length, old: val.aggregate, now: hash, changed: val.aggregate !== hash });
}

console.log(`repo root : ${REPO}`);
console.log(`baseline  : ${FILE}`);
console.log(`mode      : ${WRITE ? 'WRITE' : 'DRY RUN (nothing written)'}\n`);
for (const c of changes) {
    console.log(`  ${c.key}`);
    console.log(`     glob  : ${c.glob}   (${c.files} file(s))`);
    console.log(`     old   : ${c.old}`);
    console.log(`     new   : ${c.now}`);
    console.log(`     status: ${c.changed ? 'DRIFTED -> will be re-blessed' : 'unchanged'}`);
}

if (WRITE) {
    for (const c of changes) doc[c.key].aggregate = c.now;
    writeFileSync(FILE, JSON.stringify(doc, null, 2) + '\n', 'utf8');
    console.log(`\nWRITTEN. ${changes.filter((c) => c.changed).length} aggregate(s) updated, ${changes.length - changes.filter((c) => c.changed).length} unchanged.`);
} else {
    console.log('\nDry run only. Re-run with --write to apply.');
}
