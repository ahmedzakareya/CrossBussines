# CrossBuy — Communication Hub: Design & Implementation Plan

> Status: **Approved scope, Phase 1 in progress.** This document is the single source of truth for the
> Communication Hub. It captures the agreed scope decisions, the target architecture, the data model, and the
> phased roadmap. It follows the same convention as the other `docs/*_Analysis.md` files.

---

## 1. Scope decisions (locked)

| # | Decision | Choice |
|---|----------|--------|
| 1 | Channels | **Internal-first.** In-app notifications + real-time SignalR are the backbone for everything. |
| 2 | Email | **Real SMTP now.** Email is an *on-demand document action* (e.g. "Send this sales invoice to the customer by email"), delivered through a real SMTP server, and **logged** as a communication record. It is NOT a background notification channel. |
| 3 | Chat | **Full Teams-style chat**, internal (employee ↔ employee). v1 includes: **1:1 DMs · group channels · file attachments · @mentions · presence · typing · read receipts · reactions/edit/delete.** |
| 4 | External recipients | Only via the **email document action** (customer/vendor). No SMS / WhatsApp / Push in this scope. |
| 5 | Start point | **Phase 1 = foundation + fix the bell.** Expand the notification entity, wire the *real* bell into every layout (replace the Metronic demo), add a notifications screen + a unified approvals inbox. |

Everything else (chat, email action, announcements, document comments) builds on that Phase-1 foundation.

---

## 2. Current state (baseline, from the system audit)

**Works today:** `Notification` entity (8 cols) · `NotificationService` (81 lines) · `NotificationsHub` (`/hubs/notifications`, group `emp-{id}`) · `PosHub` (`/hubs/pos`, operational) · `NotificationsApiController` (list 50 / read / read-all) · Flutter consumer (SignalR + REST). Hub auth is **dual** (Cookie for web + JWT via `access_token` query for mobile).

**Producers:** ~25 call sites across 12 BL services, ~20 event types. Firm convention (must preserve): every notify call runs **after `CommitAsync`** and inside `try/catch { /* notifications never block the business flow */ }`.

**Gaps driving this work:**
- 🔴 4 of 11 layouts (`_LayoutBackend`, `_LayoutAccounting`, `_LayoutInventory`, `_LayoutManufacturing`) show a **static Metronic demo** bell + chat drawer — real events exist but are invisible to accountants / storekeepers / plant managers. POS lanes have no bell at all.
- 🔴 Zero external channels (no SMTP/SMS/WhatsApp/Push anywhere in the codebase).
- 🔴 `Notification` entity is thin: no `CompanyID` (breaks tenant isolation), no `Url` (no click-through), no actor/priority/category/dedup/expiry/readAt.
- 🟠 Targeting is employee-only; `NotifyRoleAsync` supports only `acc` + `inv` scopes.
- 🟠 No delivery guarantees (failures swallowed), per-recipient `SaveChanges` in loops (N DB round-trips).
- 🟠 No real "hub": no chat backend, no announcements, no document comments, **no unified approvals inbox** (approvals scattered across 4 silos: LeaveApprovalStep · EmployeeRequestStep · InventoryApproval · Pricing-D discount approval), no menu entry.
- 🟡 Type strings inconsistent (3 naming styles), no catalog. `Program.cs` middleware ordering is fragile; `CompanyId = 1` hard-coded in `CrmReminderHostedService`.

---

## 3. Target architecture

```
Producers (12 BL services, after-commit + try/catch)
        │  NotifyAsync / NotifyAudienceAsync(catalog type, url, priority, dedup)
        ▼
NotificationService  ──►  Notifications table (expanded, CompanyID-scoped, batched insert)
        │                         │
        │ realtime push           │ fetch-on-load (REST)
        ▼                         ▼
NotificationsHub (emp-{companyId}-{empId})     Bell (shared partial) in ALL layouts
        │
        ├──► Web (Cookie)   ── shared _NotificationBell.cshtml + _CommsHub.js
        └──► Mobile (JWT)   ── existing Flutter provider

Chat  ──►  ChatHub (/hubs/chat)  ──►  Conversation / ConversationMember / ChatMessage / ChatReceipt / ChatReaction
Email ──►  EmailService (SMTP)   ──►  CommMessage (outbox + log) + document action buttons
Approvals ──► ApprovalInbox (read-model union over the 4 existing silos) — no new writer
```

Design rules carried from the existing system:
- **After-commit + try/catch** notify convention is preserved unchanged.
- **CompanyID on every row** (tenant isolation) — Hub groups become `emp-{companyId}-{empId}`.
- **Idempotent `deploy/sql/*.sql`** for every structural change (migrations are broken in this project).
- **Bilingual `*Ar/*En`** fields; culture-aware display via `CurrentUICulture`.
- **Metronic-only** UI; reuse the chat drawer markup already present in the 4 layouts.
- **`DedupKey`** mirrors the existing `TaskAutoLog.RuleKey` pattern to prevent duplicate notifications.

---

## 4. Data model

### 4.1 `Notification` — expanded (additive, all new columns nullable)
Existing: `ID, RecipientEmployeeID, TitleAr/En, BodyAr/En, Type, RefId, IsRead` (+ BaseEntity `CreatedAt/By`).
Add:
| Column | Purpose |
|--------|---------|
| `CompanyID int?` | tenant isolation (scopes queries + Hub group) |
| `BranchID int?` | optional branch scope |
| `Url nvarchar(400)?` | deep-link for click-through (generic navigation) |
| `Priority nvarchar(20)?` | `Normal` / `High` / `Critical` |
| `Category nvarchar(40)?` | module bucket (HR / Sales / Inventory / Accounting / CRM / Governance …) |
| `ActorEmployeeID int?` | who caused the event |
| `DedupKey nvarchar(120)?` | idempotency (skip if an unread row with same key exists) |
| `ExpiresAt datetime2?` | auto-hide stale alerts |
| `ReadAt datetime2?` | when it was read |
| `Icon nvarchar(60)?` | optional KI icon override |

