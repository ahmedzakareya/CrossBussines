# Stage-Communication — Integration Gate Report

**Platform:** CrossBusiness Communication & Collaboration Platform
**Owner tab:** THIRD TAB (Communication & Collaboration)
**Measured:** 2026-08-06, 08:00–08:25
**Verdict:** **GATE MET on every condition this tab owns.** One condition — *"integrated build runs without
exclusions"* — is **NOT met, and is not attributable to this tab.** See §3.

---

## 1. Completion gate — condition by condition

| # | Condition | Verdict | Evidence |
| --- | --- | --- | --- |
| 1 | Integrated build runs without exclusions | ❌ **RED — Construction-owned** | §3 |
| 2 | Communication tests ≥ 250/250 | ✅ **273/273** | §4 |
| 3 | SQL applies from empty twice | ✅ | SQL-From-Empty Evidence |
| 4 | Permission boundary mechanically proven | ✅ 24 tests / 9 properties | §5 |
| 5 | Entity registry contract exists | ✅ | Entity-Registry-Contract |
| 6 | Task and Support remain unregistered | ✅ 0 matches in `EntityRegistry` | §7 |
| 7 | DocComments migration not executed | ✅ no script exists; source untouched | §7 |
| 8 | No `Program.cs` production activation | ✅ `AddCommunicationPlatform` not called | §7 |
| 9 | No hosted worker added | ✅ dispatcher is `AddScoped` | §7 |
| 10 | No UI built | ✅ no controller references the platform | §7 |
| 11 | Archive restore succeeds, 0 mismatches | ✅ | Preservation Report |
| 12 | CrossBuyDB2 remains untouched | ✅ 1157 objects before and after | SQL evidence §7 |

---

## 2. Builds

Run against the **full shared tree with no exclusions**, as instructed.

| Target | Debug | Release | TestRun |
| --- | --- | --- | --- |
| `CrossBuy.csproj` (production code) | ✅ **0 errors** | ✅ **0 errors** | ✅ **0 errors** |
| `CrossBuy.sln` (incl. shared test project) | ❌ 16 errors | ❌ | ❌ |

**Every error in the solution build is in `CrossBuy.Tests`, and every one is Construction-owned.** A filtered
count of non-Construction errors returns **0**.

Two environmental notes, recorded because they cost time and will recur:

* **Release built to a scratch output path.** `bin\Release\net8.0\CrossBuy.dll` was locked by the user's Visual
  Studio (PID 21604) and IIS Express (PID 31580). Their processes were **not** killed; `-p:BaseOutputPath=` was
  redirected instead. Compilation is unaffected by output location.
* **`TestRun` declares its symbols correctly** in both csproj files (`DEBUG;TRACE`, `Optimize=false`), so a
  TestRun acceptance compiles the same program as Debug — the defect CLAUDE.md records as having been fixed.

---

## 3. The one red condition — classified, not excused

### 3.1 What is broken

`CrossBuy.Tests` contains Construction test files referencing `CrossBuy.BL.Construction`, **a namespace that does
not exist on disk**. `CrossBuy/BL/Construction/` is absent.

```
ConstructionC1BoqIdentityTests.cs            landed 23:07
ConstructionTestFixture.cs                   landed 23:08
ConstructionC1ConcurrencyAndAuditTests.cs    landed ~23:12
ConstructionC1CommercialRevisionTests.cs     landed later
(5 files at time of writing)
```

Missing types: `IConstructionAuditService`, `ICommercialRevisionService`, `ISubcontractScopeService`,
`ConstructionTestFixture`, `BoqLineInput`.

**This is a partial landing: the Construction tab's tests arrived before its business layer.** The shared test
assembly therefore does not compile for *any* tab.

### 3.2 Why this tab did not fix it

The brief is explicit: *"Do not modify another tab's files to obtain a green result"*, and *"Do not modify:
Construction production logic"*. Writing the missing `CrossBuy/BL/Construction/` to turn the build green would
violate both, and would also fabricate another team's architecture.

### 3.3 How Communication evidence was obtained anyway

