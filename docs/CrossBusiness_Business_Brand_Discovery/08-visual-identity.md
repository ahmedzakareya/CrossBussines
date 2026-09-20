# 08 — Visual identity

Colour, typography and marks, as authored. What the product *does* with them is document 12.

Full data: `brand-tokens.json` (every token, with **computed** contrast), `asset-manifest.csv`
(every mark and font file, hashed and reference-counted), `assets/` (copies).

---

## 1. The token layer

`wwwroot/Backend-assets/css/crossbuy-brand.css` — 1,073 lines, **129 token names / 149
declarations / 114 colour values / 8 gradients**, loaded by 23 views after
`style.bundle(.rtl).css` so it wins the cascade.

This file is a real design system, and an unusually good one: **every role was chosen against a
measured contrast ratio, and the measurement is written into the file next to the value.** All
ratios below were recomputed independently for this discovery (WCAG relative luminance, sRGB) —
they agree with the file's own claims.

### 1.1 The scale

```
 25  #F3F7FF      50  #E7F0FF     100  #D0E1FF     200  #A8C5FF     300  #7DA9FF
400  #5B8CFF     500  #1877F2     600  #166FE5     700  #145DBF     800  #0E4A9E     900  #0B3D91
```

`#1877F2` is Facebook blue. The sheet says so in its first line and overrides Metronic's own blue
across the product.

`25` is an interpolated step below `50`, added because the design uses two levels of near-white
brand tint. It is documented as **a background only, never a text or border colour.**

### 1.2 Roles are not one colour — and this is the system's best idea

The identity blue **fails AA as a button fill**, and the file says so rather than shipping it:

| Role | Token | Value | Measured | Verdict |
|---|---|---|---|---|
| Identity / tint source | `--cb-brand` → `--cb-blue-500` | `#1877F2` | white on it **4.23** | ✗ below AA 4.5 — *never carries small white text* |
| Solid fill, rest | `--cb-brand-fill` → `600` | `#166FE5` | white on it **4.73** | ✓ |
| Solid fill, hover | → `700` | `#145DBF` | white on it **6.27** | ✓ |
| Solid fill, pressed | → `800` | `#0E4A9E` | white on it **8.43** | ✓ |
| Brand ink (text, border) | `--cb-brand-ink` → `800` | `#0E4A9E` | on white **8.43** | ✓ |
| Surface / light tint | `--cb-brand-surface` → `50` | `#E7F0FF` | ink on it **7.35** | ✓ |

So the brand colour is used for *identity* and never for a small white label; a separate, darker
step carries the text. The same failure is recorded in the file for Metronic's own `#1B84FF`
(3.63:1) and is deliberately not re-introduced.

Non-text graphics — charts, illustration — use the identity `#1877F2` directly, because WCAG's
4.5:1 text minimum does not apply to them. That distinction is also written down.

### 1.3 Framework tokens are aliased, not forked

```
--bs-primary      #1877F2      --kt-primary      #1877F2
--bs-link-color   #145DBF   (6.27 on white)
--bs-link-hover   #0E4A9E   (8.43 on white)
```

Bootstrap and Metronic are pointed at the same values, so a component nobody re-styled still
comes out on-brand. This is why the product looks coherent despite the drift in document 12.

### 1.4 Semantic colours, with the same discipline

| | Base | On white | Subtle bg | Text emphasis | On white |
|---|---|---:|---|---|---:|
| Success | `#16A34A` | 3.30 | `#DCFCE7` | `#14532D` | **9.11** |
| Warning | `#F59E0B` | 2.15 | `#FEF3C7` | `#92400E` | **7.09** |
| Danger | `#EF4444` | 3.76 | `#FEE2E2` | `#991B1B` | **8.31** |
| Info | `#3B82F6` | 3.68 | `#DBEAFE` | `#1E40AF` | **8.72** |

The bases are chart/fill colours; none of them is used for small text. A parallel `-text-*` token
carries the legible variant, and a `-strong` family sits between:

```
--cb-success-strong  #15803D  5.02      --cb-success-stronger #166534  7.13
--cb-warning-strong  #B45309  5.02
--cb-danger-strong   #DC2626  4.83
--cb-info-strong     #2563EB  5.17      --cb-info-stronger    #1D4ED8  6.70
```

