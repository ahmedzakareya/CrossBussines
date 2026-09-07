# Stage 2A — Execution Roadmap

**Execution planning only. Not implementation.** No production code, schema, migration, SQL against `CrossBuyDB2`, UI
or feature work in this document.

Sizes are **relative** (S / M / L / XL), not calendar estimates — no velocity data exists to convert them honestly.

---

## 1. Batch table

| Batch | Objective | Depends on | DB impact | Risk | Rollback | Size | Test | Review point |
|---|---|---|---|---|---|---|---|---|
| **A** Grant Writer | Production writer for `PlatformRoleAssignments` + admin authorization + 8 additive columns + filtered unique index | — | **Additive** | **High** | Easy (additive) | **L** | **XL** | after A — **hard gate** |
| **B** Bootstrap Governance | `BootstrapAccessPolicies` + history + behaviour-preserving seed | **A** | Additive | High | Easy (flag) | L | L | after B |
| **P** Non-regression evidence | The 24-invariant harness + baseline capture | — (can start with A) | None | Low | n/a | M | **XL** | before any migration |
| **C** Master Data infrastructure | Unit Sets, packaging/variant/attribute tables, compatibility readers, shadow projection | B, **P** | Additive | High | Moderate | **XL** | XL | after C |
| **N** Migration utilities | Idempotent resumable engine, divergence reporting, cutover flags | C, P | Additive | High | Easy | M | L | after N |
| **D** Barcode Center | Barcode redesign + sold-unit guard + conflict workflow | C, N | Compatibility | **High** | Moderate | M | L | after D |
| **E** Unit Conversion Designer | Conversion redesign + Unit Sets UX | C, N | Compatibility | **High** | Moderate | M | L | with D |
| **F** Composite Designer | Revisions (existing → revision 1) + designer | C, N | Additive | **High** | Moderate | L | XL | after F |
| **G** Weighted Item admin | The missing admin surface (RISK-049) | C, D | None | Medium | Easy | **S** | M | with D |
| **H** Variant infrastructure | Template/attribute/variant model + matrix | C, **measurement** | Additive | High | Moderate | L | L | **blocked** |
| **I** Packaging | Package types, item packages, packaging barcodes | C, E | Additive | Medium | Easy | M | M | with I/J |
| **J** Commerce Presentation | Channel presentation tables + read-compatible view | C | Additive | Medium | Easy | M | M | with I/J |
| **K** Retail Core improvements | Shared retail core per the Hypermarket roadmap | C, G | Compatibility | Medium | Moderate | L | L | after K |
| **L** Hypermarket UX rebuild | Retail admin screens (weighted, scale, tax, loyalty) | **K**, G | None | Medium | Easy | L | M | after L |
| **M** Security Console | 7 views; effective permissions from the real access services | **A**, B | None | Medium | Easy | L | L | after M |
| **O** Wave 2 authorization rollout | 52 High-risk HR/Identity endpoints | **A**, **B**, M | None | **High** | Easy (attributes) | L | XL | after O |

## 2. Per-batch detail — the four that carry the most risk

**Batch A — Grant Writer.** *Value:* the only way a platform role can be granted in production; unblocks Wave 2, the
Security Console and cutover. *Blocking risks:* RISK-037 (Critical), RISK-039. *Evidence:* 23 Grant Writer tests + SQL
Server proof of the filtered unique index, validity constraints, transactions and concurrent grants, all
`[RequiredEvidence]`. *Why first:* every other security batch is meaningless without it.

**Batch B — Bootstrap Governance.** *Value:* makes bootstrap-open visible and auditable; closes the 14
Never-Bootstrap-Open actions. *Blocking risks:* RISK-040 (Critical), 041–045. *Critical constraint:* the seed **must
preserve current behaviour** — the first behavioural change is closing the 14, and HR/Projects cannot be hardened before
A exists (RISK-043).

**Batch P — Non-regression evidence.** *Value:* none user-visible; it is the licence to migrate anything. *Why early:*
the 24 invariants need a **baseline captured before** any Master Data change. Running it after C would measure the wrong
starting point. **This is the batch most likely to be skipped under pressure and the one that must not be.**

**Batch O — Wave 2.** *Value:* 52 High-risk endpoints protected. *Hard prerequisite:* A **and** B. Without both, the
gates evaluate bootstrap-open and allow everyone while the tests pass (RISK-037 + RISK-040). **O must never be
scheduled before A and B are green.**

## 3. Dependency verification

**No cycles.** Verified by topological ordering: `A → B → {M, O}`, `P → C → {N, D, E, F, G, H, I, J, K} → L`, with
`O` additionally requiring `M`'s prerequisites. Every batch depends only on earlier batches.

| Required order | Enforced how |
|---|---|
| Grant Writer **precedes** Bootstrap hardening | B depends on A; RISK-043 |
| Bootstrap **precedes** Wave 2 | O depends on A **and** B; RISK-040 |
| Master Data **precedes** WMS | WMS is Stage 4, after all of 2A/2C |
| Master Data **precedes** Manufacturing expansion | Manufacturing is Stage 5 |
| Retail Core **precedes** Hypermarket UX | L depends on K |
| Evidence **precedes** migration | N and D–J all depend on P |
| Migration **precedes** cleanup | no cleanup batch exists in 2A — deliberately deferred |

**No batch depends on future work.** Batch **H is blocked by data measurement**, not by a later batch — that is an
external approval dependency, and it is why H is listed but unscheduled.

## 4. Recommended sequence

```
A (Grant Writer)  ┐                      hard gate
P (Evidence)      ┘ can run in parallel
        ↓
B (Bootstrap)     → M (Console) → O (Wave 2)
        ↓
C (Master Data infra) → N (Migration utilities)
        ↓
D + E + G (barcode, conversions, weighted)   ← highest-risk data surfaces
        ↓
F (Composite)  →  I + J (packaging, commerce)
        ↓
K (Retail Core) → L (Hypermarket UX)
        ↓
H (Variants)      ← only after measurement is approved
```

**A and P first, in parallel** — one unblocks security, the other licenses migration. Neither depends on the other.

## 5. What is deliberately NOT in Stage 2A

The Roslyn authorization analyzer and CI evidence guardrail (the original 2A scope) are **not** in this batch list,
because the brief's batch set is security- and Master-Data-led. **Recommendation:** the analyzer and CI guardrail should
be **Batch 0**, before A — they were the original evidence-backed 2A scope, and they are what makes every later batch
self-verifying rather than self-reported. Flagged for decision rather than inserted unilaterally.