Construction's **own test files** were excluded via a command-line MSBuild injection
(`-p:CustomBeforeMicrosoftCommonTargets=`), which modifies **nothing in the repository** — no file was edited,
moved, renamed or committed. Scope is exactly `CrossBuy.Tests/Construction*.cs`; no folder is excluded and no
Communication, Reporting or platform file is touched.

The unexcluded result is reported above as RED. **The exclusion is a measurement device, not a claim of green.**

### 3.4 Tree volatility — a standing condition, not an incident

Three other tabs were writing to the shared tree during this gate. Observed within ~25 minutes:

| Time | Event | Owner |
| --- | --- | --- |
| 23:07–23:12 | Construction test files land without their BL | Construction |
| 08:09 | `BL/Reporting/ReportDatasetRegistry.cs` appears with a signature mismatch | Reporting |
| ~08:15 | That error disappears — corrected mid-write by its owner | Reporting |
| ~08:18 | `ReportingTestHost.cs` grows an `EngineWith` method | Reporting |
| 08:20 | Solution builds clean again | — |

**Consequence for reading any cross-tab number in this report: it is a timestamp, not a steady state.** Every
Communication figure below carries the time it was measured.

---

## 4. Tests

**Measured 2026-08-06 08:22:05** (Construction files excluded per §3.3):

```
Communication:  Passed 273  ·  Failed 0  ·  Skipped 0   (273 total)
Full suite:     Passed 918+ ·  Failed 0  ·  Skipped 183
```

### 4.1 The count is 273, not 250 — and the starting point was 249, not 250

The brief states the delivered platform had **250/250**. The measured baseline in
`CrossBuy.Tests.Communication` was **249**, and it is reported as 249 rather than restated as 250.

| Stage | Count |
| --- | --- |
| Baseline measured at gate entry | 249 (1 failing — §6.1) |
| After fixing the parity-test defect | 249 / 249 ✅ |
| After adding `CommSecurityBoundaryTests` | **273 / 273 ✅** |

The gate requires "250/250 or higher". **273 clears it.** The discrepancy against the stated 250 is a single test;
it is flagged rather than smoothed over because a gate report that restates unverified numbers is not evidence.

### 4.2 DI validation

`CommunicationDiWiringTests` builds the **real** container from `AddCommunicationPlatform` with `ValidateOnBuild`
**and** `ValidateScopes`, and asserts:

* the container builds;
* every public service resolves (24 types, theory-driven);
* **no hosted service is registered**;
* only the in-app channel is registered;
* principal sources cover employee / team / department but **not role**.

This is deliberately separate from `CommunicationTestHost`, which wires services by hand. CLAUDE.md is explicit
that hand-constructed services do not verify a DI graph — *"112 green tests coexisted with an application that
could not boot."*

---

## 5. Security boundary — all nine properties

New file: `CrossBuy.Tests/Communication/CommSecurityBoundaryTests.cs` — **24 tests**, consolidating the nine
required properties into one artifact a reviewer can read end to end.

Two kinds of proof, and the mix is the point: **behavioural** tests prove what the code *does*; **structural**
tests (reflection over the assembly, the DI graph and the source tree) prove what the code *cannot* do. A
behavioural test cannot prove absence, and three of the nine properties are absences.

| # | Property | Kind | Representative test |
| --- | --- | --- | --- |
| 1 | Comm permission cannot widen record access | behavioural | `Every_communication_side_advantage_combined_cannot_open_a_record_the_caller_may_not_view` |
| 2 | Denied entity ⇒ no comment, mention, attachment, timeline | behavioural | `A_caller_denied_the_entity_gets_no_comment_no_mention_no_attachment_and_an_empty_timeline` |
| 3 | Registers **no** module permission provider | **structural** | `The_registration_contributes_no_authorization_service_of_any_kind` |
| 4 | Share / follower / watch grants nothing | behavioural | `Being_made_a_participant_does_not_grant_access_to_the_record` |
| 5 | Unknown entity code fails closed | behavioural | `An_unregistered_entity_code_is_refused_by_every_capability` |
| 6 | Disabled entity surface fails closed | behavioural | `The_deny_list_beats_the_registry_flag_and_the_allow_list` |
| 7 | Company mismatch denies | behavioural | `A_caller_from_another_company_cannot_read_this_companys_thread` |
| 8 | No `CompanyID` fallback | behavioural + **structural** | `An_unresolved_company_reads_nothing_and_writes_nothing` + `No_service_contains_a_company_id_fallback_literal` |
| 9 | No Session dependency | **structural** ×3 | `No_communication_service_takes_an_http_or_session_dependency` |

