# Stage-Construction-14 — Mobile, Site and UX Contract

Covers Phase 22 (mobile/site requirements) and the Design and UI Rule. **This is the construction UX contract of
record.** No UI is built in this increment.

---

## 1. The theme-first UI rule (binding on every future construction screen)

> **No final UI implementation in this increment.** And for every construction screen built later:
>
> 1. **Do not invent the design independently.**
> 2. **Request the approved Metronic/theme reference first** — for that screen type, before any markup is written.
> 3. **Use the supplied theme screen or component as the design source.** The approved reference is the input, not a
>    later correction.
> 4. **Preserve CrossBusiness Platform visual identity.**
> 5. **Do not create a speculative replacement component where an approved theme component exists.**

### 1.1 What "preserve the visual identity" means concretely

- The Metronic 8 `app-*` shell, as Accounting uses it — Accounting is the platform's visual identity reference for new
  administrative and commercial tools.
- The brand palette **via Metronic tokens**, not hand-picked hex values.
- **RTL and LTR parity.** Arabic is a first-class layout, not a mirrored afterthought. Every screen is reviewed in both.
- **Every user-facing string through Resources** with `@Localizer` and ar/en/fr resx — construction takes the strict
  convention, not the looser bilingual-literal convention some platform modules use.
- Numbers, dates and currency through the platform format tokens.

### 1.2 Known open item, carried not resolved

There is a **documented contradiction** in the platform's own design record about the brand colour: the code overrides
Metronic's blue with the ledger green + gold identity on purpose, while one design document asks to "preserve the
CrossBuy blue identity". This tab does **not** resolve it and does not pick a colour. Construction screens will follow
whatever the approved theme reference supplies at build time. **The owner's decision is required before the first
construction screen is styled.**

### 1.3 Domain-UX exception

Specialised operational surfaces — the site store, the mobile site capture screens, a future site kiosk — keep their
domain UX, exactly as POS, the kitchen display and the manufacturing floor do. The exception is about **layout density
and interaction**, not about inventing components: those screens still draw their controls from the approved theme.

### 1.4 Screens this rule governs

All 32 in `Stage-Construction-17-Screen-Inventory.md`. That document restates the rule so it cannot be read without it.

---

## 2. Mobile and site requirements

The mobile app today has 10 screens — `dashboard`, `finance`, `home`, `inventory`, `leave`, `login`, `notifications`,
`profile`, `statistics`, `structure` (`crossbuy_mobile/lib/screens/`) — and **none of them is project or site related**.
So the construction mobile surface is entirely greenfield, which is an advantage: the contract below can be set before a
single client exists.

### 2.1 Required capabilities

| Capability | Requirement |
|---|---|
| **Offline-capable draft** | Draft creation and editing offline for: daily site report, inspection request, RFI, punch item, material request, site material receipt. **Drafts only** — see §2.3. |
| **Photo capture** | Attach photos to a DSR line, punch item, inspection or NCR. Uploaded to the platform file store (`LibraryItem`); the construction row stores `LibraryItemId` only. |
| **Location capture** | **Only where approved** (§3). Attached to a photo or a site check-in, never continuous. |
| **Signature** | Inspection sign-off, material receipt acknowledgement, handover snag verification. Stored as an image in `LibraryItem` plus the signing employee id and timestamp. |
| **Barcode / QR scan** | Item lookup on receipt and issue; asset lookup for an equipment log. |
| **Quick material receipt** | Confirm a delivery to site against a transfer or PO with quantity and photo. |
| **Daily site report** | Full structured capture (`Stage-Construction-07` §3), including manpower by trade and quantity lines against WBS/BOQ. |
| **Inspection** | Raise, perform, record result, sign, photo. |
| **RFI** | Raise with photo and drawing reference; read the answer. |
| **Punch list** | Create, assign, verify, close with photographic evidence. |
| **Task handoff** | Reassign a site item to another person — reuses the Tasks module when that integration exists; **not** a construction task engine. |
| **Sync conflict handling** | §2.4. |

