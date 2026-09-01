# Communication F-1 — Resolution

## Outcome: `F1-UNRESOLVED`

**There is no requirement named F-1 in this repository.** Not in code, not in commit messages, not
in docs, not in the ownership registry, not in reflogs, not in any HANDOFF file.

## What was searched, and what every apparent hit turned out to be

| Source | Result |
|---|---|
| commit messages (all refs, fixed-string) | nothing — the only matches are this batch's own reports saying F-1 is unprovable |
| tracked `.cs` / `.md` / `.json` / `.sql` / `.cshtml` | 4 files, **all substrings of `SHF-10`, `SHF-11`, `SHF-14`, `SHF-15`** |
| `docs/platform/Report-Studio-V2-Visual-Designer.md` | `%PDF-1.4` — a file-format version string |
| `tools/authz-probe/authz-probe.mjs` | "Reporting — Phase 3F-1", a different phase label |
| reflogs | nothing |
| candidate patch directories, HANDOFF files | nothing |

## The likely origin, stated as a hypothesis and labelled as one

`C:\temp\v407\HANDOVER_FIX2.md` and `C:\temp\wt_front_prod\HANDOVER_FIX2.md` contain **real F-1
items** — but they are about `chartData` memoization and a `useMultiFilter` hook in a React
application. That is a different product on the same machine, not CrossBusiness. A label crossing
from one product's handover into another's is a plausible explanation for how "Communication F-1"
entered a CrossBusiness handoff with nothing behind it.

This is offered as the most likely account, not as proof.

## Consequence

The label is removed from technical open-bug language and recorded as:

> **Historical requirement identifier requiring product-owner clarification.**

An unknown label must not permanently contaminate engineering completeness. If the owner can name
the requirement, it can be assessed in one pass; until then there is nothing to implement, and
inventing an `IWorkspaceNotificationAuthority` to close a label would have produced a second
notification authority the architecture explicitly refuses.

## What was verified independently of the name (§6)

The behaviour F-1 was attached to **is present and covered at canonical HEAD**:

* `BL/Communication/CommNotificationDispatcher` is the transactional outbox dispatcher. It claims
  rows by setting `ClaimedAt`, recovers stale claims (`ClaimedAt < staleBefore`), and releases the
  claim on completion and on failure.
* It is company-scoped: `(companyId == null || d.CompanyID == companyId.Value)`.
* It runs through `IServiceScopeFactory`, so dispatch happens on its own scope after the producing
  transaction — post-commit, not inside it.
* `CommMessage.ClaimedAt` exists in the model and is documented as the stale-claim clock.
* Tracked `CommTimelineAndDispatchTests` asserts the claim is released on completion and re-claimable
  on the next pass.
* **No second notification authority exists.** `IWorkspaceNotificationAuthority` is not a type in
  this repository; what exists is `IWorkspaceNotificationSource` plus `ICommNotificationDispatcher`
  and `ICommNotificationService`. The constraint holds because nothing was added — a weaker claim
  than "closed", and the only one the evidence supports.

An **older, superseded** dispatcher (`CrossBuy/BL/Comm/`, 3 August) exists as untracked production
code. Its two test suites were classified obsolete rather than recovered.