Property 3 is the load-bearing one: it asserts the container contains **no** `IPlatformPermissionProvider`,
`IModuleAccessService`, `IModulePermissionAdapter`, `IPlatformRoleDirectory`, `IBootstrapAccessPolicyReader`,
`ICompanyIsolationBypass` or `IPlatformGrantWriter` — and separately that `CommAccessPolicy` *takes*
`IPlatformPermissionProvider` as a dependency. Communication asks; it never answers.

The source sweeps fail loudly if `BL/Communication` cannot be located, because an empty sweep would pass
vacuously.

---

## 6. Two real defects found and closed during this gate

Both were found **by** the gate work, in this tab's own files.

### 6.1 Schema parity test keyed off a name prefix

`CommunicationSchemaParityTests.The_models_table_list_matches_the_entities_it_actually_maps` selected
Communication entities with `TableName.StartsWith("Comm")`, plus a hand-maintained exclusion list for the email
module's `CommMessages` / `CommAttachments`.

**It broke the moment the Construction module added `CommercialRevisions` and `CommercialRevisionLines`** —
*"Commercial"* starts with *"Comm"*. The exclusion list would have had to grow for every future module that named
a table `Comm…`.

**Fix:** select by **CLR namespace** (`CrossBuy.Models.Context.Communication`). A table name is a label; the
namespace is the statement of ownership. Three sibling namespaces exist today (`…Communication`, `…Comm`,
`…Construction`) and only the first is ours. The exclusion list is gone.

### 6.2 An unscoped cross-tenant enumeration primitive

`ICommParticipationService.ResolveNotifiableAsync(long threadId, int? exceptEmployeeId, …)` took **no
`BusinessContext`** and queried `CommParticipants` filtered **only by `ThreadId`**.

Assessment, stated precisely:

* **Not a live leak.** Its single caller (`CommCommentService`) passes an already-authorized thread.
* **But a latent one.** The Comm tables are **not** covered by the platform's global query filters — they are not
  pilot entities — so nothing else constrained it. It was a public interface method that returned employee ids
  across tenants for any id a future caller supplied.

**Fix:** takes `BusinessContext`, filters on `CompanyID`, and returns empty for an unresolved company. Two call
sites in the existing suite updated. Found by property 9's positive-form test
(`Service_entry_points_take_a_resolved_business_context`) — a test written to prove a *convention* found a real
gap, which is the argument for structural tests in one sentence.

---

## 7. Gate conditions verified by direct inspection

| Condition | Evidence |
| --- | --- |
| **No production activation** | `grep AddCommunicationPlatform CrossBuy/Program.cs` → **no match**. The platform is registered nowhere in the application |
| **No hosted worker** | `CommNotificationDispatcher` is `: ICommNotificationDispatcher`, registered `AddScoped`. **No `AddHostedService`** anywhere in the Communication registration. Its file comments state a `BackgroundService` is deliberately deferred to the phase that owns production integration |
| **No UI** | No controller or view references `CrossBuy.BL.Communication`, `ICommCommentService`, `ICommThreadService` or `CommEntityRef`. `CommController` / `CommentsController` / `Views/Comm` are the **pre-existing** Comm-Hub email module (`ICommService`) and legacy comments (`IDocCommentService`) — unrelated and untouched |
| **Task / Support unregistered** | `EntityRegistry` contains 12 codes; a search for `"Task"`, `"Support"`, `"SupportTicket"` returns **0**. Preconditions for each are specified in the Entity Registry Contract §7 |
| **DocComments not migrated** | No migration script exists. `dbo.DocComments` = **0 rows** in CrossBuyDB2 and was neither read nor written |

---

## 8. Cross-tab issue for the owner: the ADR-030 collision

At gate entry, **two different documents were both numbered ADR-030**:

```
ADR-030-Communication-Platform-Architecture.md    (this tab)
ADR-030-Reporting-Platform-Architecture.md        (Reporting tab)
```

**Resolved externally during this session** — the Reporting document was renumbered to **ADR-037**, and its
companion `RPS-001` plus the Reporting source comments were updated to match. Communication retains **ADR-030…036**.

This tab did **not** perform the renumbering (Reporting Platform files are out of scope for it) and did not need
to. Recorded because:

* the collision was real and would have made both documents ambiguous in any index;
* **there is no ADR-number allocation mechanism across tabs**, so it will happen again. A single owner-held ADR
  register is the cheap fix, and it is a decision for the owner rather than for any one tab.

---

## 9. Deviations and limitations, stated plainly

1. **The integrated build is red.** Not met, Construction-owned, not worked around in the repository. §3.
2. **Communication evidence required excluding Construction's test files** — command-line only, nothing modified.
3. **The stated 250/250 baseline measured as 249.** Reported as measured. §4.1.
4. **Cross-tab test totals are unstable** (1398 → 1101 total observed within minutes) because other tabs were
   mid-write. Only the Communication figure (273/273 @ 08:22:05) is stable and owned by this tab.
5. **The SQL evidence proves the script, not a deployment.** These 14 tables exist in **no** database; CrossBuyDB2
   does not carry them.
6. **Three registry facts are not modelled** — privacy level, retention policy, per-entity audit requirements.
   Specified in the Entity Registry Contract §6 with what each would take. **The privacy ceiling must be closed
   before `Employee` is onboarded.**
7. **`@Role` remains unwired** pending reverse-membership support in `IPlatformRoleDirectory`; `EnableRoleMentions`
   defaults to false.

---

## 10. What the owner needs to decide

| # | Decision | Blocks |
| --- | --- | --- |
| 1 | Who fixes the Construction partial landing, and by when | the integrated build, for **every** tab |
| 2 | An ADR-number register across tabs | recurrence of §8 |
| 3 | Per-entity **privacy ceiling** (Registry Contract §6.1) | onboarding `Employee`, and Support |
| 4 | Retention policy + its audit interaction (§6.2/§6.3) | any regulated data |
| 5 | `Task`: does Comm own the task timeline, or does Tasks keep its own? | registering `Task` |
| 6 | `Support`: are ticket comments customer-visible? | registering `Support`; needs an external-principal ADR |
| 7 | Reverse membership in `IPlatformRoleDirectory` | `@Role` mentions |
| 8 | Approve a hosted worker under ADR-013 | notification bundling, quiet hours, dispatch drain |

---

## 11. Reproduction

```bash
X=<scratch>/exclude-construction-tests.targets     # Construction*.cs only; repo untouched

dotnet build CrossBuy/CrossBuy.csproj -c Debug   -v q --nologo   # 0 errors
dotnet build CrossBuy/CrossBuy.csproj -c Release -v q --nologo -p:BaseOutputPath=<scratch>/
dotnet build CrossBuy/CrossBuy.csproj -c TestRun -v q --nologo -p:BaseOutputPath=<scratch>/

dotnet build CrossBuy.sln -c Debug -v q --nologo                 # 16 errors, all Construction

dotnet test CrossBuy.Tests/CrossBuy.Tests.csproj -c Debug --nologo \
  -p:CustomBeforeMicrosoftCommonTargets="$X" \
  --filter "FullyQualifiedName~CrossBuy.Tests.Communication"     # 273/273
```

SQL reproduction is in the SQL-From-Empty Evidence §9.

---

## 12. Verdict

**Every condition owned by the Communication tab is met**, and two real defects in this tab's own code were found
and closed in the process — one latent cross-tenant enumeration primitive, one test whose entity selector
collided with a sibling module.

**Condition 1 is not met and cannot be met by this tab**: the shared test project does not compile because another
tab's tests landed ahead of its business layer. That is stated as RED rather than worked around, and the exclusion
used to obtain Communication's own evidence changes nothing in the repository.

**Recommendation: pass this gate for Communication, and raise the Construction partial landing as a separate,
owner-level item** — it blocks every tab's integrated build, not only this one.

**Stopping for review. No production integration performed.**