### 4.2 Notification type catalog (`NotificationTypes` constants)
One canonical string per event, grouped by module, each mapped to a default `Category`, `Icon`, `Priority`, and a URL builder. Replaces the 3 ad-hoc naming styles. Existing strings kept as aliases so history still renders.

### 4.3 Chat (Phase 3)
- `Conversation` (Id, CompanyID, Kind = Direct|Group|Channel, Title, CreatedBy, CreatedAt, LastMessageAt)
- `ConversationMember` (ConversationId, EmployeeId, Role = Owner|Member, LastReadMessageId, MutedUntil, JoinedAt)
- `ChatMessage` (Id, ConversationId, SenderEmployeeId, Body, AttachmentId?, ReplyToId?, EditedAt?, DeletedAt?, CreatedAt)
- `ChatReceipt` (MessageId, EmployeeId, DeliveredAt, ReadAt)  — or derived from member `LastReadMessageId`
- `ChatReaction` (MessageId, EmployeeId, Emoji)
- Presence + typing are **transient** (Hub-only, not persisted).

### 4.4 Email / communications log (Phase 4)
- `CommMessage` (Id, CompanyID, Channel = Email, ToAddress, Cc, Subject, Body, EntityType, EntityId, Status = Queued|Sent|Failed, Error, AttachmentId?, SentAt, CreatedBy) — the outbox + audit log for every email sent.
- `EmailService` wraps `System.Net.Mail.SmtpClient` (or MailKit) reading SMTP config from `appsettings` / env vars (secrets convention). Sends synchronously on the document action, records a `CommMessage`, retries via a hosted dispatcher on failure.

---

## 5. Audiences (targeting)
A single `ResolveAudienceAsync(spec)` that unions employee ids from:
- a single employee, an explicit list;
- **role scopes** — extend beyond `acc`/`inv` to `crm` (CrmUserRole), `pos` (BranchUserRole), Identity roles;
- **manager chain** (reuse the existing `ManagerChainAsync` logic — lift it into a shared helper);
- **org unit / hierarchy** (deputy-aware), **project team** (ProjectId), **branch**, **brand**.
External recipients (customer/vendor) are reached **only** through the email document action, never the bell.

---

## 6. Phased roadmap

| Phase | Deliverable | Notes |
|-------|-------------|-------|
| **P1 — Foundation + bell** *(in progress)* | Expand `Notification` (+SQL) · type catalog · `NotificationService` upgrade (CompanyID scope, Url, **batch insert**, dedup, audience helpers) · **shared real bell partial wired into all 11 layouts** (replace demo) · notifications **index screen** (search/filter/paging) · **unified approvals inbox** (read-model over the 4 silos) · menu entry. | Fastest visible value. Keeps after-commit/try-catch. Back-compat `NotifyAsync` overload so the 25 producers compile unchanged. |
| **P2 — Notification depth** | Click-through everywhere (per-type URL builders) · user preferences / mute / digest · Outbox + retry (hosted dispatcher, own DbContext scope) · normalize the ~20 producer type strings to the catalog. | Removes silent-loss + fragile shared-DbContext risk. |
| **P3 — Chat (Teams-style)** | `ChatHub` + chat data model · DMs · group channels · attachments (reuse `Attachment`) · @mentions → notification · presence / typing / read receipts · reactions/edit/delete · reuse the Metronic chat drawer in the 4 layouts + a full chat screen. **UI = clone Metronic demo39 chat exactly** (`.../demo39/apps/chat/private.html` DM · `group.html` group · `drawer.html` drawer — mandatory, user-supplied reference). Message IN = `bg-light-info` start / OUT = `bg-light-primary` end; messenger `#kt_chat_messenger` + contacts `#kt_chat_contacts`. | Largest phase. Mobile consumes the same hub. |
| **P4 — Email document action** | `CommMessage` + `EmailService` (real SMTP) · "Send by email" buttons on sales invoice / statement / quotation / PO / payment advice · templates (AR/EN) · communications log per party. | Real external delivery, on-demand only. |
| **P5 — Hub extras** | Company/branch announcements · document comments/timeline (generalize `Crm.Activity`) · mentions inbox · audit log of communications. | |

---

## 7. Technical risks to fix along the way
- **Outbox, not fire-and-forget** — the current `catch {}` swallows insert failures ⇒ silent loss. P2 adds an outbox + retry with a dedicated DbContext scope (the request-scoped context is only safe today because every call is post-commit).
- **`Program.cs`** — duplicated `UseRouting()` / `UseAuthentication()`, `MapHub` before `UseAuthorization()`, trailing `UseHttpsRedirection()`. Tidy the pipeline before adding `ChatHub` (a new hub will expose the fragility).
- **CORS** `AllowAnyOrigin()` conflicts with credentialed SignalR if cross-origin cookies are ever needed.
- **Hard-coded `CompanyId = 1`** in `CrmReminderHostedService` — any new dispatcher must resolve company properly.
- **Hub groups** must include CompanyID (`emp-{companyId}-{empId}`) for isolation.

---

## 8. Out of scope (this initiative)
SMS · WhatsApp · mobile push (FCM/APNs) · voice/video calls · external chat federation. These can layer onto the same `CommMessage`/audience foundation later if ever needed.