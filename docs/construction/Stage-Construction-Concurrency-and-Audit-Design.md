# Stage-Construction — Concurrency and Line-Level Audit Design

Covers C1 sections 5 (concurrency) and 6 (line-level audit).

---

## 1. Where the module started

Measured before C1, across `deploy/sql/boq.sql` and every `deploy/sql/projects_p*.sql`:

| | Count |
|---|---|
| `ROWVERSION` / `timestamp` columns | **0** |
| `CHECK` constraints | **0** |
| `FOREIGN KEY` / `REFERENCES` | **4** (all line→header) |

History consisted of `CreatedBy`/`CreatedAt` and, on postable documents, `PostedBy`/`PostedAt`. So two quantity
surveyors editing the same measurement meant the second save won silently, and a rate that changed yesterday could
not be shown as having changed, by whom, or why.

## 2. The concurrency token — and why it is not `rowversion`

C1 uses an **application-rotated `varbinary(16)`** configured with EF's `IsConcurrencyToken()`, not SQL Server's
native `rowversion`.

**Why.** The test suite runs on SQLite — the only in-process provider with real transactions, which is why
`PlatformTestHost` chose it and why `ConstructionTestFixture` follows. SQLite has no `rowversion`. A native token
would therefore leave every stale-token test either skipped or fake, on the platform the suite actually uses. A
rotated token behaves identically on both providers, so the tests in §4 are **real tests**.

**How it works.** The owning service assigns a fresh token on every write:

```csharp
public static byte[] NewToken() => Guid.NewGuid().ToByteArray();
```

and compares the caller's presented token before writing:

```csharp
if (!ConstructionConcurrency.Matches(expectedToken, entity.ConcurrencyToken))
    return (false, ConstructionConcurrency.ConflictMessage);
```

`Matches(null, …)` returns true — a caller that presents no token is not requesting a check. That is deliberate:
the legacy BOQ editor posts no token, and forcing one would have broken the existing screen. Callers that hold a
token (the C1 API, and every future C-phase screen) get the check.

**The refusal is one message everywhere:**

> تم تعديل هذا السجل بواسطة مستخدم آخر — أعد التحميل وقارن قبل الحفظ
> *(this record was changed by another user — reload and compare before saving)*

Never "save failed". The user-facing contract of a concurrency failure is *reload and compare*, because the other
user's work is not wrong — it is just newer.

## 3. Which entities carry a token, and which do not

| Entity | Token | Rationale |
|---|---|---|
| `BoqLineStates` | ✔ | The satellite is where a BOQ line's commercial decisions live, so it is the aggregate the token belongs on |
| `CommercialRevisions` | ✔ | Two approvers must not both apply one revision |
| `SubcontractScopes` | ✔ | The cap must not be moved from a stale read |
| `SubcontractCertificateLines` | ✔ | |
| `ClientContracts` | ✔ | |
| `BoqItems`, `ProgressBillings`, `ProgressBillingLines`, `VariationOrders`, `SubcontractBillings` | ✖ | **Deliberate, and a declared gap** — see §6 |

## 4. Concurrency tests (`ConstructionC1ConcurrencyAndAuditTests.cs`)

| # | Test | Proves |
|---|---|---|
| 1 | `A_stale_boq_line_token_is_rejected_and_the_first_write_survives` | A wins, B is refused, A's value is what the database holds |
| 2 | `The_token_rotates_on_every_write` | tokens differ after a write; replaying the old one fails |
| 3 | `A_stale_scope_token_is_rejected` | the cap cannot be moved from a stale read |
| 4 | `A_stale_revision_token_is_rejected_on_approval` | revision stays `Draft`; **nothing applied to the BOQ** |
| 5 | `Two_separate_contexts_writing_the_same_scope_produce_a_reported_conflict` | two independent `DbContext`s over one database — a genuine race, not a simulated one |

Test 5 matters most: it uses `SecondUser()`, which builds a **separate context and a separate service instance** over
the same SQLite database. A conflict proved inside one change tracker would prove nothing.

## 5. Line-level audit

`ConstructionAuditEntries` — append-only, field-level.

