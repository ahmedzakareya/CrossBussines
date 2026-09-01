# Missing-Test Inventory

Every test file present on the development machine and absent from canonical Git, with its
classification. Mechanical facts (size, hash, namespace, class, test count, non-hermetic flags)
were extracted by script; the classification is the judgement.

**Total machine-only files: 106.** `A + B + C + D + E = 66 + 0 + 2 + 0 + 38 = 106`.

## Totals

| Class | Files | Test methods | Meaning |
|---|---|---|---|
| **A — recovered** | 66 | 918 | protects behaviour canonical HEAD has; compiles; passes |
| **B — duplicate** | 0 | 0 | measured, not assumed: no machine-only file declares a class name that already exists in tracked tests |
| **C — obsolete** | 2 | 22 | targets a superseded implementation |
| **D — experimental** | 0 | 0 | measured: none is a prototype, benchmark or generated artifact |
| **E — blocked** | 38 | 458 | valid-looking, but blocked on untracked production code, signature drift, or an unsatisfied contract |

## Class A — recovered (66)

Committed in `ccdda6b`. They add **1200 tests**: 2540 -> 3740, zero failures.

## Class C — obsolete (2)

* `CommOutboxConcurrencyTests.cs` (10 tests) — targets the SUPERSEDED CrossBuy.BL.Comm outbox; canonical HEAD uses CommNotificationDispatcher
* `Slice3CommOutboxTests.cs` (12 tests) — targets the SUPERSEDED CrossBuy.BL.Comm outbox; canonical HEAD uses CommNotificationDispatcher

The dispatcher they exercise is not in canonical HEAD and is superseded there. Resurrecting it to
make them compile is exactly what the brief forbids. `SqlServerFixture.cs` was recovered WITHOUT its
15-line factory for that store: the other 604 lines are generic probe infrastructure nineteen
recovered suites depend on, and losing them with it would have been the worse trade.

## Class E — blocked (38)

### E1 — untracked production slice (5 files, 68 tests)

* `ConstructionC1BoqIdentityTests.cs` (10 tests)
* `ConstructionC1CommercialRevisionTests.cs` (7 tests)
* `ConstructionC1ConcurrencyAndAuditTests.cs` (9 tests)
* `ConstructionC1SubcontractCapTests.cs` (10 tests)
* `ConstructionTestFixture.cs` (0 tests)

`CrossBuy/BL/Construction/**` and `CrossBuy/Models/Context/Construction/**` are untracked production
code from another work stream. Canonical HEAD neither references nor needs them. **Decision required:
the owning tab commits the slice, or these are reclassified obsolete.**

### E2 — constructor-signature drift (13 files)

* `BatchC1HubAndHierarchyTests.cs` (16 tests)
* `BatchCAccessServiceTests.cs` (40 tests)
* `D1Wave1HrPosGateTests.cs` (18 tests)
* `EntityRegistryTests.cs` (5 tests)
* `PlatformGrantWriterAcceptanceTests.cs` (17 tests)
* `PlatformGrantWriterConcurrencyTests.cs` (5 tests)
* `Slice2ProducerTests.cs` (9 tests)
* `Slice2RegistryTests.cs` (9 tests)
* `Slice3QuotationCommentTests.cs` (10 tests)
* `Stage1F4DatabaseProofTests.cs` (12 tests)
* `Stage1NotificationAuthorizationTests.cs` (9 tests)
* `Stage1PermissionTests.cs` (22 tests)
* `TasksCalendarTransitionAndEventTests.cs` (17 tests)

`HrAccessService` gained `log`, `CrmAccessService` gained `policies`, `EntityRegistry` and
`BusinessEventService` changed argument types, `ChatHub` changed arity. The BEHAVIOUR under test
still exists; only the wiring moved. These are the strongest remaining recovery candidates and were
left for a batch that can adapt 13 call sites carefully rather than quickly.

### E3 — compiles, but the asserted contract is not met today (20 files)

`Stage1ContextTests` is the newest entry and the most instructive. It PASSES against a tree where
InventoryApprovalService is fixed and FAILS against canonical HEAD, because the fix exists only as
foreign working-tree work. It found the defect, and it stays out until the owning tab lands their
version — a test that is right about a fix nobody has committed is blocked, not green.


* `AccessDeniedGuardTests.cs` (9 tests)
* `B6BootstrapConversionTests.cs` (24 tests)
* `Batch00EvidenceGuardTests.cs` (6 tests)
* `BootstrapPolicyStorageAndReaderTests.cs` (25 tests)
* `CompanyDefaultParameterGuardTests.cs` (7 tests)
* `D1Wave1GateTests.cs` (16 tests)
* `Imp003RelationalModelTests.cs` (3 tests)
* `NewScreenReachabilityTests.cs` (9 tests)
* `PlatformPermissionVocabularyTests.cs` (17 tests)
* `PlatformSchemaDeploymentTests.cs` (9 tests)
* `ReportingDeploymentGovernanceTests.cs` (7 tests)
* `ReportingDiWiringTests.cs` (9 tests)
* `ReportingSchemaDeploymentTests.cs` (11 tests)
* `SqlCompanionGuardTests.cs` (30 tests)
* `Stage1PermissionBacklogTests.cs` (6 tests)
* `Stage1RawSqlSafetyTests.cs` (7 tests)
* `SystemEntryPointNavigationTests.cs` (11 tests)
* `UiConformanceTests.cs` (10 tests)
* `WorkspaceHomeUiTests.cs` (6 tests)

Each was RUN, not guessed at. The most consequential:

* `AccessDeniedGuardTests` (9 failures) needs `Views/Account/AccessDenied.cshtml`, which is untracked
  production code — the same shape as E1.
* `CompanyDefaultParameterGuardTests` correctly reports that `Api/DevSeedController.cs` carries
  company parameter defaults. Its own header records that increment 4.1 cleared them by introducing
  `DevSeedFixture.DefaultCompanyId` — and **`CrossBuy/Models/DevSeedFixture.cs` is untracked**. The
  remediation exists only on this machine, which makes canonical HEAD worse than the developer tree.
  Landing it means changing 69 parameter defaults in a 15k-line SHF-28 shared file: an owner decision,
  not a stabilization edit.
* Several are frozen-count ratchets (`Batch00EvidenceGuardTests`, `Stage1PermissionBacklogTests`)
  whose baselines predate later platform work. They need re-freezing against measured current state —
  the same operation `Correction005StructuralTests` demanded and got.

## Non-hermetic scan of all 106

```
absolute developer path      0
production connection string 0
azure hostname               0
bearer/api-key literal       0
env var read                13   (CROSSBUY_TEST_SQL and similar - the documented probe seam)
SQL probe fixture           17   (own probe database per run, created and dropped)
file I/O                    33   (all repo-relative source reads by the structural guards)
```

Two apparent hits in the recovered set were examined and are protective rather than dangerous: a
deliberately unreachable `Server=localhost,14331;...Password=nothing` proving a runtime gate fails
closed, and `[InlineData("CrossBuyProd")]` in a test asserting the seeder REFUSES production-looking
database names.