**The warning inverse is the tell.** `--bs-warning-inverse: #071437` — dark ink, 18.04:1 on
white — rather than white. The file records the reasoning: Bootstrap ships `#000` for
`.btn-warning`, and a white label on amber measures 1.69 (fail) while dark ink measures **10.70**
(pass). Amber keeps its fill; only the label changes.

### 1.5 Dark theme

Overrides are guarded as `:root:not([data-bs-theme="dark"])` so a light-mode rule cannot leak into
dark. `brand-tokens.json` records every token's dark-theme override alongside its light value, so
the two are never confused.

---

## 2. The marks — and what they reveal

### 2.1 The logo is blue **and amber**

```svg
<!-- crossbuy-logo.svg -->
<text … fill="#0E4A9E">Cross<tspan fill="#F59E0B">Buy</tspan></text>
```

**"Cross" is brand ink `#0E4A9E`. "Buy" is `#F59E0B` — amber.**

The dark-panel variant uses white + `#FBBF24`. The favicon is a four-square grid alternating
`#0E4A9E`/`#1877F2` blue with `#FBBF24` amber. The mark itself — a rounded tile of stacked
ledger boxes — puts an amber box top-left in every version.

This reconciles something that otherwise looks like an inconsistency. **Amber is not a rogue
colour in CrossBuy; it is half the wordmark.** `--bs-warning: #F59E0B` is the logo's accent,
reused as the semantic warning. The standing rule against amber applies to *action buttons*, where
it fails contrast and misreads as a warning — not to the identity.

A brand system should say this explicitly, because right now the accent colour and the warning
colour are the same value, and nothing in the token layer distinguishes them. A reader painting a
"brand accent" element and a reader painting a "caution" element both reach for `#F59E0B`.

### 2.2 The logo uses live text in a font it cannot guarantee

```svg
font-family="Segoe UI, Tahoma, Arial, sans-serif"
```

Both SVG logos render the wordmark as **`<text>`, not outlines**, in a Windows system font that is
**neither Cairo nor Inter** — the two faces the product actually uses. On any machine without
Segoe UI (macOS, Linux, many phones) the wordmark silently becomes Tahoma or Arial: different
letterforms, different width, different feel.

The PNG lockups (1744×394) are outlines and do not have this problem. The SVGs do, and they are
the ones used in the UI. This is the single most fixable brand-integrity defect found.

### 2.3 The asset set

| Group | Files |
|---|---|
| **Primary PNG lockups** | `crossbuy-logo-full.png` 1744×394 · `crossbuy-logo-lockup.png` 1744×356 · `crossbuy-logo.png` 367×250 · `crossbuy-logo-mark.png` 367×227 · `crossbuy-mark.png` 193×192 |
| **SVG** | `crossbuy-logo.svg` (176×34) · `crossbuy-logo-light.svg` (210×44) · `crossbuy-favicon.svg` (64×64) |
| **Favicons** | `favicon.ico` · `favicon.png` 1024×1158 |
| **Module icons** | `icon-accounting` · `icon-crm` · `icon-hr` · `icon-inventory` · `icon-manufacturing` · `icon-pos` · `icon-projects` · `icon-reports` — **the eight chips on the sign-in screen** |
| **Sign-in art** | `login-scene.png` 1536×1024 · `login-background.png` 865×270 |
| **Email** | `logo-1.svg` · `logo-2.svg` (outlines, no live text) |
| **Storefront** | a separate set under `wwwroot/assets/imgs/theme/` and `wwwroot/assets-en/imgs/theme/` — `logo.svg`, `logo-2.svg`, `logo-light.svg`, `favicon.svg`, all 215×66 |

**`favicon.png` is 1024×1158** — not square, and 253 KB for a favicon. A browser will letterbox or
squash it.

**The storefront has its own logo set at a different aspect ratio** (215×66 vs the backend's
176×34), duplicated across an Arabic and an English asset tree. Two logo systems, four copies.

`asset-manifest.csv` records **102 of 171 catalogued assets with zero references** found in the
view, controller, CSS and JS tree. Some of those are genuinely unused; some are referenced by a
path this scan did not reconstruct. Treat it as a list to audit, not a list to delete.

