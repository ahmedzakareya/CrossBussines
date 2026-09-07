# 34 — Design Governance

## Ownership

- **Design System Owner:** tokens, components, accessibility and this documentation.
- **Integration Owner:** shared layouts/assets, production token adapter and release gates.
- **Module owner:** workflows, domain semantics, permissions and module-specific screens.
- **Security owner:** authorization surfaces and cross-module aggregation review.
- **Localization/accessibility reviewers:** release parity and WCAG evidence.

Until named people are appointed, no tab may silently assume these roles.

## Change classes

1. Token/system change — platform-wide review and visual-regression plan.
2. Shared component/layout change — Integration + Design System owners.
3. Module screen change — module owner; shared patterns unchanged.
4. Operational exception — domain owner plus documented reason and expiry/review date.
5. Documentation clarification — Design System owner; no semantic change.

## Proposal record

Problem/evidence, users and workflows, existing patterns considered, proposed API/anatomy, variants/states, tokens, responsive/RTL/dark/accessibility behavior, authorization boundary, migration impact, alternatives, screenshots/prototype, tests and owners.

## Release gates

- Uses catalog component or approved exception.
- No unauthorized token/raw-style additions.
- All seven data states where applicable.
- Server authorization and company isolation unchanged/proved.
- Arabic RTL and English LTR screenshots/tests.
- Keyboard, focus, contrast, zoom and screen-reader checks.
- Mobile widths and reduced motion.
- Dark-mode token review even if activation is deferred.
- Visual regression and no layout overflow.
- Shared-file and tab-ownership gates pass.

## Deprecation

Mark component/token deprecated, name the replacement, inventory consumers, provide migration examples, retain for at least one release increment, then remove only when usage reaches zero. Legacy green chrome is deprecated as platform identity but remains supported until its reviewed module migration.

## Enforcement roadmap

Add JSON/CSS token parity validation, lint raw colors/logical-property violations, component demos, accessibility automation, screenshot baselines, Razor analyzer rules for prohibited patterns, and CI reporting. Begin warning-only against legacy debt; enforce errors for new/changed lines.

## Decision log

Material decisions receive stable IDs and link to roadmap/ADR governance. CrossBusiness Blue is approved; the staged implementation of legacy migration remains the safe path. Exceptions never overwrite the system silently.
