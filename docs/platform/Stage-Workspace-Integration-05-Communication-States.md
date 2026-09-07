# Stage-Workspace-Integration — 05 · Communication States (Phase 5) + Mark-as-Read Handover (Phase 6)

**Product:** CrossBusiness Workspace · **Tab:** TAB 3
**Screens:** `/Workspace/Mentions` · `/Workspace/Notifications`

---

## 1. Consumption-only, and no activation as a side effect

`AddCommunicationPlatform` is **not** called by the Workspace, and this increment does not add it.

`ICommMentionService` is resolved with `GetService` (nullable), never `GetRequiredService`. When the platform
is not registered the Workspace reports it and carries on.

**Why this matters.** Communication's activation is that platform's own gated decision, taken in its own
integration gate. A screen must not force another platform into production as a side effect of being rendered.
When Communication IS activated, Mentions starts working **with no change to the Workspace**.

The Workspace also performs **no** Communication write: no comment, no mention, no read-marking, no
participation.

---

## 2. The five required states — all distinct

| # | Required | Panel state | Trigger | What the user sees |
| --- | --- | --- | --- | --- |
| 1 | active with data | `Ready` | rows returned | the list |
| 2 | active and empty | `Empty` | answered, nothing | quiet grey *"No one has mentioned you yet"* |
| 3 | platform not activated | `Unavailable` | `ICommMentionService` unresolved | **accent** stripe + actionable reason |
| 4 | access denied | `AccessDenied` | no company, or no employee | **neutral** stripe — nothing is broken |
| 5 | temporary failure | `TemporaryFailure` | the service threw | **warn** stripe, invites a retry |

A sixth, `PartiallyAvailable`, exists for panels with several sources; Mentions has one, so it cannot occur
there.

### 2.1 They are rendered differently on purpose

`Views/Shared/_WorkspacePanelState.cshtml` is the one place these are drawn, so they cannot drift into looking
alike:

| State | Stripe | Icon | Reasoning |
| --- | --- | --- | --- |
| Unavailable | `--cbw-accent` | `ki-information-5` | an operator-actionable configuration fact |
| Access denied | `--cbw-ink-3` | `ki-lock-2` | nothing to fix; the answer is about this person |
| Temporary failure | `--cbw-warn` | `ki-information-4` | may succeed next request |
| Partial | `--cbw-blue-600` | `ki-information-3` | sits **above** real rows |
| Empty | none | caller's icon | unremarkable |

**Unavailable is never collapsed into empty.** That is the requirement, and it is the single defect this whole
state model exists to prevent: a platform that is switched off looking exactly like a quiet week.

### 2.2 The exact not-activated message

> The Communication Platform is not activated in this environment. Mentions appear once
> `AddCommunicationPlatform` is registered and `communication_platform_slice_001.sql` is applied.

Actionable by design — it names the registration **and** the schema slice. "Not available" would not be.

---

## 3. Notifications

Notifications are **not** Communication. They read the platform's own `Notifications` table through
`IWorkspaceNotificationSource`, and they work whether or not Communication is activated.

| State | Trigger |
| --- | --- |
| `Ready` / `Empty` | rows / no rows |
| `AccessDenied` | session has no employee — personal notifications cannot be resolved |
| `Unavailable` | the notification source reports itself unavailable |
| `TemporaryFailure` | the read threw |

Filters (`All` / `Unread`) are **links**, so the state is in the URL and survives a refresh. Expiry
(`ExpiresAt`) is evaluated at **read** time, never by a sweeper — an expiry that depends on a background job
keeps working when the job stops.

Unread is shown as **state**, not as an action — §6.

---

## 4. Nav visibility vs route reachability

| | Mentions |
| --- | --- |
| **Nav entry** | hidden when Communication is not activated — a link that always leads to "not available" trains people to ignore the rail |
| **Route** | `/Workspace/Mentions` stays reachable, and renders the not-activated state |

Deliberate: hiding the route as well would make the state **unreviewable**. A reviewer can open the URL today
and see exactly what a user will see once the platform is switched on.

---

## 5. What flows from Communication, and what does not

| Consumed | Not consumed |
| --- | --- |
| `GetHistoryAsync` — where I was mentioned | comments, threads, participation, reactions, attachments |
| `GetUnreadCountAsync` — badge counts | any Comm table |
| `ViaKind` (direct / team / department) | any Comm write |

`ViaKind` is surfaced because *being named personally* and *being caught by a team mention* are materially
different, and a reader needs to know which one put this in front of them.

---

## 6. Phase 6 — mark-as-read handover

**The endpoint stays withdrawn. This increment does not restore it.**

### 6.1 Why it was removed

R3 shipped `POST /Workspace/MarkNotificationRead`. The repository's Roslyn analyzer rejected it:

```
CBA001: Mutating endpoint 'WorkspaceController.MarkNotificationRead' has no authorization the analyzer
can see and is not listed in authorization-baseline.json. The baseline may only shrink — a new entry is
not an option.
```

The write **was** safe — company **and** recipient were both in the `WHERE` clause, so a crafted id could not
touch another person's row. But that is authorization the analyzer cannot observe, and *"it is safe, trust me"*
is exactly what the rule exists to refuse.

### 6.2 Required authority contract — for TAB 1 to approve

`CrossBuy.Analyzers/AuthorizationSurface.cs` holds `AuthorityTypes`; the analyzer credits an endpoint that
reaches one. That file's own header describes adding to it as a deliberate, visible process — Stage 2A Batch A
did exactly this when the analyzer blocked its three endpoints.

**Proposed contract** (owned by TAB 1, not by this tab):

```csharp
public interface IWorkspaceNotificationAuthority        // add to AuthorityTypes
{
    /// Allows a caller to act on ONE notification row. Returns false unless the row exists,
    /// belongs to context.CompanyId, AND is addressed to context.EmployeeId.
    Task<bool> CanActOnAsync(BusinessContext context, int notificationId, CancellationToken ct = default);
}
```

**Why a new authority rather than an existing one.** A notification is not an Accounting, Inventory, CRM, POS,
HR, Projects, Tasks or Communication record — it is a personal row addressed to one employee. Borrowing any of
the eight module access services would satisfy the analyzer while asserting something false about what was
checked, which is worse than the current gap.

### 6.3 The endpoint, once approved

| Property | Value |
| --- | --- |
| Endpoint | `POST /Workspace/MarkNotificationRead` |
| Body | `id` (int) + anti-forgery token |
| Permission target | the notification row's **recipient** — a personal act on a personal row, not a module permission |
| Company enforcement | `CompanyID == context.CompanyId` **in the `WHERE` clause**, never checked after loading |
| Recipient enforcement | `RecipientEmployeeID == context.EmployeeId` in the same `WHERE` |
| Analyzer recognition | the action body calls `IWorkspaceNotificationAuthority.CanActOnAsync` **before** the write, so the decision is visible to the analyzer |
| Idempotency | already-read returns `false` and writes nothing |
| Scope | sets `IsRead` only. No other column, no other table |

### 6.4 Tests required before it ships

1. A notification belonging to **another company** cannot be marked read.
2. A notification addressed to **another employee** cannot be marked read.
3. An **unresolved company** marks nothing.
4. Marking an already-read row is a **no-op** returning `false`.
5. The write sets `IsRead` and **no other column**.
6. A structural test asserting the endpoint calls the authority **before** `SaveChanges`.
7. The build produces **no CBA001** for the endpoint.

### 6.5 Until then

Unread is **display-only**: a "new" / "جديد" chip plus an unread row tint. No affordance suggests an action
that does not exist — a disabled button the user cannot explain is worse than no button.
