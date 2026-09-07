# Stage 2 — Business Value Roadmap

**Planning only.** Maturity figures are assessments from source evidence, consistent with the frozen Stage 1 maturity
record (57.60 / 100) and the Phase 0 module reassessment. Delivery sequence references
`Stage-002A-Execution-Roadmap.md` batches.

---

| Capability | Current | Target (end 2A/2C) | Business benefit | Dependencies | Batches | Sequence |
|---|---|---|---|---|---|---|
| **Security & authorization** | **Low-Med** — 143 endpoints unprotected, no grant writer, 14 financial actions bootstrap-open | **High** | A tenant can be configured safely; financial posting requires a role; grants are auditable | — | **A, B, M, O** | **1st** |
| **Master Data** | **Low-Med** — 4 overlapping type signals, 2 duplicated sources, no variants/packaging/lifecycle | **High** | One coherent product record; barcode and unit correctness provable | A, B, P | **C, D, E, F, G, I, J, H** | **2nd** |
| **Retail / Hypermarket** | **Med-High engine, Low admin** — weighted items and scale codes work but have no screen | **High** | Fresh food configurable without SQL; retail admin owns retail data | C, G | **K, L** | 3rd |
| **Inventory** | **High engine** — stock writer, locking, batch/expiry, moving average proven | **High** (unchanged engine, better master data) | No regression; better item data quality | C | C, D, E, I | with 2nd |
| **POS** | **High** — two lanes, capabilities, offline sync, fail-closed authorization | **High** (unchanged) | Protected from Master Data regression by the 24 invariants | P | P | continuous |
| **Manufacturing** | **Medium** — routing, work centres, planning exist; no scheduling | Medium+ | Composite revisions make historical cost reproducible | C, F | F | Stage 5 for scheduling |
| **Workflow & approvals** | **Very Low** — leave chain only | Medium | Master Data approvals; four-eyes on money-or-quantity changes | 2B framework | B, C | 2nd–3rd |
| **Reporting** | **Low** — statements and dashboards only | Low+ | Data-quality and exposure reporting | A, B, C | M | later |
| **CRM** | **Low-Med** — owner scoping works; 97 company constants | Low-Med+ | Role source consolidated | A | A | 1st (partial) |
| **HR** | **Medium engine, bootstrap-open in production** | Medium+ | Payroll and confidential tiers genuinely closed | **A, B** | A, B, O | 1st |
| **Customer Experience** | **None** | None in Stage 2 | — | 2B framework | — | Stage 7 |
| **Portals** | **None** | None in Stage 2 | — | trimming | — | Stage 7+ |
| **Mobile** | **Low** — POS/WMS lanes only | Low+ | Six focused Master Data workflows (scan, capture, approve, weighted validation) | C, D, G | D, G | 2nd |
| **Analytics** | **Low** | Low+ | Master Data quality dashboard | C | C | 2nd |
| **AI** | **Very Low** — 4 endpoints, authentication-only | Very Low+ | Endpoints authorized (Wave 2); duplicate detection designed but gated on trimming | **O**, 2B trimming | O | 1st for security, Stage 9 for capability |

## Value sequencing — the honest shape

**Stage 2A delivers no end-user feature.** Its value is that a company can be **configured safely** for the first time:
roles can be granted, bootstrap-open becomes visible, and financial posting stops being open on an unconfigured tenant.
That is a real business benefit (a new tenant is no longer permissive by default) but it is not a screen anyone asks for.

**Stage 2C is where user-visible value concentrates** — the Product Workspace, Barcode Center, unit and composite
designers, and the weighted-item admin screen that closes the "no screen exists" gap.

**Three capabilities move without any work of their own**: Inventory, POS and Manufacturing benefit from the 24
non-regression invariants — their guarantee is that nothing breaks, which is why Batch P is scheduled early rather than
treated as test cleanup.

## Capabilities deliberately not advanced in Stage 2

Customer Experience · Portals · full Reporting/BI · AI capability (beyond authorizing its 4 endpoints) · WMS · advanced
Manufacturing scheduling. Each is sequenced in the accepted `Stage-002-Dependency-Roadmap.md` at Stage 4 or later, and
each depends on the 2B framework or on Master Data landing first.
