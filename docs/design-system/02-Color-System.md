# 02 — Color System

## Brand scale

| Token | Value | Use |
|---|---:|---|
| Blue 900 | `#0B1F3D` | dark shell ground |
| Blue 800 | `#102C55` | elevated dark ground |
| Blue 700 | `#16407C` | strong selected state |
| Blue 600 | `#1F5FD0` | primary action/link/focus |
| Blue 500 | `#3A78E0` | hover and dark-theme action |
| Blue 300 | `#8FB4EE` | decorative/supporting |
| Blue 100 | `#E6EFFC` | selected/soft background |
| Blue 50 | `#F4F8FE` | subtle information surface |

## Semantic palette

Success `#17845C`; warning `#A86A12`; danger `#C22A4D`; information uses Blue 600. Each has a soft background and foreground pair in the tokens. Never assign semantic meaning to brand blue or financial green without a label/icon.

## Neutrals

Ink `#131A26`; secondary ink `#566173`; tertiary ink `#8A94A6`; rule `#DFE4EC`; soft rule `#EEF1F6`; surface white; canvas `#F5F7FB`. Body text must meet WCAG AA against its surface.

## Financial palette

Financial green 900 `#13433A`, green 700 `#1F6253`, green 100 `#E9F1EE`; gold `#D4A017`. Allowed only inside accounting/finance content, profit/loss and financial indicators/reports. Inventory value is not automatically “financial green”; apply it only when presenting an accounting-valued fact.

## Usage rules

- One primary color per screen: Blue 600.
- Status colors appear only with text or icon.
- Destructive actions use danger and require confirmation when irreversible or consequential.
- Charts reserve semantic red/green for meaning, never series decoration.
- No raw hex values in migrated production views; use semantic tokens.
- Disabled state reduces contrast but remains readable; never encode disabled solely with opacity below 0.45.
