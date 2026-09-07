# CORRECTION-004 — the permission-coverage basis was wrong in both directions

**Raised by:** Stage 1 Batch B / B6 (regenerate and reclassify the backlog).
**Supersedes the FIGURE accepted in CORRECTION-003**, not its method.
**Headline: the backlog is 193, not 189.**

| Measure | CORRECTION-003 (accepted) | CORRECTION-004 (measured) |
|---|---|---|
| Mutating actions | 384 | **384** (unchanged) |
| Protected by a module permission at any level | 195 | **151** |
| Authorized in-body by an access service (not by any attribute) | *not measured* | **40** |
| Backlog — no authorization the scan can see | 189 | **193** |

---

## 1. What was wrong

`scan-architecture.ps1` treated two attributes as module permissions. Neither is one.

### 1.1 `PosLaneActivityGuard` is not a permission — it is a lane-compatibility check

`Models/PosLaneActivityGuardAttribute.cs` reads the POS session, looks up the branch's `ActivityPresetCode`, and
redirects if the branch does not belong to the lane (restaurant vs hypermarket). Three facts decide it:

* it checks **no role** — `IPosAccessService.IsActivityAllowedForLane(code, lane)` compares a branch's activity
  preset against a lane, not a person against a right;
* when the session is **absent or malformed** it calls `next()` — so it does not even require authentication;
* when `branchId` is 0 it calls `next()` too.

Counting it credited **44 mutating POS actions** (36 `PosAppController` + 8 `HyperPosController`) as
"protected by a module permission at any level". They were not.

### 1.2 `DevOnly` is an environment gate

It matched 0 mutating actions, so it changed no number. It was still wrong to have on a permission list, and it is
removed so the next controller to carry it is not silently credited.

## 2. What was ALSO wrong — in the opposite direction

Removing the lane guard would move all 44 into the backlog and report 233. That would be its own false finding,
because **40 of the 44 authorize themselves in the method body**:

```csharp
var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, ... });
if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed..."] });
```

An attribute scanner cannot see that. So the scanner now measures it: for each mutating action with no permission
attribute, it inspects the method body — cut at the next member declaration, capped at 40 lines — for a call to an
access service (`_access.Can*/Is*/Has*`, `CanAsync`, `RolesAsync`) and reports `in_body_authorization` as a new
column and a new summary line.

**Three categories are now printed instead of two**, because the two-category split was being read as the security
position and it is not:

```
mutating WITH a permission ATTRIBUTE at any level: 151
mutating WITHOUT a permission attribute         : 233
  ...of which authorized IN-BODY (access svc)   : 40
  ...with NO authorization this scan can see    : 193   <-- the backlog
```

## 3. The four actions the correction actually adds

44 lane-guarded mutating actions − 40 with an in-body role check = **4** that hold a session check only:

| Action | What it does | Assessment |
|---|---|---|
| `PosAppController.AddCustomer` | **creates a `Customer`** — a B2 pilot entity | The material one. Any POS operator, regardless of role, can create a customer with an AR control account. |
| `PosAppController.Start` | terminal-selection screen | Low: selection only, and a POS session is required. |
| `HyperPosController.Start` | terminal-selection screen | Low, as above. |
| `HyperPosController.PriceCheck` | price lookup | Low: read-shaped, though declared `POST`. |

All four require a POS session (`Ctx()` non-null), so none is anonymous. The gap is **role**, not authentication.

## 4. A measurement error made and corrected inside this batch

The first in-body pass, written ad hoc, reported **41** in-body-authorized actions and concluded that
`233 − 44 = 189` — i.e. that the accepted figure survived. That was wrong twice over: the arithmetic used 44 where
its own measurement said 41, and the measurement itself was inflated because its 25-line window ran past the end of
`AddCustomer` into `DiscardOrder`'s `_access.CanOrder` on the next line but one.

The scanner's member-boundary window gives **40**, and `AddCustomer` is genuinely unchecked. Recorded because R7
requires measurement corrections to be disclosed, including the ones made while producing the measurement.

## 5. Effect on the maturity model

**None.** No maturity score changes: the backlog was already scored as a gap, and 193 vs 189 does not cross a
10-point step in the Permission dimension. Per the standing instruction, the immutable stage records are preserved
and only the **explanation/basis** is updated — Stage-003's basis line now cites CORRECTION-004 for the figure.

## 6. Files

| File | Change |
|---|---|
| `CrossBuy/deploy/scan-architecture.ps1` | `$permissionAttributes` corrected; `PosLaneActivityGuard`/`DevOnly` moved to `$authAttributes`; `in_body_authorization` measured and emitted; three-category summary |
| `docs/architecture/evidence/Permission-Coverage.csv` | new `in_body_authorization` column |
| `docs/architecture/evidence/Permission-Backlog-B6.csv` | the 193, regenerated |
| `docs/platform/Stage-001-B6-Permission-Backlog-Reclassification.md` | the risk classification |
