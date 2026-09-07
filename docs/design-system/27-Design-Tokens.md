# 27 — Design Tokens

Tokens are decisions, not aliases for every CSS property.

## Tiers

1. **Primitive:** raw palette, size and duration scales.
2. **Semantic:** action, text, surface, status, focus, financial meaning.
3. **Component:** button/card/dialog values only when the semantic tier cannot express a stable contract.

`29-Tokens.json` is canonical. `28-CSS-Tokens.css` is its reference CSS projection. Future generation must validate name uniqueness, values, light/dark parity and CSS/JSON equivalence before writing.

## Naming

Prefix `cb`; use lower-case dot paths in JSON and `--cb-` kebab case in CSS. Components consume semantic tokens. Production code must not consume primitive color values when a semantic token exists.

## Framework mapping

CrossBusiness tokens feed Bootstrap/Metronic variables: `--bs-primary`, focus, links, body, border and component states. This reverses the current dependency where `crossbuy-brand.css` directly owns framework overrides. The mapping belongs in a future reviewed production adapter, not in individual views.

## Change control

Changing a semantic token is a platform-wide change requiring Design System owner approval, contrast evidence, light/dark and RTL review, visual regression scope, and migration notes. Token deletion requires deprecation for at least one release increment.
