// PROOF for the warm-up gate in certification-gate.mjs.
//
// The rule decides whether a screenshot baseline may be blessed. Before this file the only way to
// exercise it was three full governed matrices — ~45 minutes, against a live host, to observe ONE
// branch. Several branches (a mode that flips mid-run, a malformed status, a half-configured host)
// a live host will not produce on demand at all, so they were never tested. Here every branch is a
// list of samples and a verdict.
//
//   node certification-gate-proof.mjs        exit 0 = all cases hold
import { evaluateSequence, CERT_STABLE_PROBES } from './certification-gate.mjs';

const NORMAL = (role) => ({ workerRole: role, certificationMode: false, backgroundWritersSuppressed: false });
const CERT   = (role) => ({ workerRole: role, certificationMode: true,  backgroundWritersSuppressed: true });
const rep = (s, n) => Array.from({ length: n }, () => s);

let pass = 0, fail = 0;
function check(kind, name, samples, wanted) {
    const r = evaluateSequence(samples);
    const ok = r.verdict === wanted;
    if (ok) { pass++; console.log(`   ok    [${kind}] ${name}  ->  ${r.verdict}`); }
    else { fail++; console.log(`   FAIL  [${kind}] ${name}  ->  expected ${wanted}, got ${r.verdict} (${r.reason})`); }
}

console.log('\n=== NEGATIVE PROOFS — the gate must REFUSE ===');

// The normal contract is unchanged: only Primary, however stable anything else looks.
check('neg', 'normal runtime + Unknown (stable, 30 probes)', rep(NORMAL('Unknown'), 30), 'fail');
check('neg', 'normal runtime + Standby (stable, 30 probes)', rep(NORMAL('Standby'), 30), 'fail');
check('neg', 'normal runtime never reaching Primary (Unknown->Standby->Unknown)',
      [...rep(NORMAL('Unknown'), 5), ...rep(NORMAL('Standby'), 5), ...rep(NORMAL('Unknown'), 5)], 'fail');
check('neg', 'normal runtime + a single Primary that never settles',
      [NORMAL('Standby'), NORMAL('Primary'), NORMAL('Standby'), NORMAL('Primary')], 'fail');

// A half-configured host is refused rather than quietly demoted to the normal contract.
check('neg', 'certificationMode=true but writers NOT suppressed',
      rep({ workerRole: 'Unknown', certificationMode: true, backgroundWritersSuppressed: false }, 20), 'fail');
check('neg', 'writers suppressed but certificationMode=false',
      rep({ workerRole: 'Unknown', certificationMode: false, backgroundWritersSuppressed: true }, 20), 'fail');

// Terminal means terminal.
check('neg', 'certification mode with a CHANGING workerRole',
      [...rep(CERT('Unknown'), 3), CERT('Standby'), ...rep(CERT('Standby'), 10)], 'fail');
check('neg', 'certification mode flipping to normal mid-warm-up',
      [...rep(CERT('Unknown'), 3), ...rep(NORMAL('Unknown'), 10)], 'fail');

// Anything unreadable or malformed is a refusal, never a retry-forever.
check('neg', 'runtime status unreadable (fetch error)', [{ error: 'TypeError: failed to fetch' }], 'fail');
check('neg', 'runtime status HTTP 500', [{ error: 'HTTP 500' }], 'fail');
check('neg', 'runtime status missing workerRole',
      rep({ certificationMode: true, backgroundWritersSuppressed: true }, 10), 'fail');
check('neg', 'runtime status with null workerRole',
      rep({ workerRole: null, certificationMode: true, backgroundWritersSuppressed: true }, 10), 'fail');
check('neg', 'certification mode but too few stable probes',
      rep(CERT('Unknown'), CERT_STABLE_PROBES - 1), 'fail');

console.log('\n=== POSITIVE PROOFS — the gate must ACCEPT ===');

// Normal runtime still passes exactly as before, and only via Primary.
check('pos', 'normal runtime reaching a settled Primary',
      [...rep(NORMAL('Standby'), 3), ...rep(NORMAL('Primary'), 5)], 'pass');

// Certification runtime accepts the terminal role produced by intentional worker suppression.
check('pos', 'certification runtime, stable Unknown (the real observed case)',
      rep(CERT('Unknown'), CERT_STABLE_PROBES + 2), 'pass');
check('pos', 'certification runtime, stable Standby is equally terminal',
      rep(CERT('Standby'), CERT_STABLE_PROBES + 2), 'pass');
check('pos', 'certification runtime that happens to report Primary is still fine',
      rep(CERT('Primary'), CERT_STABLE_PROBES + 2), 'pass');

console.log(`\n=== ${pass} passed, ${fail} failed ===`);
process.exit(fail === 0 ? 0 : 1);
