# 29 — Form Patterns

Use Bootstrap/Metronic fields, Select2, flatpickr, input groups and validation patterns already shipped. Forms have a clear title, short purpose, logical sections, persistent labels, help where needed, and action area. Required status is explicit; placeholder is never the only label.

Choose native input first; Select2 for searchable/remote/large choices; radio for few exclusive choices; checkbox/switch for independent settings; lookup for business objects. Dates show expected format and locale; monetary inputs show currency; destructive/privileged fields explain impact.

Validate locally for immediacy and server-side for authority. On failure, retain values, show summary, focus first invalid field, and associate inline errors. Loading disables duplicate submit while retaining button text. Success states the resulting object/next action. Warn on navigation with meaningful unsaved changes. Mobile uses appropriate keyboards and avoids multi-column forms; RTL uses logical alignment while codes/numbers remain readable.