---

## 3. Typography

### 3.1 Two families, three sources

**Cairo** — the primary face, Arabic and Latin. **Inter** — the Latin companion.

| Where | What |
|---|---|
| Bundled static | `wwwroot/Backend-assets/fonts/cairo/Cairo-{Regular,SemiBold,Bold,ExtraBold}.ttf` — 4 weights, ~91–95 KB each |
| Embedded in the assembly | `BL/Reporting/Fonts/Cairo-Regular.ttf`, `Cairo-Bold.ttf` + `OFL.txt`, via `<EmbeddedResource Include="BL\Reporting\Fonts\*.ttf" />` |
| Remote | `fonts.googleapis.com` — Cairo `wght@400;500;600;700;800` and Inter, requested by every layout and several standalone pages |

`font-family: 'Cairo', sans-serif` is applied in nine layouts: `_LayoutAccounting`,
`_LayoutBackend`, `_LayoutEmbed`, `_LayoutHyperPos`, `_LayoutInventory`, `_LayoutManufacturing`,
`_LayoutPeople`, `_LayoutPos`, `_LayoutPosApp`. Several gate it on `html[lang="ar"]`.

**Cairo is licensed under the SIL Open Font License** (`OFL.txt` ships beside the embedded copies),
so bundling and embedding are permitted.

### 3.2 The PDF constraint, written down

`ReportFontLibrary` records that Chromium's print-to-PDF **does not embed a *variable* Cairo TTF**
and silently falls back to SegoeUI-Bold. Static faces are embedded instead. This is why there are
two copies of Cairo in the repository, and it is a correct decision, not duplication.

The companion rule from the same area: a report's font stack must be **dual-script**. A Latin-first
stack, a null template font, or CSS in a `style` attribute each split a bilingual document across
two typefaces.

### 3.3 Verified at runtime

On the Arabic pass, `document.fonts.check('600 16px Cairo')` returned **`true`** on every screen,
and the loaded families were `Cairo`, `Inter`, `keenicons-outline`. The body stack resolved to
`Cairo, sans-serif` on application screens and
`Cairo, "Segoe UI", Tahoma, sans-serif` on the sign-in screen.

> **But:** the capture also recorded
> `net::ERR_ABORTED https://fonts.gstatic.com/s/inter/…UcC73FwrK3iLTeHuS_nVMrMxCp50SjIa1ZL7.woff2`
> — **an Inter weight failed to load from Google.** Cairo is bundled and survives; Inter is
> remote-only and does not. On a machine with no internet, or behind a firewall that blocks
> `fonts.gstatic.com`, the Latin face falls back silently.
>
> For an on-premise ERP — which is what CrossBuy is — a remote font dependency is a real
> deployment risk, and the product already knows how to solve it: bundle Inter the way Cairo is
> bundled.

---

## 4. Iconography

**Keenicons**, Metronic's set, in `ki-outline` and `ki-solid` variants. The menu model carries an
icon per category (`ki-abstract-26`, `ki-element-11`, `ki-chart-simple`, `ki-people`,
`ki-calendar-tick`, `ki-setting-2`, …), so navigation iconography is declared in one file
alongside the labels.

The webfont `keenicons-outline.ttf` also failed to load on one capture pass
(`net::ERR_ABORTED …keenicons-outline.ttf?fzo4bm`) while succeeding on the next — a local asset,
so probably a cold-start race rather than a defect, but worth a second look.

---

## 5. Summary judgement

**The token layer is the strongest artefact in this discovery.** It is better than most commercial
design systems: measured, reasoned, documented in place, with roles split by function rather than
by name, framework tokens aliased rather than forked, and dark mode guarded against leakage.

Three things hold it back, all addressable:

1. **The logo's wordmark is live text in a font the product does not ship.**
2. **The brand accent and the warning semantic are the same hex**, with nothing distinguishing
   them — which is exactly how an amber action button gets built by someone acting in good faith.
3. **Inter is loaded from the internet**, so the Latin half of the typography is not guaranteed on
   an on-premise install. Cairo already shows the fix.