### 2.2 What mobile must **not** do

Post a certificate · approve a variation · post a journal entry · post a stock movement while offline · release
retention · edit a BOQ · approve a budget. Anything that reaches the GL or stock, or that changes a commercial value,
requires an online request through the normal server-side gate.

### 2.3 Offline policy (decision **D-14**, recommendation: draft-only)

1. **Drafts are local; postings are online.** A document may be *composed* offline and is *submitted* only when
   connected. Nothing offline touches money or stock.
2. **Every offline document carries a client-generated idempotency key** (device id + local sequence + created-at), and
   the server treats a repeat key as the same document. This is the single control that prevents the duplicate-site-data
   failure (CR-18).
3. **Reference data is cached with an explicit staleness stamp** — project, WBS, BOQ items, item list, current document
   revisions. The UI shows the cache age; a document composed against stale references is flagged on submit.
4. **Superseded drawings must be visible as stale.** A cached revision that has been superseded since sync shows as
   stale, never as current (`Stage-Construction-08` §3.4).
5. **No offline deletion.** An offline-created draft may be discarded locally before submission; nothing already
   submitted can be deleted from the device.

### 2.4 Sync conflict handling

| Situation | Behaviour |
|---|---|
| Same idempotency key resubmitted | Server returns the existing document; no duplicate. |
| Server-side change to the same record while offline | The submission fails with a **structured conflict** (`Stage-Construction-11` §5.3) and the app shows both versions field by field. No automatic merge. |
| Reference data changed (BOQ revision superseded, WBS node archived) | Submission is accepted into `Draft` but flagged `RequiresRevalidation`; a reviewer must re-point it before approval. |
| Duplicate DSR for the same site and date | Rejected as a duplicate with a link to the existing report, offering an **amendment** instead. |
| Clock skew on the device | Server records both the device-reported time and the server receipt time; the server time is authoritative for any deadline. |

---

## 3. Privacy and permission concerns (recorded, as required)

| Concern | Position |
|---|---|
| **Location capture** | Opt-in, purpose-limited, per-event. Recorded only when attached to a specific document (photo, receipt, site check-in). **No background tracking, no continuous trace, no movement history.** A site geofence may confirm "at site / not at site" for a check-in; it must not become an attendance surveillance mechanism — attendance is HR's, with HR's own consent basis. |
| **Photos of people** | Site photos routinely contain workers. Photographic evidence is attached to work, not to persons; a photo must not be used to identify or evaluate an individual. Access follows the document's confidentiality. |
| **Manpower data** | A subcontractor's workers are recorded as a **trade headcount**, not as named persons (`Stage-Construction-07` §4) — deliberately less personal data, not more. |
| **Cost rates on a device** | An employee's cost rate must **never** be cached on a mobile device. A manpower log captures hours; costing happens server-side under `cost.confidential`. |
| **Commercial data on a device** | Client rates, subcontractor rates and margins are not synced to mobile at all. The site needs quantities, not values. |
| **Device loss** | Local drafts are the only construction data at rest on the device; they must be encrypted at rest and cleared on sign-out. |
| **Signatures** | A stored signature is personal data: retained only as evidence for the document it signed, never reused for another document. |
| **Offline audit** | Every offline-composed document records device id, local created-at and server received-at, so an audit can distinguish "recorded late" from "backdated". |

## 4. Definition of done for any future construction UI work

1. The approved theme reference for that screen type has been **requested and supplied** — not assumed.
2. Every string is in Resources (ar/en/fr).
3. RTL and LTR both reviewed.
4. Every action is gated **server-side** by the construction permission (`Stage-Construction-11`); hiding a control is
   never the control.
5. Confidential columns are absent from the payload when the caller lacks the right — not merely hidden in the view.
6. No new component where an approved theme component exists.
7. For mobile: the idempotency key, the staleness stamp and the conflict UI exist before the first document type ships.