| Field | Note |
|---|---|
| `EntityType`, `EntityId`, **`LineId`** | line-level, not just header |
| `FieldName`, `OldValue`, `NewValue` | text form |
| `OldNumeric`, `NewNumeric` | numeric mirrors, so a report can aggregate a change without parsing text |
| `ChangeKind` | `Created`/`Updated`/`Retired`/`Approved`/`Posted`/`Reversed`/`Cancelled`/**`CapRaised`**/`Restructured` |
| `Reason` | **required for any commercial field** |
| `ActorEmployeeId`, `ActorUserId`, `OccurredAt` | who and when |
| `SourceContext` | the service that produced it |
| **`CorrelationId`** | one user action stays recognisable as one action across the rows it produced |
| `RevisionId` | the commercial revision in force |

**Three properties by construction, not convention:**

1. **Append-only.** `IConstructionAuditService` exposes `Record` and three reads — no update, delete, amend or purge.
   Asserted *structurally* by reflection over the interface, because a future update method would silently
   invalidate every other audit assertion in the suite, and a test that reads the API surface is what catches that.
   The entity itself carries no `UpdatedAt`, `UpdatedBy` or `DeletedAt` — also asserted.
2. **Written in the caller's transaction.** `Record` only *stages* rows; the caller's `SaveChanges` commits the
   change and its history together. An audit row that could be lost independently of its change would make the
   history look complete when it is not.
3. **A reason is mandatory for a commercial value.** Enforced in the audit service against a field list
   (`Quantity`, `UnitPrice`, `SubRate`, `AssignedQuantity`, `CappedQuantity`, `ContractValue`, …), so a *new* caller
   cannot forget it. The services also refuse an empty reason up front, so the user gets a clear message rather than
   an exception.

**Unchanged values are recorded too.** Approving a revision writes both `Quantity` and `UnitPrice` for every line,
even where the value did not move — so the record shows what was *considered*, not only what changed. Test:
`Audit_records_old_and_new_values_source_and_correlation` asserts 4 rows for 2 lines and a single correlation id.

## 6. Declared gap — the base tables carry no token yet

`BoqItems`, `ProgressBillings`, `ProgressBillingLines`, `VariationOrders` and `SubcontractBillings` have **no**
concurrency token after C1.

**Why not.** Adding one means an `ALTER TABLE` plus an EF mapping change. This increment may not execute SQL, and
between shipping the mapping and the owner applying the script, EF would reference a non-existent **column** and
break every read of the existing Projects screens — for three other tabs working in the same tree. A missing new
**table** only breaks the new C1 paths; a missing **column** breaks everything.

**What protects those rows in the meantime.** Every commercial decision about a BOQ line now flows through
`BoqLineStates` (token) or `CommercialRevisions` (token), and both are checked before the base row is written. So a
concurrent *commercial* change is caught. A concurrent *descriptive* edit (two users retyping a description with no
token presented) is not — and that is the honest limit of C1.

**Consolidation step, for the owner to schedule:**

1. Apply `construction_c1_commercial_foundation.sql`.
2. Run the backfills in its §8 after the mapping measurement.
3. A later increment adds `ROWVERSION` to the five base tables, maps them, and folds
   `BoqLineStates`/`CertificateLineSnapshots` into their base tables — at which point the satellites become
   history tables rather than live state.

Until step 3, the statement "concurrency is enforced" is true of the **commercial decision path** and not of every
column on every legacy row. Stated here rather than left to be discovered.

## 7. Database-level integrity added by C1

The C1 DDL does what the pre-C1 construction schema never had — for its own tables:

| Kind | Examples |
|---|---|
| `FOREIGN KEY` | 14 across the seven new tables, including `BoqLineStates → BoqItems`, `SubcontractCertificateLines → SubcontractScopes`, `CertificateLineSnapshots → ProgressBillingLines` |
| `CHECK` | status domains; `AssignedQuantity > 0`; `ApprovedVariationQuantity >= 0` **and** must name a variation when non-zero; a retired line must carry `RetiredAt` and a reason; an approved revision must carry `ApprovedAt` and a `Reason`; a variation-sourced revision must name its variation; `CurrentQuantity <> 0`; a negative certificate movement must name the line it adjusts; `CumulativeQuantity >= 0` |
| filtered `UNIQUE` | one primary contract per project; one draft revision per project; one state row per BOQ line; one snapshot per certificate line |

Two of those checks encode business decisions directly in the schema, so the rule survives a future caller that
forgets it: `CK_SubcontractScopes_VariationQty` (D-07 — the cap moves only with a variation) and
`CK_CommercialRevisions_Approval` (a commercial change with no stated reason is refused).
