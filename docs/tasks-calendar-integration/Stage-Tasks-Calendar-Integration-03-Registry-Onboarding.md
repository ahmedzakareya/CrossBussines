# Stage-Tasks-Calendar-Integration-03 — Registry Onboarding Contract

**Delivered as a contract; implementation externally pending.**
`CrossBuy/BL/TasksCalendar/TaskCalendarIntegrationContracts.cs`

---

## 1. Ownership — why this is a request and not an edit

`BL/Platform/EntityRegistry.cs` is Platform Kernel property. This tab did not edit it.

The request is expressed as **data of the kernel's own type** (`EntityDefinition`) plus the policy metadata that type
does not carry. So the kernel adopts it by moving a value, not by re-deriving a design — and a test can assert the
request's shape today, before anyone has acted on it.

`RegistryOnboardingRequest.ExternallyPending = true` marks that state in code rather than in prose.

## 2. `Task`

| Field | Value | Reason |
|---|---|---|
| `Code` | `Task` | matches the `TaskItem.EntityType` conventions already in use |
| `Module` | `Tasks` | |
| `RouteTemplate` | `/Tasks/Index` | no per-task detail screen exists — the same situation as `PosOrder`, which the registry already carries as reference-only |
| `SupportsSearch` / `Timeline` / `Comments` / `Files` / `Followers` | all true | a task is the most natural comment and attachment target in the product; a supervisor follows without being the assignee |
| **`PermissionScope`** | **`Tasks`** | **not `None`** — see §4 |
| `ListedInRecordPicker` | true | task-to-task linking, and construction activities later |
| `ReadAccessResolver` | `ITasksAccessService.CanAsync(context, Read, ForTask(id))` | named so the kernel does not have to guess |
| `PrivacyCeiling` | `InternalOnly` | a ceiling, not a default |
| Retention / Audit | placeholders, non-null | the policy is not this tab's to decide, and a null would read as "no policy needed" |

**Capabilities at onboarding:** Mentions **enabled**, Attachments **enabled**, Timeline **enabled**,
Comments **PendingAccessProof**, Public visibility **Forbidden**, External principal access **Forbidden**.

## 3. `CalendarEvent`

| Field | Value | Reason |
|---|---|---|
| `Code` | `CalendarEvent` | |
| `SupportsFollowers` | **false** | attendees already *are* the follower set; a second concept would diverge from the attendee list the moment either changed |
| `PermissionScope` | `None` | Calendar has **no** access service. Its real rule is record-level — organiser ∪ company-scope ∪ attendee — and already lives in `CalendarService.Visible()`. Inventing a module scope here would create an authorization surface nobody owns. |
| `ReadAccessResolver` | `CalendarService.Visible(companyId, employeeId)` | the rule that already exists, named |

**Capabilities:** Mentions **enabled**, Timeline **enabled**, Comments **PendingAccessProof**,
Attachments **PendingAccessProof**, Public visibility **Forbidden**, External principals **Forbidden**.

**An attendee email is not an authenticated platform principal.** External attendees are deferred pending an
`ExternalPrincipalContext`, and that is stated in the request's own `Rationale`, not just here.

## 4. Fail closed — the two places it matters

**Comments stay `PendingAccessProof` on both entities.** A comment surface is a *read* surface: enabling it before
the per-entity read resolver is wired and tested would open a door the access service has not been asked about.
Asserted by `Comment_surfaces_stay_closed_until_an_access_proof_exists`.

**`Task.PermissionScope` must be `Tasks`, never `None`.** The registry currently carries `Project` with
`PermissionScope = ScopeNone` even though `ProjectsAccessService` exists — a gap this tab reported in the
construction track. Registering `Task` the same way would make the registry the loosest door into a module that
already has a lock. Asserted by `The_task_registration_request_is_scoped_and_never_permissive`.

## 5. Tests

`TasksCalendarContractTests` — scoped-and-never-permissive, comment surfaces closed, calendar defers external
attendees and claims no module scope.

## 6. What the Platform Kernel is asked to do

1. Add both definitions from `TaskCalendarRegistryOnboarding.All()`.
2. Keep `PermissionScope = "Tasks"` on `Task`.
3. Leave Comments **off** for both until the read resolvers are wired — the flags are requests, and turning them all
   on at once would enable a surface nobody has proven.
