// THE WARM-UP DECISION, as a pure state machine.
//
// WHY IT IS SEPARATE. The rule it encodes decides whether a screenshot baseline may be blessed, and
// the only previous way to exercise it was to run three full governed matrices — about 45 minutes,
// against a live host, to observe ONE branch. A rule that expensive to test is a rule that gets
// changed without being re-proved. Here it takes a list of runtime samples and returns a verdict, so
// every branch (including the ones a live host will not produce on demand, like a mode that flips
// mid-run) is provable in milliseconds.
//
// There are two contracts and the RUNTIME chooses between them — never a flag passed to the tool:
//
//   NORMAL        the dispatch worker is registered, so it will claim the lease and workerRole WILL
//                 become Primary. Unknown and Standby are refused however stable they look, because
//                 in this mode "stable" only means "has not transitioned yet". Unchanged behaviour.
//
//   CERTIFICATION UI certification deliberately does not register BusinessEventDispatchWorker, so no
//                 lease is claimed and the role is terminal at its starting value. Waiting for
//                 Primary would wait forever. The requirement becomes what actually matters: the role
//                 must be present, single-valued and unchanging across the observation window.
//
// The relaxation requires certificationMode AND backgroundWritersSuppressed to BOTH be true. A run
// claiming certification mode while its writers still run is refused: that is a misconfigured host,
// and it is exactly the case where a stable-looking Unknown would be luck rather than a guarantee.

export const CERT_STABLE_PROBES = 5;

export function createWarmUpEvaluator({ certStableProbes = CERT_STABLE_PROBES } = {}) {
    let last = null, stableFor = 0, certObserved = null;

    return {
        /**
         * @param {{workerRole?:string, certificationMode?:boolean, backgroundWritersSuppressed?:boolean, error?:string}} s
         * @returns {{verdict:'continue'|'pass'|'fail', reason?:string}}
         */
        feed(s) {
            if (!s || s.error) return fail(`runtime status unreadable (${s ? s.error : 'no sample'})`);
            if (typeof s.workerRole !== 'string' || s.workerRole.length === 0)
                return fail('runtime status carries no workerRole — malformed');

            const certMode = s.certificationMode === true;
            const suppressed = s.backgroundWritersSuppressed === true;

            // A half-configured claim is refused rather than quietly demoted to the normal contract.
            if (certMode !== suppressed)
                return fail(`certificationMode=${certMode} but backgroundWritersSuppressed=${suppressed}` +
                            ' — inconsistent runtime contract');

            const certification = certMode && suppressed;
            if (certObserved === null) certObserved = certification;
            else if (certObserved !== certification)
                return fail('certification mode changed during warm-up');

            const role = s.workerRole;

            if (certification) {
                if (role === last) {
                    stableFor++;
                    if (stableFor >= certStableProbes)
                        return pass(`certification mode (writers suppressed), workerRole=${role} ` +
                                    `single-valued and unchanged across ${stableFor + 1} probes`);
                } else if (last !== null) {
                    // Terminal means terminal. A role that moves at all is not a settled state.
                    return fail(`certification mode but workerRole moved ${last} -> ${role} — not terminal`);
                } else {
                    stableFor = 0;
                }
            } else {
                // NORMAL: unchanged rule. Only Primary, and only once settled.
                if (role === 'Primary') {
                    if (role === last) { stableFor++; if (stableFor >= 2) return pass(`workerRole=${role}, settled`); }
                    else stableFor = 0;
                } else {
                    stableFor = 0;
                }
            }

            last = role;
            return { verdict: 'continue' };
        },

        /** No sample ever satisfied the contract within the window. */
        exhausted() {
            return fail(`workerRole never reached a terminal state (last=${last}, certification=${certObserved})`);
        },
    };

    function pass(reason) { return { verdict: 'pass', reason }; }
    function fail(reason) { return { verdict: 'fail', reason }; }
}

/** Convenience for tests: run a whole sample sequence and return the final verdict. */
export function evaluateSequence(samples, options) {
    const ev = createWarmUpEvaluator(options);
    for (const s of samples) {
        const r = ev.feed(s);
        if (r.verdict !== 'continue') return r;
    }
    return ev.exhausted();
}
