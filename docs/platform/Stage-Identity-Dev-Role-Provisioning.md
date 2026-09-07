# Stage — Development Identity Role Provisioning (TAB-1)

**Owner:** Platform / Security / Integration
**Scope:** development environment enablement only — no product feature, no Reporting/Workspace/Tasks change
**Target:** `localhost` · `CrossBuyDev`

---

## 1. Why this exists

`CrossBuyDev` shipped with `AspNetRoles = 0`, `AspNetUserRoles = 0` and no role claims.
`BusinessContextFactory` builds `BusinessContext.Roles` from `ClaimTypes.Role` on the signed-in principal, so
**every** context resolved with an empty role set. Reporting's permission map (`Program.cs`) resolves against role
*names*, so with no roles the entire map matched nobody and no role-gated authorization could be exercised by a
real user. TAB-2's Reporting final closure was blocked on exactly this.

## 2. Canonical role names (read from source, not from a document)

| Permission key | Roles | Source |
|---|---|---|
| `reporting.administer` | Admin, SuperAdmin | `Program.cs:213` |
| `reporting.businessevents.view` | Admin, SuperAdmin, Auditor | `Program.cs:229` |
| `reporting.businessevents.confidential` | Admin, SuperAdmin | `Program.cs:230` |
| `reporting.businessevents.restricted` | SuperAdmin | `Program.cs:231` |

Adjacent vocabularies that must not drift from these:
`PlatformOpsAttribute.AdminRoles` = `Admin, Administrator, SuperAdmin, PlatformOps`;
`GrantAdminIdentityRoles.PlatformSecurity` = `SuperAdmin, PlatformOps`;
`GrantAdminIdentityRoles.CompanyAdministration` = `Admin, Administrator`.

## 3. The mechanism

`GET /api/dev/identity-roles-seed?key=seed123&password=<supplied at call time>`
— one endpoint on the existing `DevSeedController`.

- **Environment-gated.** The class-level `[DevOnly]` returns **404** outside Development. Verified: under
  `ASPNETCORE_ENVIRONMENT=Staging` both this endpoint and `culture-check` answer 404.
- **No credential in the repository.** The password is a required request parameter. There is no hardcoded
  secret and therefore no known-password account discoverable by reading the source.
- **Real Identity APIs.** Roles via `RoleManager<IdentityRole>`, users via `UserManager<Users>.CreateAsync`.
  No password hash is written by hand, so every account signs in through the ordinary
  `AccountController.Login → PasswordSignInAsync` path.
- **Idempotent.** Every step is create-if-absent. A second run reports `already-present` throughout and never
  rewrites an existing user's password. Verified: two runs → exactly one user, one employee and the correct
  role count each.
- **Not a production seeder.** Nothing runs at startup; it is invoked deliberately.

`RoleManager` is resolved from `RequestServices` inside the method rather than added to the controller's
constructor, which several tabs edit concurrently — a 23rd constructor parameter would collide on every merge.

## 4. The user matrix

Four users, each isolating **one** refusing dimension so a denial is provable rather than merely observed.
A roleless outsider would be refused for two reasons at once and would prove neither.

| User | Company | Employee | Dept | Roles | Purpose |
|---|---|---|---|---|---|
| `dev.auditor` | 1 | 2045 | 41 | Auditor | authorized, **not** an administrator — proves the tier boundary |
| `dev.clerk` | 1 | 2046 | 41 | *(none)* | same company + department: cross-owner and write-denial peer |
| `dev.superadmin` | 1 | 2047 | 41 | SuperAdmin | elevated: administer + confidential + restricted |
| `dev.otherco` | 65 | 2048 | — | Auditor | holds the role, different company: isolates **company** as the cause |

`dev.auditor` and `dev.clerk` share department 41 so Team-scope template behaviour is testable (a team peer may
run the team layout but must not edit it).

**The password is not recorded here.** It is held by the owner and passed per invocation.

## 5. Proven at runtime

- All four sign in normally (`{"success":true}`) and receive `.AspNetCore.Identity.Application`.
- `dev.auditor` opens `Platform.BusinessEventLog` (200) where `dev.clerk` gets 404 — identical in every respect
  except role, which proves role claims reach `BusinessContext.Roles`.
- Visibility tiers, exactly: company 1 holds 317 `Internal` + 77 `Confidential` = 394 events.
  `dev.auditor` exports **317** rows; `dev.superadmin` exports **394**.
- Company isolation: `dev.otherco` (company 65) exports **0** rows from the same report.

## 6. Note for whoever seeds employees next

`_LayoutInventory.cshtml:838` renders the avatar as `Url.Content("~" + ProfileImage.Replace("\\","/"))`. Any
non-empty `Employee.ProfileImage` that is not a rooted path throws
`ArgumentException: The path in 'value' must start with '/'` and **500s every page that user opens**. The
convention in existing rows is a leading-slash path or the empty string; the seeder writes empty. This was hit
once during this task with a `"-"` placeholder and cost a full round of false 500s against Reporting.
